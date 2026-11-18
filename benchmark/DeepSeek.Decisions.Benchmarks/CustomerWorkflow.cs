using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class CustomerWorkflow
{
    private static readonly JsonNode Policies = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "customer-policies.json")))!;
    internal static async Task<JsonArray> Run(JsonNode c, WorkflowSession session)
    {
        var account = c["account"]!; var customer = c["customer"]!; var state = c["state"]!;
        var conversation = c["transcript"]!;
        var a = new Dictionary<string, SystemOneAnswer>();
        async Task<Dictionary<string, SystemOneAnswer>> Ask(string node, JsonNode input, JsonObject? qs = null)
        {
            var answers = await session.Ask(node, input, qs ?? Workflows.Panel("customer", node));
            foreach (var item in answers) a[item.Key] = item.Value;
            a["__ran__" + node] = new() { Type = "noul", Noul = 1 };
            return answers;
        }
        var tri = await Ask("triage", J.O(("conversation", conversation), ("customer", customer),
            ("account_summary", J.O(("card_status", account["card_status"]), ("transaction_on_file", account["transaction"] is not null), ("refund_claim_on_file", account["refund"] is not null), ("fee_on_file", account.N("outstanding_fee_usd") > 0), ("order_on_file", account["order"] is not null), ("subscription_active", account.B("subscription_active")))), ("assistant_pending_proposal", state["pending"])));
        var intent = tri["intent"];
        if (state["pending"] is not null)
        {
            var proposal = conversation.Items().SkipLast(1).LastOrDefault(t => t.S("speaker") == "assistant");
            JsonObject? terms = state.S("pending") switch
            {
                "issue_refund" when account["refund"] is not null => J.O(("amount_usd", account["refund"]!["amount_usd"])),
                "cancel_subscription" => J.O(("monthly_usd", account["subscription_monthly_usd"])),
                "freeze_card" or "open_dispute" when account["transaction"] is not null => J.O(("transaction", account["transaction"])),
                _ => null,
            };
            await Ask("consent", J.O(("pending_proposal", state["pending"]), ("proposed_terms_on_record", terms), ("assistant_proposal_message", proposal?["text"]), ("customer_reply", conversation.Items().Last()["text"])));
        }
        if (tri.P("reports_unauthorized") >= .30 || intent.Choice == "unauthorized_charge")
            await Ask("fraud", J.O(("conversation", conversation), ("ledger_entry", (object?)account["transaction"] ?? "no matching entry on file"), ("card_status", account["card_status"])));
        if ((account["refund"] is not null || account.N("outstanding_fee_usd") > 0) && (tri.Prob("desired_outcome", "money_back") >= .2 || intent.Choice is "refund_request" or "billing_dispute"))
        {
            var refund = account["refund"];
            var claim = refund is not null ? J.O(("kind", "refund_claim"), ("amount_usd", refund["amount_usd"]), ("eligible_under_policy", refund["eligible"]), ("already_refunded", refund["already_issued"])) : J.O(("kind", "fee"), ("amount_usd", account["outstanding_fee_usd"]));
            await Ask("money", J.O(("conversation", conversation), ("on_file", claim), ("order", account["order"]), ("customer", J.O(("tenure_months", customer["tenure_months"]), ("prior_refunds_90d", customer["prior_refunds_90d"])))));
        }
        if (intent.Choice == "cancel_account" || account.B("subscription_active") && tri.Prob("intent", "cancel_account") >= .15)
            await Ask("retention", J.O(("conversation", conversation), ("subscription", J.O(("active", account["subscription_active"]), ("monthly_usd", account["subscription_monthly_usd"]))), ("customer", customer)));
        var ranked = intent.Probabilities!.OrderByDescending(p => p.Value).ToArray();
        if (ranked[0].Value - ranked[1].Value < .25)
        {
            var names = Workflows.Panel("customer", "intents");
            var first = ranked[0].Key; var second = ranked[1].Key;
            var pair = await Ask("intent_pair", J.O(("customer_messages", J.A(conversation.Items().Where(t => t.S("speaker") == "customer").Select(t => t["text"])))),
                J.O(("intent_pair", J.O(("type", "choice"), ("instructions", "Which of these two better describes the customer's primary issue, judged by the outcome they want above all else?"), ("criteria", J.O((first, names[first]), (second, names[second])))))));
            a["intent_triage"] = intent;
            var mass = ranked[0].Value + ranked[1].Value;
            var probabilities = new Dictionary<string, double>(intent.Probabilities!);
            probabilities[first] = mass * pair.Prob("intent_pair", first); probabilities[second] = mass * pair.Prob("intent_pair", second);
            a["intent"] = new() { Type = "choice", Choice = probabilities.MaxBy(p => p.Value).Key, Probabilities = probabilities, Confidence = Answers.ChoiceConfidence(probabilities) };
        }
        var turns = conversation.Items().ToArray();
        var customerIndexes = turns.Select((t, i) => (t, i)).Where(t => t.t.S("speaker") == "customer").Select(t => t.i).ToArray();
        var start = customerIndexes.Length >= 2 ? customerIndexes[^2] : -1;
        var messages = new JsonArray(); var numbers = new List<int>(); var k = 0;
        for (var i = 0; i < turns.Length; i++)
            if (turns[i].S("speaker") == "assistant") { messages.Add(J.O(("number", k), ("text", turns[i]["text"]))); if (i > start) numbers.Add(k); k++; }
        if (numbers.Count > 0 && (a.ContainsKey("__ran__fraud") || a.ContainsKey("__ran__money")))
        {
            var qs = new JsonObject();
            foreach (var number in numbers)
                foreach (var (id, q) in Workflows.Panel("customer", "integrity"))
                {
                    var question = q!.DeepClone();
                    question["instructions"] = question.S("instructions").Replace("number 0,", $"number {number},", StringComparison.Ordinal);
                    qs[id.Replace("_0", "_" + number, StringComparison.Ordinal)] = question;
                }
            var ig = await Ask("integrity", J.O(("assistant_messages", messages), ("record", J.O(("refund_issued", account["refund"].B("already_issued")), ("card_status", account["card_status"])))), qs);
            foreach (var (claim, prefix) in new[] { ("claims_refund_done", "refund_done_msg_"), ("claims_secured", "secured_msg_") })
                a[claim] = new() { Type = "noul", Noul = 1 - numbers.Aggregate(1.0, (value, number) => value * (1 - ig.P(prefix + number))) };
        }
        return new JsonArray(new[] { "A", "B", "C" }.Select(policy => (JsonNode)Workflows.Decision(policy, Decide(policy, c, a))).ToArray());
    }
    internal static List<JsonObject> Decide(string policy, JsonNode c, Dictionary<string, SystemOneAnswer> a)
    {
        if (c["state"].B("handed_off")) return [];
        var sections = Policies[policy]!;
        if (policy == "C")
        {
            var names = new[] { "high", "mid", "low", "unknown" };
            var rules = sections["stage1"].Items().ToArray();
            var tier = Enumerable.Range(0, rules.Length).First(i => Holds(rules[i]["args"]![1]!, c, a));
            sections = sections["tiers"]![names[tier]]!;
        }
        var actions = new List<JsonObject>();
        foreach (var section in sections.Items())
        {
            var stop = false;
            foreach (var rule in section["args"]![1].Items())
            {
                if (!Holds(rule["args"]![1]!, c, a)) continue;
                foreach (var emit in rule["args"]![2].Items()) actions.Add(Emit(emit, c, a));
                stop |= rule["stop"] is null || rule.B("stop");
                if (section.S("mode", "first") == "first") break;
            }
            if (stop) break;
        }
        var seen = new HashSet<string>(); var responded = false;
        return actions.Where(action =>
        {
            if (action.S("name") == "respond_with") { if (responded) return false; responded = true; }
            return seen.Add(Scoring.ActionKey(action));
        }).ToList();
    }
    internal static bool Holds(JsonNode condition, JsonNode c, Dictionary<string, SystemOneAnswer> a)
    {
        var args = condition["args"]!.AsArray(); var kind = condition.S("kind");
        string Q() => args[0].S();
        return kind switch
        {
            "Always" => true,
            "All" => args.All(item => Holds(item!, c, a)),
            "Any" => args.Any(item => Holds(item!, c, a)),
            "Not" => !Holds(args[0]!, c, a),
            "NodeRan" => a.ContainsKey("__ran__" + Q()),
            "NoulGate" => a.P(Q()) > (args.Count > 1 ? args[1].N() : .5),
            "ChoiceIn" => args[1].Items().Any(option => option.S() == a.C(Q())),
            "ChoiceTop" => a.Confidence(Q()) > args[1].N() && (args.Count < 3 || a.C(Q()) == args[2].S()),
            "ScoreTail" => a.ContainsKey(Q()) && (args.Count > 3 && args[3].S() == "<" ? a.Tail(Q(), (int)args[1].N()) < args[2].N() : a.Tail(Q(), (int)args[1].N()) > args[2].N()),
            "ScoreExpect" => a.ContainsKey(Q()) && a.Expected(Q()) > args[1].N() && a.Confidence(Q()) > args[2].N(),
            _ when kind.StartsWith("Fact:") => Fact(kind[5..], args, c),
            _ => throw new InvalidDataException("未知客服条件：" + kind),
        };
    }
    private static bool Fact(string name, JsonArray args, JsonNode c)
    {
        var state = c["state"]!; var account = c["account"]!; var customer = c["customer"]!; var refund = account["refund"];
        return name switch
        {
            "handed_off" => state.B("handed_off"), "no_pending" => state["pending"] is null, "intent_unset" => state["intent"] is null,
            "pending_is" => state.S("pending") == args[0].S(), "intent_is" => state.S("intent") == args[0].S(),
            "card_active" => account.S("card_status") == "active", "card_frozen" => account.S("card_status") == "frozen",
            "has_transaction" => account["transaction"] is not null, "no_transaction" => account["transaction"] is null,
            "has_refund_claim" => refund is not null, "no_refund_claim" => refund is null,
            "refund_eligible" => refund is not null && refund.B("eligible") && !refund.B("already_issued"),
            "refund_ineligible" => refund is not null && !refund.B("eligible") && !refund.B("already_issued"),
            "refund_already_issued" => refund.B("already_issued"), "refund_not_issued" => !refund.B("already_issued"),
            "refund_at_most" => refund is not null && refund.N("amount_usd") <= args[0].N(), "refund_over" => refund is not null && refund.N("amount_usd") > args[0].N(),
            "has_fee" => account.N("outstanding_fee_usd") > 0, "has_order" => account["order"] is not null, "no_order" => account["order"] is null,
            "subscription_active" => account.B("subscription_active"), "tenure_at_least" => customer.N("tenure_months") >= args[0].N(),
            "prior_refunds_under" => customer.N("prior_refunds_90d") < args[0].N(), "vip" => customer.B("vip"),
            "channel_is" => args.Any(v => v.S() == account["transaction"].S("channel")),
            _ => throw new InvalidDataException("未知客服事实：" + name),
        };
    }
    private static JsonObject Emit(JsonNode emit, JsonNode c, Dictionary<string, SystemOneAnswer> a)
    {
        var value = emit["args"]![0].S();
        var account = c["account"]!;
        return emit.S("kind") switch
        {
            "R" => Scoring.Action("respond_with", J.O(("message", value))),
            "pending" => Scoring.Action("set_pending", J.O(("proposal", value))),
            "handoff" => Scoring.Action("handoff_human", J.O(("queue", value))),
            "flag" => Scoring.Action("flag_for_review", J.O(("reason", value))),
            "priority" => Scoring.Action("set_priority", J.O(("level", value))),
            "emit" => value switch
            {
                "FREEZE" => Scoring.Action("freeze_card", J.O(("card_id", account["card_id"]))),
                "REFUND" => Scoring.Action("issue_refund", J.O(("amount_usd", Math.Round(account["refund"].N("amount_usd"), 2, MidpointRounding.ToEven)))),
                "WAIVE_FEE" => Scoring.Action("issue_refund", J.O(("amount_usd", Math.Round(account.N("outstanding_fee_usd"), 2, MidpointRounding.ToEven)))),
                "DISPUTE" => Scoring.Action("open_dispute", J.O(("transaction_id", account["transaction"]?["transaction_id"]))),
                "SET_INTENT" => Scoring.Action("set_intent", J.O(("intent", a.C("intent")))),
                "CANCEL" => Scoring.Action("cancel_subscription"), "RETENTION" => Scoring.Action("apply_retention_offer"),
                "CLOSE" => Scoring.Action("close_ticket"), "REDACT" => Scoring.Action("redact_transcript"),
                "CLEAR_PENDING" => Scoring.Action("clear_pending"), "MARK_RESOLVED" => Scoring.Action("mark_resolved"),
                _ => throw new InvalidDataException("未知动作：" + value),
            },
            _ => throw new InvalidDataException("未知客服动作：" + emit.S("kind")),
        };
    }
}
