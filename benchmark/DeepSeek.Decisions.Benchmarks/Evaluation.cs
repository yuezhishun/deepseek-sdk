using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Evaluation
{
    internal static async Task<int> Run(string root, string name, double budget, bool retryErrors)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("运行名称必须是单个合法目录名。");
        var estimatePath = Path.Combine(root, "estimate.json");
        if (!File.Exists(estimatePath)) throw new InvalidOperationException("请先运行 prepare 和 estimate 并确认预算。");
        var estimate = JsonNode.Parse(await File.ReadAllTextAsync(estimatePath))!;
        var datasets = await Data.Load(root);
        if (estimate.S("source_manifest_sha256") != J.Hash(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sources.json"))))) throw new InvalidDataException("估价与当前数据清单不符，请重新估价。");
        Scoring.Validate(datasets);
        await Workflows.Replay(datasets);
        var counter = await Estimation.LoadCounter(root);
        var tokenizerHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "tokenizer.zip"))));
        if (tokenizerHash != estimate["tokenizer"].S("sha256")) throw new InvalidDataException("tokenizer 与估价不符，请重新估价。");
        var config = Configuration(root, estimate);
        var folder = Path.Combine(root, "runs", name);
        Directory.CreateDirectory(folder);
        await using var runLock = new FileStream(Path.Combine(folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifestPath = Path.Combine(folder, "manifest.json");
        var hash = J.Hash(config);
        if (File.Exists(manifestPath))
        {
            if (JsonNode.Parse(await File.ReadAllTextAsync(manifestPath)).S("configuration_hash") != hash) throw new InvalidOperationException("此运行属于不同的数据、模型、参数或实现，请使用不同的 --name。");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any(p => Path.GetFileName(p) != ".lock")) throw new InvalidOperationException("目录非空但缺少 manifest，拒绝覆盖。");
            await J.Write(manifestPath, J.O(("configuration_hash", hash), ("configuration", config), ("created_at_utc", DateTimeOffset.UtcNow.ToString("O"))));
        }
        var ledger = await RunLedger.Open(Path.Combine(folder, "ledger.json"), budget);
        var apiKey = ReadKey();
        var pending = new List<(WorkflowData Data, JsonObject Case, string Path)>();
        foreach (var data in datasets)
            foreach (var c in data.Cases)
            {
                var path = CasePath(folder, data.Name, c.S("case_id"));
                if (File.Exists(path))
                {
                    var previous = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                    if (previous.S("case_hash") != J.Hash(JsonNode.Parse(c.S("input_json")))) throw new InvalidDataException("案例检查点与输入不符。");
                    if (previous.S("status") == "ok" || previous.S("status") == "error" && !retryErrors) continue;
                }
                pending.Add((data, c, path));
            }
        Console.WriteLine($"待运行 {pending.Count} 案例；预算 ${budget:F2}，已知费用高峰估计 ${ledger.Spent:F4}。显式 HTTP 重试为 0，格式纠正最多 1 次。");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation.Token }, async (item, token) =>
            {
                if (ledger.StopReason is not null) return;
                if (File.Exists(item.Path))
                {
                    var previous = JsonNode.Parse(await File.ReadAllTextAsync(item.Path))!;
                    await J.Write(Path.Combine(folder, "attempts", item.Data.Name, J.Hash(item.Case.S("case_id")) + "-" + Guid.NewGuid().ToString("N") + ".json"), previous);
                }
                using var handler = new RecordingHandler(counter, ledger, Path.Combine(folder, "calls", item.Data.Name, J.Hash(item.Case.S("case_id")) + ".jsonl"), item.Data.Name, item.Case.S("case_id"));
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                var client = new DeepSeekClient(apiKey, new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) }).GetDecisionsClient("deepseek-flash");
                var diagnostics = new JsonArray();
                var session = new WorkflowSession(ask: async (node, state, questions) =>
                {
                    handler.Node = node;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(600));
                    var result = await client.SystemOneAsync(Estimation.Request(state, questions), Estimation.Options(), new RequestOptions { CancellationToken = timeout.Token });
                    diagnostics.Add(J.O(("node_id", node), ("diagnostics", JsonSerializer.SerializeToNode(result.Diagnostics, J.Options)), ("usage", JsonSerializer.SerializeToNode(result.Usage, J.Options))));
                    return result.Answers.ToDictionary(p => p.Key, p => p.Value);
                });
                var watch = Stopwatch.StartNew(); var status = "ok"; JsonArray decisions; string? error = null;
                try { decisions = await Workflows.Run(item.Data, item.Case, session); }
                catch (Exception ex)
                {
                    status = ledger.StopReason is "budget_exhausted" or "budget_estimate_exceeded" || token.IsCancellationRequested ? "stopped" : "error";
                    error = ex.GetType().Name + (ex is DeepSeekException api ? $" HTTP {api.Status}" : "");
                    decisions = new JsonArray(item.Data.Policies.Select(p => (JsonNode)J.O(("policy_id", p), ("status", "error"), ("actions", new JsonArray()))).ToArray());
                    if (ex.Data["DecisionDiagnostics"] is IReadOnlyList<DecisionDiagnostic> failed) diagnostics.Add(J.O(("node_id", handler.Node), ("diagnostics", JsonSerializer.SerializeToNode(failed, J.Options)), ("usage", JsonSerializer.SerializeToNode(ex.Data["DecisionUsage"], J.Options))));
                }
                var nodeCallsPath = Path.Combine("node-calls", item.Data.Name, J.Hash(item.Case.S("case_id")) + "-" + Guid.NewGuid().ToString("N") + ".json");
                await J.Write(Path.Combine(folder, nodeCallsPath), session.Calls);
                var checkpoint = J.O(("case_id", item.Case.S("case_id")), ("workflow", item.Data.Name), ("case_hash", J.Hash(JsonNode.Parse(item.Case.S("input_json")))), ("status", status), ("error", error), ("decisions", decisions), ("wall_time_s", watch.Elapsed.TotalSeconds), ("diagnostics", diagnostics), ("node_calls_path", nodeCallsPath), ("completed_at_utc", DateTimeOffset.UtcNow.ToString("O")));
                await J.Write(item.Path, checkpoint);
                Console.WriteLine($"{item.Data.Name}/{item.Case.S("case_id")}：{status}；{watch.Elapsed.TotalSeconds:F1}s；累计高峰估计 ${ledger.Spent:F4}");
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.WriteLine("收到取消，已保存完成的案例和调用日志。"); }
        finally { Console.CancelKeyPress -= cancel; }
        var report = await Reporting.Report(root, name, datasets);
        return report.B("complete") ? 0 : 1;
    }
    internal static string CasePath(string folder, string workflow, string id) => Path.Combine(folder, "cases", workflow, J.Hash(id) + ".json");
    private static JsonObject Configuration(string root, JsonNode estimate)
    {
        string FileHash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        return J.O(("model", "deepseek-flash"), ("method", "auto"), ("temperature", 0), ("thinking", "disabled"), ("max_tokens", 4096), ("case_concurrency", 8), ("question_concurrency", 8), ("decision_timeout_seconds", 600), ("http_retries", 0),
            ("estimate_sha256", J.Hash(estimate)), ("data_sha256", estimate["source_manifest_sha256"]), ("code_revision", Data.CodeRevision),
            ("runner_sha256", FileHash(typeof(Evaluation).Assembly.Location)), ("sdk_sha256", FileHash(typeof(DecisionsClient).Assembly.Location)), ("core_sha256", FileHash(typeof(DeepSeekClient).Assembly.Location)),
            ("questions_sha256", FileHash(Path.Combine(AppContext.BaseDirectory, "questions.json"))), ("policies_sha256", FileHash(Path.Combine(AppContext.BaseDirectory, "customer-policies.json"))), ("runtime", Environment.Version.ToString()));
    }
    private static string ReadKey()
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("deepseek-api-key");
        if (string.IsNullOrWhiteSpace(key) && File.Exists("sample/appsettings.json")) key = JsonNode.Parse(File.ReadAllText("sample/appsettings.json")).S("apiKey");
        return string.IsNullOrWhiteSpace(key) || key == "test-key" ? throw new InvalidOperationException("缺少有效 DeepSeek API Key。") : key.Trim();
    }
    internal sealed class RecordingHandler(TokenCounter counter, RunLedger ledger, string path, string workflow, string caseId, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
    {
        private readonly SemaphoreSlim _logLock = new(1);
        internal string Node { get; set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            long input;
            lock (counter) input = (long)Math.Ceiling((counter.Messages(body) + 20) * 1.2);
            var id = Guid.NewGuid().ToString("N");
            if (!await ledger.Reserve(id, Estimation.Cost(input, 4096))) throw new InvalidOperationException("预算停止：" + ledger.StopReason);
            var watch = Stopwatch.StartNew(); JsonNode? responseBody = null; double? cost = null; string? model = null; int? status = null; string? error = null;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                try { responseBody = JsonNode.Parse(text); } catch (JsonException) { }
                var responseObject = responseBody as JsonObject;
                model = responseObject?["model"]?.ToString();
                var usage = responseObject?["usage"] as JsonObject;
                if (Count(usage?["prompt_tokens"], out var prompt) && Count(usage?["completion_tokens"], out var completion))
                {
                    var reportedCache = usage?["prompt_cache_hit_tokens"] ?? (usage?["prompt_tokens_details"] as JsonObject)?["cached_tokens"];
                    if (reportedCache is null) cost = Estimation.Cost(prompt, completion);
                    else if (Count(reportedCache, out var cached) && cached <= prompt) cost = Estimation.Cost(prompt, completion, cached: cached);
                }
                return response;
            }
            catch (Exception ex) { error = ex.GetType().Name; throw; }
            finally
            {
                // 日志不写请求头或密钥；先记录尝试，再结算，崩溃后宁可保留未知预留。
                var record = J.O(("attempt_id", id), ("workflow", workflow), ("case_id", caseId), ("node_id", Node), ("utc", DateTimeOffset.UtcNow.ToString("O")), ("http_status", status), ("error", error), ("wall_time_s", watch.Elapsed.TotalSeconds), ("request", body), ("response", responseBody), ("response_model", model), ("usage", (responseBody as JsonObject)?["usage"]), ("peak_estimated_usd", cost));
                await _logLock.WaitAsync(CancellationToken.None);
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.AppendAllTextAsync(path, record.ToJsonString(J.Options) + "\n"); }
                catch { await ledger.Halt("log_write_failed"); throw; }
                finally { _logLock.Release(); }
                await ledger.Settle(id, cost, model);
            }
        }
        private static bool Count(JsonNode? node, out long count)
        {
            count = 0;
            return node is JsonValue value && value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out count) && count >= 0;
        }
    }
}
