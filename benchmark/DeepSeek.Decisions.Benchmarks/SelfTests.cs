using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class SelfTests
{
    internal static async Task Run(string root)
    {
        var count = 0;
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException("自测失败：" + why); count++; }
        var counterPath = Path.Combine(root, "tokenizer.zip");
        if (!File.Exists(counterPath)) throw new InvalidOperationException("自测需要冻结 tokenizer，请先运行 prepare；自测本身不下载或调用模型。");
        var counter = new TokenCounter(counterPath);
        var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "tokenizer-fixtures.json")))!;
        foreach (var fixture in fixtures.Items()) Check(counter.Count(fixture.S("text")) == (int)fixture.N("tokens"), "多语言及特殊符号必须按官方 tokenizer 计费，文本=" + fixture.S("text"));
        Check(Math.Abs(Estimation.Cost(1_000_000, 1_000_000) - 1.5) < 1e-12, "输入与输出均应计费");
        Check(Math.Abs(Estimation.Cost(1_000_000, 1_000_000, false) - .75) < 1e-12, "低峰价格应应用于输入和输出");
        Check(Math.Abs(Estimation.Cost(1_000_000, 0, cached: 1_000_000) - .006) < 1e-12, "缓存命中应使用命中价格");
        var action = Scoring.Action("issue_refund", J.O(("amount_usd", 25)));
        Check(!Scoring.Same(action, Scoring.Action("issue_refund", J.O(("amount_usd", 50)))), "动作参数不同必须判为不一致");
        Check(Scoring.Exact(J.A([action, action]), J.A([action])), "动作集合比较应忽略重复");
        Check(Scoring.Same(action, Scoring.Action("issue_refund", J.O(("amount_usd", 25.0)))), "JSON数值类型表示不应造成假差异");
        Check(Scoring.Compare("primary_action", J.O(("primary_action", action)), new JsonObject()) is null, "不适用的主要动作指标应保留null");
        Check(InvoiceWorkflow.ApplyPrecedence(["PAY", "FRAUD_REVIEW"]).SequenceEqual(["FRAUD_REVIEW"]), "欺诈风险必须阻止付款");
        Check(InvoiceWorkflow.ApplyPrecedence(["DISPUTE_LINES", "SHORT_PAY", "PAY"]).SequenceEqual(["DISPUTE_LINES", "SHORT_PAY"]), "供应商纠正仍允许合规的部分付款");

        var q = J.O(("type", "noul"), ("instructions", "Is this a greeting?"));
        var captured = await Estimation.Capture(JsonValue.Create("Hello")!, "greeting", q, "logprobs");
        Check(captured.B("logprobs") && captured.N("top_logprobs") == 20 && captured.N("max_tokens") == 4096 && captured["thinking"].S("type") == "disabled", "估价必须捕获SDK实际参数");
        var original = await Estimation.Capture(JsonValue.Create("Hello")!, "greeting", q, "structured");
        var corrected = await Estimation.Capture(JsonValue.Create("Hello")!, "greeting", q, "structured", true);
        Check(counter.Messages(corrected) > counter.Messages(original), "格式纠正应增加输入费用");

        var a = new Dictionary<string, SystemOneAnswer> { ["p"] = new() { Type = "noul", Noul = .5 }, ["s"] = new() { Type = "score", Probabilities = new Dictionary<string, double> { ["0"] = .25, ["1"] = .25, ["2"] = .5 } } };
        Check(!CustomerWorkflow.Holds(J.O(("kind", "NoulGate"), ("args", new object[] { "p", .5 })), new JsonObject(), a), "客服严格阈值处不应自动执行敏感操作");
        Check(Math.Abs(a.Expected("s") - 1.25) < 1e-12, "Score应保留分布期望，不能四舍五入成离散等级");
        var outcome = new Dictionary<string, SystemOneAnswer> { ["request_fulfilled"] = new() { Noul = .55 } };
        Check(AgentWorkflow.Classify(outcome, null, false) == "silent_failure", "用户沉默时需要更强的成功证据才能关闭");
        outcome["request_fulfilled"].Noul = .6;
        Check(AgentWorkflow.Classify(outcome, null, false) == "healthy", "Agent成功阈值包含边界");

        var securityInput = J.O(("alert", "IDP-1"), ("context", J.O(("asset", J.O(("tier", 2), ("environment", "prod"))))));
        var requested = new HashSet<string>();
        var closeSession = new WorkflowSession(ask: (node, _, questions) =>
        {
            requested.Add(node); var answers = Uniform(questions);
            answers["is_true_positive"].Noul = .14; answers["context_explains_activity"].Noul = .9;
            return Task.FromResult(answers);
        });
        var closed = await SecurityWorkflow.Run(securityInput, closeSession);
        Check(closed[0]!["primary_action"].S("name") == "auto_close" && requested.SetEquals(["triage"]), "明确授权且非Tier0时可关闭，不能继续执行隔离问题");
        var containmentAsked = false;
        var actSession = new WorkflowSession(ask: (node, _, questions) =>
        {
            var answers = Uniform(questions);
            if (node == "triage") answers["is_true_positive"].Noul = .9;
            else { containmentAsked = true; answers["outbound_channel_active"].Noul = .9; answers["affected_scope"].Choice = "organization_wide"; }
            return Task.FromResult(answers);
        });
        var acted = await SecurityWorkflow.Run(securityInput, actSession);
        Check(containmentAsked && acted[0]!["primary_action"].S("name") == "block_destination", "新答案进入act分支时必须执行containment，而非沿用历史路径");

        var customer = J.O(("transcript", J.A([J.O(("speaker", "customer"), ("text", "Yes, please refund the agreed $25."))])),
            ("customer", J.O(("tenure_months", 12), ("vip", false), ("prior_contacts_30d", 0), ("prior_refunds_90d", 0))),
            ("account", J.O(("card_id", "test-card"), ("card_status", "active"), ("subscription_active", false), ("subscription_monthly_usd", 0.0), ("outstanding_fee_usd", 0.0), ("transaction", null), ("order", null), ("refund", J.O(("amount_usd", 25.0), ("eligible", true), ("already_issued", false))))),
            ("state", J.O(("pending", "issue_refund"), ("intent", null), ("handed_off", false))));
        var customerSession = new WorkflowSession(ask: (_, _, questions) =>
        {
            var answers = Uniform(questions);
            foreach (var (id, option) in new[] { ("intent", "refund_request"), ("desired_outcome", "money_back"), ("proposal_reply", "accepts") })
                if (answers.TryGetValue(id, out var answer))
                { answer.Choice = option; answer.Confidence = 1; foreach (var key in answer.Probabilities!.Keys.ToArray()) answer.Probabilities[key] = key == option ? 1 : 0; }
            return Task.FromResult(answers);
        });
        var customerResult = await CustomerWorkflow.Run(customer, customerSession);
        var guarded = customerResult.Items().Single(d => d.S("policy_id") == "A");
        Check(guarded["actions"].Items().Any(d => Scoring.Same(d, action)), "明确同意的小额合规退款应执行准确金额");
        Check(customerSession.Calls.Items().Select(d => d.S("node_id")).ToHashSet().SetEquals(["triage", "consent", "money"]), "节点调用应由当前证据和待定提案决定");

        var temp = Path.Combine(root, "self-test", Guid.NewGuid().ToString("N"));
        var ledgerPath = Path.Combine(temp, "ledger.json");
        var ledger = await RunLedger.Open(ledgerPath, .035);
        var reserved = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ledger.Reserve(i.ToString(), .01)));
        Check(reserved.Count(r => r) == 3, "并发预算必须计入在途预留，不能超额发起请求");
        for (var i = 0; i < reserved.Length; i++) if (reserved[i]) await ledger.Settle(i.ToString(), .005, "fixture-model");
        var resumed = await RunLedger.Open(ledgerPath, .035);
        Check(Math.Abs(resumed.Spent - .015) < 1e-12 && resumed.HttpAttempts == 3, "断点恢复必须保留之前尝试的实际用量费用");
        Check(await resumed.Reserve("unknown", .01), "剩余预算应允许新的合法尝试");
        await resumed.Settle("unknown", null, null);
        Check(resumed.StopReason == "unknown_usage" && !await resumed.Reserve("after-unknown", 0), "未知用量应停止新增调用");
        var rejected = false;
        try { await RunLedger.Open(ledgerPath, 1); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "未知费用不能通过增加预算隐式归零");

        var successLedger = await RunLedger.Open(Path.Combine(temp, "http-ledger.json"), 1);
        var fake = new FakeHttp();
        var callPath = Path.Combine(temp, "calls.jsonl");
        using (var recorder = new Evaluation.RecordingHandler(counter, successLedger, callPath, "test", "case", fake))
        using (var http = new HttpClient(recorder))
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://offline.invalid/v1/chat/completions") { Content = new StringContent(captured.ToJsonString(J.Options), Encoding.UTF8, "application/json") };
            message.Headers.Authorization = new("Bearer", "SELF_TEST_SECRET");
            using var response = await http.SendAsync(message);
            Check(response.IsSuccessStatusCode && fake.Count == 1, "内存传输应记录真实尝试数");
        }
        var log = await File.ReadAllTextAsync(callPath);
        Check(!log.Contains("SELF_TEST_SECRET", StringComparison.Ordinal) && !log.Contains("Authorization", StringComparison.OrdinalIgnoreCase), "调用日志绝不能泄露认证信息");
        Check(Math.Abs(successLedger.Spent - Estimation.Cost(10, 2, cached: 3)) < 1e-12, "费用应使用响应usage并区分缓存命中");
        var malformedLedger = await RunLedger.Open(Path.Combine(temp, "malformed-ledger.json"), 1);
        using (var recorder = new Evaluation.RecordingHandler(counter, malformedLedger, Path.Combine(temp, "malformed.jsonl"), "test", "case", new FakeHttp("{\"usage\":{\"prompt_tokens\":\"invalid\",\"completion_tokens\":2}}")))
        using (var http = new HttpClient(recorder))
        using (var response = await http.PostAsync("https://offline.invalid/v1/chat/completions", new StringContent(captured.ToJsonString(J.Options), Encoding.UTF8, "application/json")))
            Check(malformedLedger.StopReason == "unknown_usage", "无效usage必须视为未知，不能按零token费用继续");
        var brokenPath = Path.Combine(temp, "broken-ledger.json");
        await File.WriteAllTextAsync(brokenPath, "{}");
        rejected = false;
        try { await RunLedger.Open(brokenPath, 1); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "损坏账本不能重置累计费用");
        var changedPath = Path.Combine(temp, "changed-ledger.json");
        var changed = await RunLedger.Open(changedPath, 1);
        await changed.Reserve("first", .01); await changed.Settle("first", .001, "model-v1");
        await changed.Reserve("second", .01); await changed.Settle("second", .001, "model-v2");
        Check(changed.StopReason == "response_model_changed", "同一运行不能混合服务端模型版本");
        rejected = false;
        try { await RunLedger.Open(changedPath, 2); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "模型版本变化不能通过断点恢复清除");
        Console.WriteLine($"自测通过：{count} 项，模型调用数 0。全量工作流回放请运行 replay。");
    }
    private static Dictionary<string, SystemOneAnswer> Uniform(JsonObject questions)
    {
        var answers = new Dictionary<string, SystemOneAnswer>();
        foreach (var (id, question) in questions)
        {
            var q = question ?? throw new InvalidDataException("自测题目不能为空。");
            var kind = q.S("type");
            var probabilities = kind == "choice" ? q["criteria"]!.AsObject().ToDictionary(k => k.Key, _ => 0.0)
                : kind == "score" ? Enumerable.Range(0, q["criteria"]!.AsArray().Count).ToDictionary(i => i.ToString(), _ => 0.0) : null;
            if (probabilities is not null) probabilities[probabilities.Keys.First()] = 1;
            answers[id] = new() { Type = kind, Noul = kind == "noul" ? 0 : null, Choice = kind == "choice" ? probabilities!.Keys.First() : null, Score = kind == "score" ? 0 : null, Confidence = kind == "noul" ? null : 1, Probabilities = probabilities };
        }
        return answers;
    }
    private sealed class FakeHttp(string? payload = null) : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "offline.invalid") throw new InvalidOperationException("自测禁止外部模型请求。");
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload ?? "{\"model\":\"fixture-model\",\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_cache_hit_tokens\":3}}", Encoding.UTF8, "application/json") });
        }
    }
}
