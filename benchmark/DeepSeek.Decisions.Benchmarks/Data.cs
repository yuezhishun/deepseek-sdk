using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parquet.Serialization;

namespace DeepSeek.Decisions.Benchmarks;

internal static class J
{
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };
    internal static JsonNode? Copy(this JsonNode? node) => node?.DeepClone();
    internal static string S(this JsonNode? node, string key = "", string fallback = "") => (key.Length == 0 ? node : node?[key])?.ToString() ?? fallback;
    internal static double N(this JsonNode? node, string key = "", double fallback = 0) => double.TryParse(node.S(key), CultureInfo.InvariantCulture, out var n) ? n : fallback;
    internal static bool B(this JsonNode? node, string key = "") => bool.TryParse(node.S(key), out var b) && b;
    internal static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];
    internal static JsonObject O(params (string Key, object? Value)[] values) => new(values.Select(v => KeyValuePair.Create(v.Key, v.Value is JsonNode n ? n.Copy() : JsonSerializer.SerializeToNode(v.Value, Options))));
    internal static JsonArray A(IEnumerable<JsonNode?> nodes) => new(nodes.Select(Copy).ToArray());
    internal static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key, Options) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number => e.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        _ => node?.ToJsonString(Options) ?? "null",
    };
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Hash(JsonNode? node) => Hash(Canonical(node));
    internal static async Task Write(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, value.ToJsonString(new(Options) { WriteIndented = true }), Encoding.UTF8);
        File.Move(temp, path, true);
    }
    internal static string Html(string text) => WebUtility.HtmlEncode(text);
}

// 一份冻结工作流的数据与已发布运行。沿用 Parquet 的 JSON 结构，避免复制协议 DTO。
internal sealed class WorkflowData(string name, string revision, JsonObject manifest, List<JsonObject> cases, List<JsonObject> questions, List<JsonObject> runs)
{
    internal string Name { get; } = name;
    internal string Revision { get; } = revision;
    internal JsonObject Manifest { get; } = manifest;
    internal List<JsonObject> Cases { get; } = cases;
    internal List<JsonObject> Questions { get; } = questions;
    internal List<JsonObject> Runs { get; } = runs;
    internal string[] Policies => Manifest["policies"].Items().Select(p => p.S("policy_id")).ToArray();
    internal string JevRun => Manifest["runs"]!.AsObject().Single(p => p.Value!["model"].S("provider") == "typesafe").Key;
    internal IEnumerable<JsonObject> JevCases => Runs.Where(r => r.S("run_id") == JevRun);
    internal IEnumerable<JsonNode> InputQuestions(string caseId) => Questions.Where(q => q.S("case_id") == caseId).Cast<JsonNode>()
        .Concat(Runs.Where(r => r.S("case_id") == caseId).SelectMany(r => r["questions"].Items()));
    internal JsonNode? InputState(string caseId, string node) => InputQuestions(caseId).FirstOrDefault(q => q.S("node_id") == node) is { } q ? JsonNode.Parse(q.S("state_json")) : null;
}

internal static class Data
{
    internal const string CodeRevision = "0ac3b8ad845429f0d8e064ecfb2430a47c5a25cb";
    internal static readonly (string Name, int Count, string Revision)[] Specs =
    [
        ("invoice_processing", 150, "6beeb2d2acd65c086c835022f5f4d7434114cafc"),
        ("customer_service", 204, "b1342f5a704587dbc465867c38c2694348ff86e4"),
        ("agent_trace_observability", 111, "8635540973910a92465fe2bc53e195375aa6e1a8"),
        ("security_incidents", 240, "fbe1ea5c69cf494157fd23f2002a0d9d9a418443"),
    ];
    internal static string Repo(string name) => "typesafeAI/evalsafe-" + name.Replace('_', '-');
    internal static async Task Download(HttpClient http, string url, string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".download";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using (var output = File.Create(temp)) await response.Content.CopyToAsync(output);
        File.Move(temp, path, true);
    }
    internal static async Task Prepare(string root)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var sources = new JsonArray();
        foreach (var spec in Specs)
        {
            var files = new JsonArray();
            foreach (var file in new[] { "dataset.json", "README.md", "data/cases.parquet", "data/questions.parquet", "data/run_results.parquet" })
            {
                var url = $"https://huggingface.co/datasets/{Repo(spec.Name)}/resolve/{spec.Revision}/{file}";
                var path = Path.Combine(root, "data", spec.Name, file);
                await Download(http, url, path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                files.Add(J.O(("file", file), ("url", url), ("sha256", hash), ("bytes", new FileInfo(path).Length)));
            }
            sources.Add(J.O(("workflow", spec.Name), ("repo", Repo(spec.Name)), ("revision", spec.Revision), ("files", files)));
            Console.WriteLine($"已准备 {spec.Name}：{spec.Count} 个案例。");
        }
        await Download(http, TokenCounter.Source, Path.Combine(root, "tokenizer.zip"));
        await J.Write(Path.Combine(root, "sources.json"), J.O(("code_revision", CodeRevision), ("datasets", sources)));
        var data = await Load(root);
        var validation = Scoring.Validate(data);
        await J.Write(Path.Combine(root, "validation.json"), validation);
        Console.WriteLine(validation.ToJsonString(J.Options));
    }
    internal static async Task<List<WorkflowData>> Load(string root)
    {
        var sources = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sources.json")))!;
        if (sources.S("code_revision") != CodeRevision) throw new InvalidDataException("官方代码版本不符。");
        var result = new List<WorkflowData>();
        foreach (var spec in Specs)
        {
            var source = sources["datasets"].Items().Single(s => s.S("workflow") == spec.Name);
            if (source.S("revision") != spec.Revision) throw new InvalidDataException("数据版本不符。");
            foreach (var file in source["files"].Items())
            {
                var path = Path.Combine(root, "data", spec.Name, file.S("file"));
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                if (hash != file.S("sha256")) throw new InvalidDataException($"文件校验失败：{path}");
            }
            var folder = Path.Combine(root, "data", spec.Name);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "dataset.json")))!.AsObject();
            result.Add(new(spec.Name, spec.Revision, manifest,
                await Read(Path.Combine(folder, "data/cases.parquet")),
                await Read(Path.Combine(folder, "data/questions.parquet")),
                await Read(Path.Combine(folder, "data/run_results.parquet"), manifest["runs"]!.AsObject().Single(p => p.Value!["model"].S("provider") == "typesafe").Key)));
        }
        return result;
    }
    private static async Task<List<JsonObject>> Read(string path, string? runId = null)
    {
        await using var stream = File.OpenRead(path);
        var (_, rows, _) = await ParquetSerializer.DeserializeUntypedAsync(stream);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode? Node(object? value)
        {
            if (value is string text)
            {
                if (text.Length > 256)
                {
                    if (strings.TryGetValue(text, out var existing)) text = existing;
                    else strings.Add(text, text);
                }
                return JsonValue.Create(text);
            }
            if (value is IDictionary<string, object> map) return new JsonObject(map.Select(p => KeyValuePair.Create(p.Key, Node(p.Value))));
            if (value is System.Collections.IEnumerable list) return new JsonArray(list.Cast<object?>().Select(Node).ToArray());
            return JsonSerializer.SerializeToNode(value, J.Options);
        }
        return rows.Where(r => runId is null || r["run_id"]?.ToString() == runId).Select(r => Node(r)!.AsObject()).ToList();
    }
}
