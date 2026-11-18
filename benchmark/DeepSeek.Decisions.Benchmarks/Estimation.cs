using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Estimation
{
    internal const string PriceSource = "https://api-docs.deepseek.com/quick_start/pricing/";
    internal const string PriceDate = "2026-11-11";
    internal static double Cost(long input, long output, bool peak = true, long cached = 0) => (Math.Max(0, input - cached) * (peak ? .30 : .15) + cached * (peak ? .006 : .003) + output * (peak ? 1.20 : .60)) / 1_000_000;
    internal static async Task<TokenCounter> LoadCounter(string root)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var path = Path.Combine(root, "tokenizer.zip");
        await Data.Download(http, TokenCounter.Source, path);
        return new(path);
    }
    internal static SystemOneRequest Request(JsonNode state, JsonObject questions) => new()
    {
        State = JsonSerializer.SerializeToElement(state, J.Options),
        Questions = questions.ToDictionary(p => p.Key, p => JsonSerializer.Deserialize<SystemOneQuestion>(p.Value!.ToJsonString())!),
    };
    // 唯一传输是内存 handler：即使配置了真实 API key，此入口也不会请求模型。
    internal static async Task<JsonNode> Capture(JsonNode state, string id, JsonNode question, string method, bool correction = false)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new DeepSeekClient("offline-no-api-key", new DeepSeekClientOptions
        {
            Endpoint = new Uri("https://offline.invalid/v1"), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0),
        }).GetDecisionsClient("deepseek-flash");
        try { await client.SystemOneAsync(Request(state, J.O((id, question))), Options(method)); }
        catch (InvalidOperationException) when (handler.Body is not null) { }
        var body = handler.Body ?? throw new InvalidOperationException("未捕获到 SDK 请求。");
        if (correction)
        {
            var content = body["messages"]![0]!.S("content");
            var format = content[content.IndexOf("Return only one JSON object", StringComparison.Ordinal)..];
            body["messages"]!.AsArray().Add(J.O(("role", "user"), ("content", "The output format must be followed exactly. " + format)));
        }
        return body;
    }
    internal static DecisionOptions Options(string method = "auto") => new() { Method = method, MaxConcurrency = 8, MaxTokens = 4096, Temperature = 0 };
    internal static async Task<JsonObject> Estimate(string root, List<WorkflowData> datasets)
    {
        var counter = await LoadCounter(root);
        var replay = await Workflows.Replay(datasets);
        await J.Write(Path.Combine(root, "replay.json"), replay);
        var rows = new JsonArray();
        foreach (var data in datasets)
        {
            long normalInput = 0, normalOutput = 0, conservativeInput = 0, conservativeOutput = 0, normalCalls = 0, conservativeCalls = 0;
            foreach (var c in data.JevCases)
            {
                foreach (var q in c["questions"].Items())
                {
                    var state = JsonNode.Parse(q.S("state_json"))!; var question = JsonNode.Parse(q.S("question_json"))!;
                    var options = question["criteria"] is JsonObject criteria ? criteria.Count : question["criteria"] is JsonArray levels ? levels.Count : 2;
                    var eligible = question.S("type") != "choice" || options <= 26;
                    var structured = await Capture(state, q.S("question_id"), question, "structured");
                    var structuredInput = counter.Messages(structured);
                    var correctedInput = counter.Messages(await Capture(state, q.S("question_id"), question, "structured", true));
                    var structuredOutput = question.S("type") == "noul" ? 16 : Math.Min(4096, 64 + options * 16);
                    long probeInput = 0;
                    if (eligible) probeInput = counter.Messages(await Capture(state, q.S("question_id"), question, "logprobs"));
                    normalInput += eligible ? probeInput : structuredInput; normalOutput += eligible ? 2 : structuredOutput; normalCalls++;
                    conservativeInput += (long)Math.Ceiling((probeInput + structuredInput + correctedInput + 60) * 1.2);
                    conservativeOutput += (eligible ? 2 : 0) + 2 * structuredOutput; conservativeCalls += eligible ? 3 : 2;
                }
            }
            rows.Add(J.O(("workflow", data.Name), ("cases", data.Cases.Count), ("policies", data.Policies.Length),
                ("normal", Scenario(normalCalls, normalInput, normalOutput)), ("conservative", Scenario(conservativeCalls, conservativeInput, conservativeOutput))));
            Console.WriteLine($"已估算 {data.Name}：{normalCalls} 题；无缓存低峰 ${Cost(normalInput, normalOutput, false):F4}，高峰 ${Cost(normalInput, normalOutput):F4}。");
        }
        JsonObject Total(string scenario) => Scenario((long)rows.Items().Sum(r => r[scenario].N("calls")), (long)rows.Items().Sum(r => r[scenario].N("input_tokens")), (long)rows.Items().Sum(r => r[scenario].N("output_tokens")));
        var manifestHash = J.Hash(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sources.json"))));
        var tokenizerHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "tokenizer.zip"))));
        var estimate = J.O(("status", "awaiting_budget_confirmation"), ("model", "deepseek-flash"), ("method", "auto"), ("created_at_utc", DateTimeOffset.UtcNow.ToString("O")), ("source_manifest_sha256", manifestHash),
            ("pricing", J.O(("date", PriceDate), ("url", PriceSource), ("peak_input_per_million", .30), ("peak_output_per_million", 1.20), ("offpeak_input_per_million", .15), ("offpeak_output_per_million", .60))),
            ("tokenizer", J.O(("url", TokenCounter.Source), ("sha256", tokenizerHash))),
            ("workflows", rows), ("normal_total", Total("normal")), ("conservative_total", Total("conservative")),
            ("assumptions", new[] { "使用官方 Jev 公开执行路径逐题估算；DeepSeek 可能走不同分支，因此区间不是账单上限。", "正常情景：26项以内可读 Logprobs，每题2个输出token；宽选项使用Structured。", "保守情景：每题Structured及一次格式纠正，适用题另计Logprobs探测；输入额外增加20%和每次20token包装余量。", "Structured输出按每个选项16token加64token估算，Noul为16token；配置MaxTokens=4096。", "使用公开V4 tokenizer和文本chat模板；服务端Flash版本与包装可能不同，最终以usage和账单为准。", "两种情景均按全部输入缓存未命中计价；缓存命中会降低实际费用。", "按当前正常路径计算的MaxTokens输出上限也单列，不代表预期费用。", "费用仅含DeepSeek模型API；数据下载、本地计算与人工移植不计入。", "尚未调用DeepSeek或TypeSafe模型API；必须确认预算后显式运行run。" }));
        await J.Write(Path.Combine(root, "estimate.json"), estimate);
        await File.WriteAllTextAsync(Path.Combine(root, "estimate.html"), Html(estimate));
        return estimate;
    }
    private static JsonObject Scenario(long calls, long input, long output) => J.O(("calls", calls), ("input_tokens", input), ("output_tokens", output), ("offpeak_usd", Cost(input, output, false)), ("peak_usd", Cost(input, output)), ("max_tokens_output_peak_usd", Cost(input, checked(calls * 4096))));
    private static string Html(JsonObject estimate)
    {
        var rows = string.Concat(estimate["workflows"].Items().Select(r => $"<tr><td>{J.Html(r.S("workflow"))}</td><td>{r.N("cases")}</td><td>{r["normal"].N("calls"):N0}</td><td>${r["normal"].N("offpeak_usd"):F4}–${r["normal"].N("peak_usd"):F4}</td><td>${r["conservative"].N("offpeak_usd"):F4}–${r["conservative"].N("peak_usd"):F4}</td></tr>"));
        var assumptions = string.Concat(estimate["assumptions"].Items().Select(a => $"<li>{J.Html(a.S())}</li>"));
        return $"<!doctype html><html lang=zh-CN><meta charset=utf-8><title>WorkflowEvals 费用预估</title><style>body{{font:16px/1.6 system-ui;max-width:1100px;margin:40px auto;padding:20px;color:#18212b}}table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #cbd5e1;padding:10px;text-align:left}}th{{background:#edf3f8}}strong{{color:#045b4d}}</style><h1>DeepSeek.Decisions 官方 WorkflowEvals 费用预估</h1><p><strong>等待预算确认，模型调用数：0。</strong>705 个案例，4 个工作流；deepseek-flash / auto。</p><p>价格快照：{PriceDate}，<a href='{PriceSource}'>官方价格</a>。下表均按无缓存计价，单位 USD。</p><table><tr><th>工作流</th><th>案例</th><th>正常路径题目数</th><th>正常情景：低峰–高峰</th><th>保守情景：低峰–高峰</th></tr>{rows}<tr><th>合计</th><td>705</td><td>{estimate["normal_total"].N("calls"):N0}</td><td><strong>${estimate["normal_total"].N("offpeak_usd"):F4}–${estimate["normal_total"].N("peak_usd"):F4}</strong></td><td><strong>${estimate["conservative_total"].N("offpeak_usd"):F4}–${estimate["conservative_total"].N("peak_usd"):F4}</strong></td></tr></table><h2>计算依据和边界</h2><ul>{assumptions}</ul><p>正常路径每次请求均生成4096个输出token时，高峰费用约 ${estimate["normal_total"].N("max_tokens_output_peak_usd"):F2}；这是输出上限情景。</p><p>详细token统计、源文件和tokenizer校验值见同目录 estimate.json、sources.json、validation.json、replay.json。</p></html>";
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal JsonNode? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            throw new InvalidOperationException("离线捕获请求，禁止发送模型调用。");
        }
    }
}
