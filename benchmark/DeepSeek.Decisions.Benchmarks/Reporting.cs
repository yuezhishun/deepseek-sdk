using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Reporting
{
    internal static async Task<JsonObject> Report(string root, string name, List<WorkflowData> datasets)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..") throw new ArgumentException("非法运行名称。");
        var folder = Path.Combine(root, "runs", name);
        var reportFolder = File.Exists(Path.Combine(folder, "manifest.json")) ? folder : Path.Combine(root, "reports", name);
        Directory.CreateDirectory(reportFolder);
        var baseline = Scoring.Validate(datasets);
        var rows = new JsonArray(); var coverage = new JsonArray(); var complete = true;
        foreach (var data in datasets)
        {
            var predictions = new Dictionary<string, JsonNode>();
            foreach (var c in data.Cases)
            {
                var path = Evaluation.CasePath(folder, data.Name, c.S("case_id"));
                if (!File.Exists(path)) continue;
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                if (saved.S("case_hash") != J.Hash(JsonNode.Parse(c.S("input_json")))) throw new InvalidDataException("报告检查点与数据不符。");
                predictions[c.S("case_id")] = saved;
            }
            var ok = predictions.Values.Count(p => p.S("status") == "ok");
            var errors = predictions.Values.Count(p => p.S("status") == "error");
            var stopped = predictions.Values.Count(p => p.S("status") == "stopped");
            complete &= ok == data.Cases.Count;
            var times = predictions.Values.Select(p => p.N("wall_time_s")).Order().ToArray();
            var diagnostics = predictions.Values.SelectMany(p => p["diagnostics"].Items()).SelectMany(p => p["diagnostics"].Items()).ToArray();
            var counts = J.O(("workflow", data.Name), ("cases", data.Cases.Count), ("ok", ok), ("error", errors), ("stopped", stopped), ("missing", data.Cases.Count - predictions.Count),
                ("median_case_seconds", times.Length > 0 ? times[(times.Length - 1) / 2] : null), ("p95_case_seconds", times.Length > 0 ? times[(int)Math.Ceiling(times.Length * .95) - 1] : null),
                ("fallback_questions", diagnostics.Count(d => !string.IsNullOrEmpty(d.S("FallbackReason")))), ("questions_with_missing_labels", diagnostics.Count(d => d["MissingLabels"].Items().Any())),
                ("probability_sources", diagnostics.GroupBy(d => d.S("ProbabilitySource")).ToDictionary(g => g.Key, g => g.Count())));
            coverage.Add(counts);
            foreach (var reference in new[] { "consensus", "openai", "anthropic" })
                foreach (var metric in data.Manifest["metrics"].Items())
                {
                    var correct = 0; var denominator = 0; var unresolved = 0; var missing = 0;
                    var byPolicy = new JsonArray();
                    foreach (var policy in data.Policies)
                    {
                        var pc = 0; var pd = 0;
                        foreach (var c in data.Cases)
                        {
                            var label = c[reference]!["decisions"].Items().SingleOrDefault(d => d.S("policy_id") == policy);
                            if (label is null || label.S("status") != "ok") { unresolved++; continue; }
                            if (!predictions.TryGetValue(c.S("case_id"), out var prediction) || prediction.S("status") == "stopped") { missing++; continue; }
                            var d = prediction["decisions"].Items().SingleOrDefault(d => d.S("policy_id") == policy);
                            var match = prediction.S("status") == "ok" && d is not null ? Scoring.Compare(metric.S("comparison"), d, label) : false;
                            if (match is null) continue;
                            pd++; denominator++; if (match.Value) { pc++; correct++; }
                        }
                        byPolicy.Add(J.O(("policy", policy), ("correct", pc), ("denominator", pd), ("agreement", pd > 0 ? (double)pc / pd : null)));
                    }
                    var jev = baseline["jev_scores"].Items().Single(r => r.S("workflow") == data.Name && r.S("reference") == reference && r.S("metric") == metric.S("comparison"));
                    rows.Add(J.O(("workflow", data.Name), ("reference", reference), ("metric", metric.S("comparison")), ("correct", correct), ("denominator", denominator), ("agreement", denominator > 0 ? (double)correct / denominator : null), ("jev_agreement", jev["agreement"]), ("jev_denominator", jev["denominator"]), ("unresolved_reference", unresolved), ("missing_prediction", missing), ("by_policy", byPolicy)));
                }
        }
        var ledgerPath = Path.Combine(folder, "ledger.json");
        var ledger = File.Exists(ledgerPath) ? JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath)) : null;
        var unknown = ledger?["unknown_attempts"].Items().Count() ?? 0;
        complete &= ledger is not null && ledger["stop_reason"] is null && unknown == 0 && (ledger["reservations"]?.AsObject().Count ?? 0) == 0;
        var headline = rows.Items().Where(r => r.S("reference") == "consensus").GroupBy(r => r.S("workflow")).Select(g => g.First()).ToArray();
        var overallJev = headline.Average(r => r.N("jev_agreement"));
        var summary = J.O(("complete", complete), ("status", complete ? "complete" : "incomplete"), ("name", name), ("model", "deepseek-flash"), ("method", "auto"), ("created_at_utc", DateTimeOffset.UtcNow.ToString("O")),
            ("overall_equal_workflow_agreement", complete ? headline.Average(r => r.N("agreement")) : null), ("jev_overall_equal_workflow_agreement", overallJev), ("coverage", coverage), ("scores", rows), ("ledger", ledger),
            ("limits", new[] { "指标是参考动作一致率；参考标签由模型生成。", "模型错误计为不一致；未执行或预算停止保留为缺失并报告覆盖，不发布完整总分。", "Jev的费用与延迟来自2026-09-28公开历史记录；本机运行的网络、模型版本与并发不同。", "本工具费用按锁定价格的高峰费率保守估算，实际账单可能更低；未知用量保持未知。", "动作包含参数，比较忽略顺序及重复；指标不适用时保留null。" }));
        await J.Write(Path.Combine(reportFolder, "results.json"), summary);
        var csv = new StringBuilder("workflow,reference,metric,deepseek_agreement,deepseek_denominator,jev_agreement,jev_denominator,missing_prediction,unresolved_reference\n");
        foreach (var row in rows.Items()) csv.AppendJoin(',', new[] { row.S("workflow"), row.S("reference"), row.S("metric"), row.S("agreement"), row.S("denominator"), row.S("jev_agreement"), row.S("jev_denominator"), row.S("missing_prediction"), row.S("unresolved_reference") }).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(reportFolder, "comparison.csv"), csv.ToString(), new UTF8Encoding(true));
        var body = string.Concat(rows.Items().Where(r => r.S("reference") == "consensus").Select(r => $"<tr><td>{J.Html(r.S("workflow"))}</td><td>{J.Html(r.S("metric"))}</td><td>{Percent(r["agreement"])}</td><td>{r.N("denominator")}</td><td>{Percent(r["jev_agreement"])}</td><td>{r.N("jev_denominator")}</td></tr>"));
        var coverageRows = string.Concat(coverage.Items().Select(r => $"<tr><td>{J.Html(r.S("workflow"))}</td><td>{r.N("ok")}</td><td>{r.N("error")}</td><td>{r.N("stopped")}</td><td>{r.N("missing")}</td><td>{r.N("fallback_questions")}</td><td>{r.N("questions_with_missing_labels")}</td></tr>"));
        var historyRows = string.Concat(datasets.Select(d =>
        {
            var run = d.Manifest["runs"]![d.JevRun]!;
            return $"<tr><td>{J.Html(d.Name)}</td><td>{run.N("mean_cost_usd").ToString("F6", CultureInfo.InvariantCulture)}</td><td>{run.N("mean_wall_time_s").ToString("F3", CultureInfo.InvariantCulture)}</td><td>{J.Html(run.S("wall_time_cases"))}</td></tr>";
        }));
        var limits = string.Concat(summary["limits"].Items().Select(l => $"<li>{J.Html(l.S())}</li>"));
        var html = $"<!doctype html><html lang=zh-CN><meta charset=utf-8><title>WorkflowEvals 对照报告</title><style>body{{font:16px/1.6 system-ui;max-width:1200px;margin:35px auto;padding:20px}}table{{border-collapse:collapse;width:100%;margin:20px 0}}td,th{{border:1px solid #cbd5e1;padding:9px;text-align:left}}th{{background:#edf3f8}}</style><h1>DeepSeek.Decisions / Jev 官方 WorkflowEvals 对照</h1><p>状态：<strong>{(complete ? "全量完成" : "未完成")}</strong>。模型：deepseek-flash，Method=auto。主参考：consensus。</p><p>四工作流等权一致率：DeepSeek {Percent(summary["overall_equal_workflow_agreement"])}；Jev {Percent(summary["jev_overall_equal_workflow_agreement"])}。</p><h2>参考动作一致率</h2><table><tr><th>工作流</th><th>指标</th><th>DeepSeek</th><th>分母</th><th>Jev</th><th>分母</th></tr>{body}</table><h2>覆盖与概率诊断</h2><table><tr><th>工作流</th><th>成功</th><th>失败</th><th>停止</th><th>未运行</th><th>回退题目</th><th>缺失标签题目</th></tr>{coverageRows}</table><p>已知累计高峰估算费用：{(ledger is null ? "尚未运行" : "$" + ledger.N("spent_peak_estimated_usd").ToString("F6", CultureInfo.InvariantCulture))}；未知用量尝试：{unknown}。全部参考和逐策略分数见 results.json，CSV 可直接导入表格。</p><h2>Jev 历史测量（非同期测试）</h2><table><tr><th>工作流</th><th>USD/案例</th><th>平均秒/案例</th><th>测量案例数</th></tr>{historyRows}</table><h2>方法与边界</h2><ul>{limits}</ul><p>来源：<a href='https://github.com/typesafe-ai/WorkflowEvals/tree/{Data.CodeRevision}'>官方冻结代码</a>；数据版本及校验值见上级目录 sources.json。</p></html>";
        await File.WriteAllTextAsync(Path.Combine(reportFolder, "report.html"), html);
        Console.WriteLine($"报告已保存：{Path.Combine(reportFolder, "report.html")}；状态={summary.S("status")}");
        return summary;
    }
    private static string Percent(JsonNode? value) => value is null ? "—" : value.N().ToString("P2", CultureInfo.InvariantCulture);
}
