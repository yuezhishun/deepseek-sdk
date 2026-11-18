using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Answers
{
    internal static double P(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Noul ?? 0 : 0;
    internal static string C(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Choice ?? "" : "";
    internal static double Confidence(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Confidence ?? 0 : 0;
    internal static double Prob(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key, string option) => a.TryGetValue(key, out var v) && v.Probabilities is not null && v.Probabilities.TryGetValue(option, out var probability) ? probability : 0;
    internal static double Tail(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key, int level, bool above = true) => a.TryGetValue(key, out var v) ? v.Probabilities?.Where(p => above ? int.Parse(p.Key) >= level : int.Parse(p.Key) <= level).Sum(p => p.Value) ?? 0 : 0;
    internal static double Expected(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Probabilities?.Sum(p => int.Parse(p.Key) * p.Value) ?? 0 : 0;
    internal static double ChoiceConfidence(IDictionary<string, double> probabilities)
    {
        var n = probabilities.Count;
        return n == 1 ? 1 : Math.Clamp((probabilities.Values.Max() - 1.0 / n) / (1 - 1.0 / n), 0, 1);
    }
    internal static SystemOneAnswer Read(JsonNode row, JsonNode question)
    {
        var kind = row.S("kind");
        var probabilities = row["probabilities"].Items().ToDictionary(p => p.S("option"), p => p.N("probability"));
        if (kind == "noul") return new() { Type = kind, Noul = probabilities["true"] };
        var keys = kind == "choice" ? question["criteria"]!.AsObject().Select(p => p.Key).ToArray() : Enumerable.Range(0, probabilities.Count).Select(i => i.ToString()).ToArray();
        var ordered = keys.ToDictionary(k => k, k => probabilities[k]);
        var best = keys.MaxBy(k => probabilities[k])!;
        if (kind == "choice") return new() { Type = kind, Choice = best, Probabilities = ordered, Confidence = ChoiceConfidence(ordered) };
        var mode = int.Parse(best);
        var center = (keys.Length - 1) / 2.0;
        var uniformSpread = Enumerable.Range(0, keys.Length).Average(i => Math.Abs(i - center));
        return new() { Type = kind, Score = ordered.Sum(p => int.Parse(p.Key) * p.Value), Probabilities = ordered, Confidence = Math.Clamp(1 - ordered.Sum(p => Math.Abs(int.Parse(p.Key) - mode) * p.Value) / uniformSpread, 0, 1) };
    }
}

// 固定输入、模型读数和运行记录在此交汇；回放客户端不会创建网络传输。
internal sealed class WorkflowSession(JsonObject? replay = null, Func<string, JsonNode, JsonObject, Task<Dictionary<string, SystemOneAnswer>>>? ask = null)
{
    internal JsonArray Calls { get; } = [];
    private readonly HashSet<string> _read = [];
    internal async Task<Dictionary<string, SystemOneAnswer>> Ask(string node, JsonNode state, JsonObject questions)
    {
        var call = J.O(("node_id", node), ("state", state), ("questions", questions));
        Calls.Add(call);
        Dictionary<string, SystemOneAnswer> answers;
        if (replay is not null)
        {
            answers = [];
            foreach (var (id, question) in questions)
            {
                var row = replay["questions"].Items().SingleOrDefault(q => q.S("node_id") == node && q.S("question_id") == id)
                    ?? throw new InvalidDataException($"Jev 回放缺少题目：{node}/{id}");
                var expectedQuestion = JsonNode.Parse(row.S("question_json"))!.AsObject();
                var actualQuestion = question!.DeepClone().AsObject();
                // 可选空字段不改变题义；choice 顺序另行保留用于平局。
                foreach (var field in new[] { "criteria", "instructions" })
                {
                    if (actualQuestion[field] is null) actualQuestion.Remove(field);
                    if (expectedQuestion[field] is null) expectedQuestion.Remove(field);
                }
                if (J.Canonical(actualQuestion) != J.Canonical(expectedQuestion) || J.Canonical(state) != J.Canonical(JsonNode.Parse(row.S("state_json"))!))
                    throw new InvalidDataException($"回放输入不一致：{node}/{id}；question_match={J.Canonical(actualQuestion) == J.Canonical(expectedQuestion)}；state_match={J.Canonical(state) == J.Canonical(JsonNode.Parse(row.S("state_json"))!)}");
                answers[id] = Answers.Read(row, question!);
                _read.Add(node + "/" + id);
            }
        }
        else answers = await (ask ?? throw new InvalidOperationException("没有模型读取器。"))(node, state, questions);
        call["answers"] = JsonSerializer.SerializeToNode(answers, J.Options);
        return answers;
    }
    internal void AssertComplete()
    {
        if (replay is not null && _read.Count != replay["questions"].Items().Count()) throw new InvalidDataException("回放没有完整重现 Jev 执行路径。");
    }
}

internal static class Workflows
{
    private static readonly JsonNode Templates = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "questions.json")))!;
    internal static JsonObject Panel(string workflow, string panel) => Templates[workflow]![panel]!.DeepClone().AsObject();
    internal static JsonObject Decision(string policy, IEnumerable<JsonObject> actions, JsonObject? primary = null) => J.O(("policy_id", policy), ("status", "ok"), ("actions", J.A(actions)), ("primary_action", primary));
    internal static JsonObject Single(string policy, string name) => Decision(policy, [Scoring.Action(name)], Scoring.Action(name));
    internal static Task<JsonArray> Run(WorkflowData data, JsonObject c, WorkflowSession session) => data.Name switch
    {
        "security_incidents" => SecurityWorkflow.Run(JsonNode.Parse(c.S("input_json"))!, session),
        "customer_service" => CustomerWorkflow.Run(JsonNode.Parse(c.S("input_json"))!, session),
        "agent_trace_observability" => AgentWorkflow.Run(data, c, session),
        "invoice_processing" => InvoiceWorkflow.Run(data, c, session),
        _ => throw new InvalidDataException("未知工作流。"),
    };
    internal static async Task<JsonObject> Replay(List<WorkflowData> datasets)
    {
        var counts = new JsonArray();
        foreach (var data in datasets)
        {
            var jev = data.JevCases.ToDictionary(c => c.S("case_id"));
            var cases = 0; var decisions = 0; var questions = 0;
            foreach (var c in data.Cases)
            {
                var recorded = jev[c.S("case_id")];
                var session = new WorkflowSession(recorded);
                JsonArray actual;
                try { actual = await Run(data, c, session); session.AssertComplete(); }
                catch (Exception ex) { throw new InvalidDataException($"{data.Name}/{c.S("case_id")}: {ex.Message}", ex); }
                foreach (var expected in recorded["decisions"].Items())
                {
                    var a = actual.Single(d => d.S("policy_id") == expected.S("policy_id"))!;
                    if (!Scoring.Exact(a["actions"], expected["actions"]) || expected["primary_action"] is not null && !Scoring.Same(a["primary_action"], expected["primary_action"]))
                        throw new InvalidDataException($"回放动作不符：{data.Name}/{c.S("case_id")}/{expected.S("policy_id")}；actual={a}；expected={expected}");
                    decisions++;
                }
                cases++; questions += recorded["questions"].Items().Count();
            }
            Console.WriteLine($"回放通过 {data.Name}：{cases} 案例，{decisions} 策略决策。");
            counts.Add(J.O(("workflow", data.Name), ("cases", cases), ("decisions", decisions), ("questions", questions)));
        }
        return J.O(("status", "ok"), ("code_revision", Data.CodeRevision), ("workflows", counts));
    }
}
