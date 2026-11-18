using System.Text.Json.Nodes;

namespace DeepSeek.Decisions.Benchmarks;

// 在途预留必须与已知费用一起计入；未知用量不会被当作免费请求。
internal sealed class RunLedger
{
    private readonly SemaphoreSlim _mutex = new(1);
    private readonly string _path;
    private readonly Dictionary<string, double> _reservations = [];
    private readonly JsonArray _unknown = [];
    internal double Budget { get; }
    internal double Spent { get; private set; }
    internal string? StopReason { get; private set; }
    internal string? ResponseModel { get; private set; }
    internal long HttpAttempts { get; private set; }
    private RunLedger(string path, double budget) { _path = path; Budget = budget; }
    internal static async Task<RunLedger> Open(string path, double budget)
    {
        if (!double.IsFinite(budget) || budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget), "预算必须是有限正数。");
        var ledger = new RunLedger(path, budget);
        if (File.Exists(path))
        {
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (saved["spent_peak_estimated_usd"] is null || !double.TryParse(saved.S("spent_peak_estimated_usd"), System.Globalization.CultureInfo.InvariantCulture, out var spent) || !double.IsFinite(spent) || spent < 0
                || saved["reservations"] is not JsonObject || saved["unknown_attempts"] is not JsonArray)
                throw new InvalidDataException("费用账本缺失或损坏，禁止按零费用恢复。");
            ledger.Spent = saved.N("spent_peak_estimated_usd"); ledger.HttpAttempts = (long)saved.N("http_attempts");
            ledger.ResponseModel = saved["response_model"]?.ToString();
            if (saved.S("stop_reason") == "response_model_changed") throw new InvalidOperationException("上次运行已观察到模型版本变化，请使用新的运行名称以免混合版本。");
            foreach (var unknown in saved["unknown_attempts"].Items()) ledger._unknown.Add(unknown.Copy());
            if (ledger._unknown.Count > 0 || saved["reservations"]!.AsObject().Count > 0)
                throw new InvalidOperationException("上次运行有未知用量或未结算的在途请求；请先核对 ledger.json 与服务商账单，保留审计记录后处理，禁止自动按零费用续跑。");
        }
        await ledger.Save();
        return ledger;
    }
    internal async Task<bool> Reserve(string id, double amount)
    {
        await _mutex.WaitAsync();
        try
        {
            if (StopReason is not null) return false;
            if (!double.IsFinite(amount) || amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (Spent + _reservations.Values.Sum() + amount > Budget) { StopReason = "budget_exhausted"; await Save(); return false; }
            _reservations.Add(id, amount); HttpAttempts++;
            await Save(); return true;
        }
        finally { _mutex.Release(); }
    }
    internal async Task Settle(string id, double? cost, string? model)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_reservations.Remove(id)) throw new InvalidOperationException("重复或未知的请求结算。");
            if (cost is null) { _unknown.Add(id); StopReason = "unknown_usage"; }
            else
            {
                if (!double.IsFinite(cost.Value) || cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
                Spent += cost.Value;
                if (Spent + _reservations.Values.Sum() > Budget) StopReason = "budget_estimate_exceeded";
            }
            if (!string.IsNullOrWhiteSpace(model))
            {
                if (ResponseModel is not null && ResponseModel != model) StopReason = "response_model_changed";
                ResponseModel ??= model;
            }
            await Save();
        }
        finally { _mutex.Release(); }
    }
    internal async Task Halt(string reason)
    {
        await _mutex.WaitAsync();
        try { StopReason = reason; await Save(); }
        finally { _mutex.Release(); }
    }
    private Task Save() => J.Write(_path, J.O(("budget_usd", Budget), ("spent_peak_estimated_usd", Spent), ("http_attempts", HttpAttempts), ("reservations", _reservations), ("unknown_attempts", _unknown), ("stop_reason", StopReason), ("response_model", ResponseModel), ("pricing_date", Estimation.PriceDate)));
}
