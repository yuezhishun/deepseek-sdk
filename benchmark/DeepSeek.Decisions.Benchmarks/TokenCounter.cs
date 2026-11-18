using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace DeepSeek.Decisions.Benchmarks;

// 使用官方 BPE 词表与三段 Isolated Split；评测运行不依赖 Python。
internal sealed class TokenCounter
{
    internal const string Source = "https://cdn.deepseek.com/api-docs/deepseek_v4_tokenizer.zip";
    private readonly BpeTokenizer _bpe;
    private readonly Regex[] _splits;
    private readonly Regex _special;
    private readonly Dictionary<string, int> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ordinaryCache = new(StringComparer.Ordinal);
    internal TokenCounter(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        using var reader = new StreamReader(zip.GetEntry("deepseek_v4_tokenizer/tokenizer.json")!.Open());
        var root = JsonNode.Parse(reader.ReadToEnd())!;
        if (root["model"].S("type") != "BPE" || root["normalizer"]!["normalizers"].Items().Any()) throw new InvalidDataException("不支持此 tokenizer 配置。");
        var vocabulary = root["model"]!["vocab"]!.AsObject().Select(p => KeyValuePair.Create(p.Key, (int)p.Value!.N())).ToArray();
        var specials = root["added_tokens"].Items().ToDictionary(t => t.S("content"), t => (int)t.N("id"));
        _bpe = BpeTokenizer.Create(new BpeOptions(vocabulary)
        {
            ByteLevel = true,
            Merges = root["model"]!["merges"].Items().Select(m => m is JsonArray ? string.Join(' ', m.Items().Select(v => v.S())) : m.S()).ToArray(),
            SpecialTokens = specials,
            PreTokenizer = null,
        });
        var preprocessors = root["pre_tokenizer"]!["pretokenizers"].Items().ToArray();
        if (preprocessors.Length != 4 || preprocessors[^1].S("type") != "ByteLevel" || preprocessors[^1].B("use_regex")) throw new InvalidDataException("官方预分词配置发生变化。");
        _splits = preprocessors.Take(3).Select(p => new Regex(p["pattern"]!.S("Regex"), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))).ToArray();
        _special = new Regex(string.Join('|', specials.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }
    internal int Count(string text)
    {
        if (_cache.TryGetValue(text, out var cached)) return cached;
        var count = 0; var offset = 0;
        foreach (Match match in _special.Matches(text))
        {
            count += Ordinary(text[offset..match.Index]); count++; offset = match.Index + match.Length;
        }
        count += Ordinary(text[offset..]);
        if (text.Length <= 2048 && _cache.Count < 50000) _cache[text] = count;
        return count;
    }
    private int Ordinary(string text)
    {
        if (text.Length == 0) return 0;
        if (_ordinaryCache.TryGetValue(text, out var cached)) return cached;
        var segments = new List<string> { text };
        foreach (var regex in _splits)
        {
            var next = new List<string>();
            foreach (var segment in segments)
            {
                var position = 0;
                foreach (Match match in regex.Matches(segment))
                {
                    if (match.Index > position) next.Add(segment[position..match.Index]);
                    if (match.Length > 0) next.Add(match.Value);
                    position = match.Index + match.Length;
                }
                if (position < segment.Length) next.Add(segment[position..]);
            }
            segments = next;
        }
        var count = segments.Sum(s => _bpe.CountTokens(s, considerPreTokenization: false, considerNormalization: false));
        if (_ordinaryCache.Count < 50000) _ordinaryCache[text] = count;
        return count;
    }
    internal long Messages(JsonNode request)
    {
        var text = new StringBuilder("<｜begin▁of▁sentence｜>");
        var systems = request["messages"].Items().Where(m => m.S("role") == "system").Select(Content);
        text.Append(string.Join("\n\n", systems));
        foreach (var message in request["messages"].Items().Where(m => m.S("role") != "system"))
        {
            text.Append(message.S("role") == "user" ? "<｜User｜>" : "<｜Assistant｜>"); text.Append(Content(message));
        }
        text.Append("<｜Assistant｜>");
        return Count(text.ToString());
    }
    private static string Content(JsonNode message) => message["content"] is JsonArray parts ? string.Concat(parts.Items().Select(p => p.S("text"))) : message.S("content");
}
