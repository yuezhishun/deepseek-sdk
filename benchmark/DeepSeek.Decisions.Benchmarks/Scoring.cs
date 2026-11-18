using System.Text.Json.Nodes;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Scoring
{
    internal static JsonObject Action(string name, JsonObject? arguments = null) => J.O(("name", name), ("arguments_json", (arguments ?? new JsonObject()).ToJsonString(J.Options)));
    internal static string ActionKey(JsonNode action) => action.S("name") + ":" + J.Canonical(JsonNode.Parse(action.S("arguments_json", "{}")));
    internal static bool Same(JsonNode? left, JsonNode? right) => left is not null && right is not null && ActionKey(left) == ActionKey(right);
    internal static bool Exact(JsonNode? left, JsonNode? right) => left.Items().Select(ActionKey).ToHashSet().SetEquals(right.Items().Select(ActionKey));
    internal static bool? Compare(string comparison, JsonNode predicted, JsonNode reference) => comparison switch
    {
        "exact_actions" => Exact(predicted["actions"], reference["actions"]),
        "primary_action" => reference["primary_action"] is null ? null : Same(predicted["primary_action"], reference["primary_action"]),
        _ => throw new InvalidDataException("未知比较规则：" + comparison),
    };
    internal static JsonObject Validate(List<WorkflowData> datasets)
    {
        var rows = new JsonArray();
        foreach (var data in datasets)
        {
            var count = Data.Specs.Single(s => s.Name == data.Name).Count;
            if (data.Cases.Count != count || data.Cases.Select(c => c.S("case_id")).Distinct().Count() != count) throw new InvalidDataException(data.Name + " 案例数量或 ID 不符。");
            var jev = data.JevCases.ToDictionary(c => c.S("case_id"));
            if (jev.Count != count || data.Cases.Any(c => !jev.ContainsKey(c.S("case_id")))) throw new InvalidDataException(data.Name + " Jev 案例缺失。");
            foreach (var role in new[] { "consensus", "openai", "anthropic" })
            {
                foreach (var metric in data.Manifest["metrics"].Items())
                {
                    var correct = 0; var denominator = 0; var unresolved = 0;
                    foreach (var c in data.Cases)
                    {
                        var labels = c[role]!["decisions"].Items().ToDictionary(d => d.S("policy_id"));
                        var predicted = jev[c.S("case_id")]["decisions"].Items().ToDictionary(d => d.S("policy_id"));
                        if (!predicted.Keys.ToHashSet().SetEquals(data.Policies)) throw new InvalidDataException("策略缺失：" + c.S("case_id"));
                        foreach (var policy in data.Policies)
                        {
                            if (!labels.TryGetValue(policy, out var reference) || reference.S("status") != "ok") { unresolved++; continue; }
                            var prediction = predicted[policy];
                            var matched = prediction.S("status") == "ok" ? Compare(metric.S("comparison"), prediction, reference) : false;
                            if (matched is null) continue;
                            denominator++; if (matched.Value) correct++;
                        }
                    }
                    var rate = denominator == 0 ? (double?)null : (double)correct / denominator;
                    if (role == "consensus")
                    {
                        var published = data.Manifest["runs"]![data.JevRun]!["scores"].Items().Single(m => m.S("metric_id") == metric.S("metric_id"));
                        if (denominator != (int)published.N("denominator") || rate is null || Math.Abs(rate.Value - published.N("value")) > 1e-9) throw new InvalidDataException($"官方指标不符：{data.Name}/{metric.S("metric_id")}");
                    }
                    rows.Add(J.O(("workflow", data.Name), ("reference", role), ("metric", metric.S("comparison")), ("correct", correct), ("denominator", denominator), ("agreement", rate), ("unresolved", unresolved)));
                }
            }
        }
        return J.O(("status", "ok"), ("cases", datasets.Sum(d => d.Cases.Count)), ("jev_scores", rows));
    }
}
