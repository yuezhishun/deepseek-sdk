using System.Globalization;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class InvoiceWorkflow
{
    private static readonly string[] Kinds = ["work_or_goods", "tax", "charge_on_top", "credit_or_discount", "expense_or_retainage"];
    private static readonly string[] Scopes = ["priced_unit", "component_also_billed", "extra_approved", "extra_unapproved", "not_ours", "unsure"];
    private static readonly string[] Completions = ["confirmed", "less", "not_done", "vendor_only", "none", "unsure"];
    private static readonly string[] Priors = ["same_obligation", "earlier_period_or_phase", "unrelated", "unsure"];
    private static readonly string[] Approvals = ["approves_as_billed", "approves_except_lines", "review_in_progress", "withholds", "unsure"];
    private static readonly string[] Senders = ["plausible", "unconfirmable_story", "impostor", "unsure"];
    private static readonly string[] Bases = ["total_fee", "period_fee", "unit_rates", "cap_only", "none", "unsure"];
    private static readonly string[] Precedence = ["FRAUD_REVIEW", "REJECT_AS_DUPLICATE", "HOLD_VERIFY_BANK_DETAILS", "VERIFY_VENDOR_OUT_OF_BAND", "DUPLICATE_REVIEW", "REJECT_AS_UNAUTHORIZED", "PROJECT_OWNER_REVIEW", "DISPUTE_LINES", "HOLD_REQUEST_DOCUMENTS", "REQUEST_CORRECTED_INVOICE", "PROCUREMENT_REVIEW", "APPROVAL_ESCALATION", "HUMAN_APPROVAL", "SHORT_PAY", "SCHEDULE_PAYMENT", "PAY"];
    private static readonly string[] Safety = ["HOLD_VERIFY_BANK_DETAILS", "VERIFY_VENDOR_OUT_OF_BAND"];
    private static readonly string[] Holds = [.. Safety, "DUPLICATE_REVIEW", "PROCUREMENT_REVIEW", "PROJECT_OWNER_REVIEW", "HOLD_REQUEST_DOCUMENTS", "APPROVAL_ESCALATION"];
    internal static async Task<JsonArray> Run(WorkflowData data, JsonObject c, WorkflowSession session)
    {
        // 发票只有一个 semantic 节点；这些输入在所有模型回答之前由官方代码生成。
        var id = c.S("case_id");
        var state = data.InputState(id, "semantic") ?? throw new InvalidDataException("发票缺少固定 semantic 输入。");
        var qs = new JsonObject();
        foreach (var row in data.InputQuestions(id).Where(q => q.S("node_id") == "semantic").DistinctBy(q => q.S("question_id")))
        {
            var qid = row.S("question_id");
            var q = JsonNode.Parse(row.S("question_json"))!.AsObject();
            if (q.S("type") == "choice")
            {
                var order = qid.EndsWith("_kind") ? Kinds : qid.EndsWith("_scope") ? Scopes : qid.EndsWith("_completion") ? Completions
                    : qid.StartsWith("prior_") ? Priors : qid.StartsWith("approval_") ? Approvals : qid.StartsWith("sender_") ? Senders
                    : qid == "price_basis" ? Bases : qid == "retainage" ? ["5", "10", "15", "none"] : qid == "discount_days" ? ["7", "10", "15", "20", "30", "none"] : q["criteria"]!.AsObject().Select(p => p.Key).ToArray();
                var criteria = q["criteria"]!;
                q["criteria"] = new JsonObject(order.Where(k => criteria[k] is not null).Select(k => KeyValuePair.Create(k, criteria[k].Copy())));
            }
            qs[qid] = q;
        }
        var answers = await session.Ask("semantic", state, qs);
        var result = new JsonArray();
        foreach (var policy in data.Policies)
        {
            var facts = Resolve(state, answers);
            result.Add(Decide(policy, facts, answers));
        }
        return result;
    }
    private static string Choice(Dictionary<string, SystemOneAnswer> a, string key, string[] options, string fallback = "unsure")
    {
        if (!a.ContainsKey(key)) return fallback;
        return options.MaxBy(option => a.Prob(key, option))!;
    }
    private static JsonObject Resolve(JsonNode state, Dictionary<string, SystemOneAnswer> a)
    {
        var f = state["exact_facts"]!.DeepClone().AsObject();
        bool Yes(string key) => a.P(key) >= .5;
        var poLines = f["po_lines"].Items().ToArray(); var figures = f["evidence_figures"].Items().ToArray();
        var receipts = figures.Any(fg => fg.S("evidence_type") is "goods_receipt" or "acceptance_record");
        var lines = new JsonArray(); var index = 0;
        foreach (var source in state["invoice"]!["line_items"].Items())
        {
            var line = source.DeepClone().AsObject(); var i = index++; var prefix = "line_" + i;
            var kind = Choice(a, prefix + "_kind", Kinds, "");
            var scope = kind == "work_or_goods" ? Choice(a, prefix + "_scope", Scopes) : kind == "charge_on_top" && a.Prob(prefix + "_scope", "component_also_billed") >= .5 ? "component_also_billed" : "priced_unit";
            var completion = kind == "work_or_goods" ? Choice(a, prefix + "_completion", Completions) : "confirmed";
            var rebilled = Yes(prefix + "_rebilled"); var declined = Yes(prefix + "_owner_declined"); var fee = Yes(prefix + "_unexplained_fee");
            var category = kind is "tax" or "credit_or_discount" or "expense_or_retainage" ? "OK"
                : scope == "component_also_billed" || rebilled ? "DUPLICATE_CHARGE"
                : kind == "charge_on_top" ? fee ? "UNEXPLAINED_FEE" : "OK"
                : declined ? "DECLINED_BY_OWNER" : scope is "extra_unapproved" or "not_ours" ? "OUT_OF_SCOPE"
                : completion == "not_done" ? "NOT_PERFORMED" : fee ? "UNEXPLAINED_FEE" : "OK";
            line["index"] = i; line["kind"] = kind; line["scope"] = scope; line["completion"] = completion;
            line["category"] = category; line["is_substantive"] = kind is "" or "work_or_goods" or "charge_on_top"; line["rate_differs"] = Yes(prefix + "_rate_differs");
            var pl = poLines.OrderByDescending(p => a.P(prefix + "_po_" + (int)p.N("index"))).FirstOrDefault(p => a.P(prefix + "_po_" + (int)p.N("index")) >= .5);
            line["price_variance_amount"] = 0.0;
            if (pl is not null && pl.N("unit_price") != 0 && line["unit_price"] is not null && kind != "tax")
            {
                var variance = (line.N("unit_price") - pl.N("unit_price")) / pl.N("unit_price");
                line["unit_price_variance_pct"] = Math.Round(variance, 4);
                line["price_variance_amount"] = variance > 0 ? Math.Round((line.N("unit_price") - pl.N("unit_price")) * (line.N("quantity") == 0 ? 1 : line.N("quantity")), 2) : 0.0;
            }
            var figure = figures.OrderByDescending(p => a.P(prefix + "_evidence_" + (int)p.N("index"))).FirstOrDefault(p => a.P(prefix + "_evidence_" + (int)p.N("index")) >= .5);
            if (figure is not null && line.N("quantity") != 0) line["quantity_supported_ratio"] = Math.Min(1, figure.N("value") / line.N("quantity"));
            else if (!receipts && line.N("quantity") != 0 && pl?["received_quantity"] is not null)
            {
                var available = Math.Max(0, pl.N("received_quantity") - pl.N("invoiced_quantity"));
                if (available + 1e-9 < line.N("quantity")) line["quantity_supported_ratio"] = Math.Round(available / line.N("quantity"), 4);
            }
            lines.Add(line);
        }
        var ls = lines.Items().ToArray();
        f["lines"] = lines;
        f["tax_line_amount"] = Math.Round(ls.Where(l => l.S("kind") == "tax").Sum(l => l.N("amount")), 2);
        f["substantive_lines_total"] = Math.Round(ls.Where(l => l.S("kind") != "tax").Sum(l => l.N("amount")), 2);
        f["positive_discount_lines_total"] = Math.Round(ls.Where(l => l.S("kind") == "credit_or_discount" && l.N("amount") > 0).Sum(l => l.N("amount")), 2);
        f["positive_adjustments_total"] = Math.Round(f.N("summary_adjustments_total") + f.N("positive_discount_lines_total"), 2);
        f["tax_double_counted"] = ls.Any(l => l.S("kind") == "tax") && f.N("summary_tax") > 0;
        f["implied_tax_rate_pct"] = f.N("summary_tax") != 0 && f.N("substantive_lines_total") > 0 ? JsonValue.Create(Math.Round(f.N("summary_tax") / f.N("substantive_lines_total") * 100, 2)) : null;
        f["not_confirmed_total"] = Math.Round(ls.Where(l => l.S("kind") == "work_or_goods" && l.S("completion") is "vendor_only" or "none" or "unsure").Sum(l => l.N("amount")), 2);
        f["records_contradict_vendor"] = ls.Any(l => l.S("kind") == "work_or_goods" && l.S("completion") == "less" && (l["quantity_supported_ratio"] is null || l.N("quantity_supported_ratio") >= .98));
        var same = false; var anySame = false; var reversed = false; var unsure = false; var band = Math.Max(10, .01 * f.N("invoice_total"));
        foreach (var prior in f["prior_invoices"].Items())
        {
            var relation = Choice(a, "prior_" + (int)prior.N("index"), Priors);
            var live = prior.S("status") is "paid" or "pending";
            if (relation == "same_obligation" && live) { anySame = true; same |= Math.Abs(prior.N("amount") - f.N("invoice_total")) <= band; }
            else if (relation == "same_obligation") reversed = true;
            else if (relation == "unsure" && live) unsure = true;
        }
        f["same_obligation_same_amount"] = same; f["same_obligation_other_amount"] = anySame && !same;
        f["reversed_prior"] = reversed; f["duplicate_unsure"] = unsure;
        var retainage = Choice(a, "retainage", ["5", "10", "15", "none"], "none");
        f["retainage_pct"] = retainage == "none" ? null : JsonValue.Create(double.Parse(retainage, CultureInfo.InvariantCulture));
        var days = Choice(a, "discount_days", ["7", "10", "15", "20", "30", "none"], "none");
        f["discount_window_open"] = days != "none" && f["invoice_date"] is not null && DateOnly.Parse(f.S("as_of"), CultureInfo.InvariantCulture) <= DateOnly.Parse(f.S("invoice_date"), CultureInfo.InvariantCulture).AddDays(int.Parse(days));
        return f;
    }
    private static JsonObject Profile(string name)
    {
        var p = J.O(("review_cost", 75), ("materiality", true), ("segregation", 10000), ("price_pct", .02), ("price_floor", 100), ("short_share", .25), ("short_amount", 2500), ("adjust_abs", 250), ("adjust_pct", .01), ("stale", 120), ("first_verify", 2500), ("mailbox", 25000), ("owner_relaxes", true), ("vague", 5000), ("vendor_only", 5000));
        var overrides = name switch
        {
            "startup" => new JsonObject(),
            "enterprise" => J.O(("review_cost", 20), ("materiality", false), ("segregation", 0), ("price_pct", .005), ("price_floor", 25), ("short_share", .10), ("short_amount", 1000), ("adjust_abs", 50), ("adjust_pct", .0025), ("stale", 60), ("first_verify", 0), ("mailbox", 10000), ("owner_relaxes", false), ("vague", 1000), ("vendor_only", 0)),
            "high_volume_retailer" => J.O(("review_cost", 40), ("segregation", 25000), ("price_pct", .03), ("price_floor", 250), ("short_share", .50), ("short_amount", 10000), ("adjust_abs", 500), ("adjust_pct", .02), ("stale", 180), ("first_verify", 10000), ("mailbox", 50000), ("vague", 10000), ("vendor_only", 10000)),
            _ => throw new InvalidDataException("未知发票策略。"),
        };
        foreach (var value in overrides) p[value.Key] = value.Value.Copy();
        return p;
    }
    internal static string[] ApplyPrecedence(IEnumerable<string> actions)
    {
        var chosen = actions.ToHashSet(StringComparer.Ordinal);
        if (chosen.Count == 0) return ["PAY"];
        if (chosen.Any(a => a is not ("PAY" or "SCHEDULE_PAYMENT" or "SHORT_PAY" or "DISPUTE_LINES" or "REQUEST_CORRECTED_INVOICE"))) chosen.ExceptWith(["PAY", "SCHEDULE_PAYMENT", "SHORT_PAY"]);
        else if (chosen.Overlaps(["DISPUTE_LINES", "REQUEST_CORRECTED_INVOICE"])) chosen.ExceptWith(["PAY", "SCHEDULE_PAYMENT"]);
        if (chosen.Contains("SHORT_PAY")) chosen.ExceptWith(["PAY", "SCHEDULE_PAYMENT"]);
        if (chosen.Contains("PAY") && chosen.Contains("SCHEDULE_PAYMENT")) chosen.Remove("PAY");
        return Precedence.Where(chosen.Contains).ToArray();
    }
    private static JsonObject Decide(string policy, JsonObject f, Dictionary<string, SystemOneAnswer> a)
    {
        var p = Profile(policy); var total = f.N("invoice_total"); var ls = f["lines"].Items().ToArray();
        bool Yes(string key) => a.P(key) >= .5;
        var actions = new HashSet<string>(); var wholeHold = false; var carved = 0.0;
        bool Add(bool condition, params string[] adds) { if (condition) actions.UnionWith(adds); return condition; }
        void Carve(double amount)
        {
            var tax = f.N("tax_line_amount") == 0 ? Math.Round(amount * f.N("implied_tax_rate_pct") / 100, 2) : 0;
            carved = Math.Round(carved + Math.Max(0, Math.Round(amount + tax, 2)), 2);
        }
        JsonObject Finish(IEnumerable<string> selected, double payable = 0)
        {
            var ordered = ApplyPrecedence(selected);
            var result = Workflows.Decision(policy, ordered.Select(n => Scoring.Action(n)), Scoring.Action(ordered[0]));
            result["outputs"] = J.O(("payable_now", ordered.Any(n => n is "PAY" or "SCHEDULE_PAYMENT" or "SHORT_PAY") ? payable : 0), ("disputed_amount", carved));
            return result;
        }
        var senderIds = f["unknown_senders"].Items().Select(s => Choice(a, "sender_" + (int)s.N("index"), Senders)).ToArray();
        var genuine = senderIds.Contains("impostor") ? "impostor" : senderIds.Any(s => s is "unconfirmable_story" or "unsure") ? "doubtful" : "yes";
        var bank = f.B("bank_details_changed") && !f.B("bank_change_recently_verified") || f.B("account_holder_differs") || f.B("bank_country_changed") || Yes("bank_change_claimed_in_comms") && !f.B("recent_verified_bank_change_in_log");
        var offFile = f["sender_domains_off_file"].Items().Any();
        var impostor = genuine == "impostor" || offFile || f.B("display_name_matches_known_contact_but_address_does_not") || f.B("sender_new_mailbox_on_known_domain") && (f.B("bank_details_changed") || Yes("bank_change_claimed_in_comms"));
        var duplicate = (f.B("invoice_number_already_seen") || f.B("same_obligation_same_amount")) && !f.B("reversed_prior") && !f.B("reversed_prior_with_same_amount");
        bool False(string key) => f[key] is not null && !f.B(key);
        var wrongEntity = False("bill_to_entity_matches_po") || False("vendor_id_matches_po") || False("bill_to_entity_is_company_entity");
        var readings = f["approvals_with_comment"].Items().ToDictionary(v => (int)v.N("index"), v => Choice(a, "approval_" + (int)v.N("index"), Approvals));
        var objection = readings.Values.Contains("withholds") || f.B("invoice_approval_rejected") && !f["approvals_with_comment"].Items().Any(v => v.S("status") == "rejected");
        var unqualified = f.B("invoice_approved_without_comment");
        var approved = unqualified || f["approvals_with_comment"].Items().Any(v => v.S("status") == "approved" && readings[(int)v.N("index")] is "approves_as_billed" or "approves_except_lines");
        Add(bank, "HOLD_VERIFY_BANK_DETAILS");
        var callback = f.N("paid_invoice_count") == 0 && total > p.N("first_verify") || f["unknown_senders"].Items().Any() && total >= p.N("mailbox") || genuine != "yes" || Yes("unusual_urgency") || offFile || f.B("display_name_matches_known_contact_but_address_does_not");
        Add(callback && !bank, "VERIFY_VENDOR_OUT_OF_BAND");
        if (impostor && bank || Yes("unusual_urgency") && (bank || impostor)) return Finish(duplicate ? ["FRAUD_REVIEW", "REJECT_AS_DUPLICATE"] : ["FRAUD_REVIEW"]);
        if (duplicate) return Finish(actions.Intersect(Safety).Concat(wrongEntity ? ["REJECT_AS_DUPLICATE", "REJECT_AS_UNAUTHORIZED"] : ["REJECT_AS_DUPLICATE"]));
        var duplicateReview = f.B("same_obligation_other_amount") || f.B("duplicate_unsure");
        if (wrongEntity)
        {
            actions.IntersectWith(Safety); actions.UnionWith(["REJECT_AS_UNAUTHORIZED", "REQUEST_CORRECTED_INVOICE"]);
            Add(duplicateReview, "DUPLICATE_REVIEW"); Add(!f.B("po_exists"), "PROCUREMENT_REVIEW"); return Finish(actions);
        }
        if (f.B("document_is_statement_not_invoice") || Yes("statement_not_invoice")) return Finish(actions.Intersect(Safety).Append("REQUEST_CORRECTED_INVOICE"));
        Add(duplicateReview, "DUPLICATE_REVIEW");
        Add(!f.B("po_exists") || f.S("po_status").ToLowerInvariant() is "closed" or "cancelled" or "canceled", "PROCUREMENT_REVIEW");
        Add(Yes("different_entity"), "PROCUREMENT_REVIEW");
        Add(objection || readings.Values.Contains("unsure") && !objection || f.N("invoice_age_days") > p.N("stale") && !f.B("invoice_approved_after_receipt"), "PROJECT_OWNER_REVIEW");
        if (Add(f.B("invoice_predates_po") || f.B("invoice_predates_contract"), "PROJECT_OWNER_REVIEW", "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        Add(f.B("records_contradict_vendor") && !objection, "PROJECT_OWNER_REVIEW");
        var materiality = p.B("materiality"); var deMinimis = 2 * p.N("review_cost");
        var vague = Yes("too_ambiguous") && total > p.N("vague") && !(materiality && total < deMinimis);
        Add(vague, "HOLD_REQUEST_DOCUMENTS");
        var stake = f.N("not_confirmed_total");
        Add(stake > p.N("vendor_only") && !approved && !(materiality && stake < deMinimis), "HOLD_REQUEST_DOCUMENTS");
        var relax = unqualified && p.B("owner_relaxes"); var outLines = new List<JsonNode>();
        foreach (var line in ls.Where(l => l.B("is_substantive")))
        {
            var category = line.S("category");
            if (category is "DUPLICATE_CHARGE" or "NOT_PERFORMED") { Add(true, "DISPUTE_LINES", "REQUEST_CORRECTED_INVOICE"); Carve(line.N("amount")); }
            else if (category == "DECLINED_BY_OWNER") { Add(true, "DISPUTE_LINES"); Carve(line.N("amount")); outLines.Add(line); }
            else if (category == "OUT_OF_SCOPE") { if (line.S("scope") == "not_ours" || !(vague || relax)) outLines.Add(line); }
            else if (category == "UNEXPLAINED_FEE") { Add(true, "REQUEST_CORRECTED_INVOICE"); Carve(line.N("amount")); }
            else if (line["quantity_supported_ratio"] is not null && line.N("quantity_supported_ratio") < .98) { Add(true, "DISPUTE_LINES", "REQUEST_CORRECTED_INVOICE"); Carve(Math.Round(line.N("amount") * (1 - line.N("quantity_supported_ratio")), 2)); }
        }
        var outTotal = Math.Round(outLines.Sum(l => l.N("amount")), 2);
        var scopeMaterial = !(materiality && Math.Min(outTotal, total) < deMinimis);
        if (outLines.Count > 0 && scopeMaterial)
        {
            Add(true, outTotal <= p.N("short_amount") && outTotal <= p.N("short_share") * total ? "DISPUTE_LINES" : "PROCUREMENT_REVIEW");
            foreach (var line in outLines.Where(l => l.S("category") != "DECLINED_BY_OWNER")) Carve(line.N("amount"));
        }
        var unsureLines = ls.Where(l => l.B("is_substantive") && l.S("scope") == "unsure").ToArray();
        Add(unsureLines.Length > 0 && !relax && !(materiality && Math.Round(unsureLines.Sum(l => l.N("amount")), 2) < deMinimis), "PROCUREMENT_REVIEW");
        if (Add(False("lines_sum_to_total"), "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        var taxTwice = f.B("tax_double_counted") && f.N("tax_line_amount") > 1;
        if (Add(taxTwice || Yes("tax_two_rates"), "REQUEST_CORRECTED_INVOICE") && taxTwice) Carve(f.N("tax_line_amount"));
        var adjustments = f.N("positive_adjustments_total");
        if (Add(adjustments > 0 && (adjustments > Math.Max(p.N("adjust_abs"), p.N("adjust_pct") * f.N("substantive_lines_total")) || Yes("adjustment_duplicates_line") || Yes("unexplained_charges") || f.N("positive_discount_lines_total") > 0), "REQUEST_CORRECTED_INVOICE")) Carve(adjustments);
        var overPrice = ls.Any(l => l.N("unit_price_variance_pct") > p.N("price_pct") && l.N("price_variance_amount") > p.N("price_floor"));
        if (Add(overPrice && !Yes("lines_restate_po_pricing") || ls.Any(l => l.B("rate_differs") && l.B("is_substantive")), "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        var basis = Choice(a, "price_basis", Bases, "none"); var above = Yes("billed_above_basis") && basis is not ("none" or "unsure");
        if (Add(above && basis is "unit_rates" or "period_fee", "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        if (Add(f.B("currency_mismatch"), "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        if (Add(f.B("invoiced_quantity_exceeds_received") && !f.B("acceptance_record_present"), "REQUEST_CORRECTED_INVOICE")) Carve(Math.Round(f.N("substantive_lines_total") * (1 - f.N("received_quantity_ratio")), 2));
        var payable = Math.Max(0, Math.Round(total - carved, 2));
        var priorBilled = (f.N("cumulative_billed") == 0 ? total : f.N("cumulative_billed")) - total;
        var feeExcess = f["contract_fee_basis"] is not null ? Math.Round(priorBilled + payable - f.N("contract_fee_basis"), 2) : 0;
        if (Add(feeExcess > .005, "DISPUTE_LINES", "REQUEST_CORRECTED_INVOICE")) { Carve(feeExcess); payable = Math.Max(0, Math.Round(total - carved, 2)); }
        if (Add(above && basis == "total_fee" && feeExcess <= .005, "DISPUTE_LINES", "REQUEST_CORRECTED_INVOICE")) wholeHold = true;
        var overrun = f["remaining_po_balance"] is not null && feeExcess <= .005 ? Math.Round(payable - f.N("remaining_po_balance"), 2) : 0;
        var accepted = approved || f.B("acceptance_record_present");
        Add(overrun > .005 || above && basis == "cap_only" && overrun <= .005, accepted ? "APPROVAL_ESCALATION" : "PROJECT_OWNER_REVIEW");
        if (f.N("retainage_pct") != 0) payable = Math.Round(payable * (1 - f.N("retainage_pct") / 100), 2);
        var required = policy == "enterprise" ? total <= 10000 ? 1 : total <= 50000 ? 2 : total <= 250000 ? 3 : 4
            : policy == "high_volume_retailer" ? total <= 100000 ? 1 : total <= 500000 ? 2 : 3
            : total <= 25000 ? 1 : total <= 100000 ? 2 : total <= 250000 ? 3 : 4;
        var tierUnmet = f.N("highest_approved_role_rank") < required; var segregation = total > p.N("segregation") && f.B("requester_is_sole_approver");
        var pendingApproval = f.S("invoice_approval_status") == "pending" || f.B("invoice_approved") && !approved;
        if (actions.Overlaps(Holds) || wholeHold) return Finish(actions);
        if (carved > 0)
        {
            var owner = approved || f.B("approval_is_qualified") && readings.Values.Contains("approves_except_lines") || f.B("invoice_approval_rejected") && !objection && outLines.Any(l => l.S("category") == "DECLINED_BY_OWNER");
            var allowance = carved <= p.N("short_amount") && carved <= p.N("short_share") * total;
            if (Add(payable > 0 && owner && allowance && !tierUnmet && !segregation, "SHORT_PAY")) return Finish(actions, payable);
            Add(!allowance, "REQUEST_CORRECTED_INVOICE"); return Finish(actions);
        }
        if (tierUnmet || segregation) return Finish(["HUMAN_APPROVAL"]);
        if (pendingApproval && !objection) return Finish(["SCHEDULE_PAYMENT"], payable);
        return Finish([f.B("is_overdue") || f["days_until_due"] is not null && f.N("days_until_due") <= 7 || f.B("discount_window_open") ? "PAY" : "SCHEDULE_PAYMENT"], payable);
    }
}
