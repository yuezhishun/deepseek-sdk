using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

// 直接移植官方 security_incidents/workflow.py 的有序 playbook；严格保留 > 与 <= 边界。
internal static class SecurityWorkflow
{
    internal static async Task<JsonArray> Run(JsonNode input, WorkflowSession session)
    {
        var a = await session.Ask("triage", input, Workflows.Panel("security", "triage"));
        var p = a.P("is_true_positive");
        if (p < .15 && a.P("context_explains_activity") > .5 && input["context"]!["asset"]!.N("tier", 2) != 0) return [Workflows.Single("playbook", "auto_close")];
        if (p <= .75)
        {
            var identity = input.S("alert").Split('-')[0].Trim().ToUpperInvariant() == "IDP";
            return [Workflows.Single("playbook", identity && p > .15 && p <= .60 ? "notify_user" : "escalate_tier2")];
        }
        a = await session.Ask("containment", input, Workflows.Panel("security", "containment"));
        bool P(string q, double threshold) => a.P(q) > threshold;
        string action;
        if (P("outbound_channel_active", .45)) action = a.C("affected_scope") == "organization_wide" ? "block_destination" : "block_egress_asset";
        else if (P("session_in_attacker_hands", .5) || P("credentials_exposed", .4))
        {
            action = P("session_in_attacker_hands", .65) && P("spread_beyond_initial_entity", .5) ? "disable_account"
                : P("credentials_exposed", .6) && a.C("attack_type") == "exfiltration" ? "revoke_access_key"
                : P("session_in_attacker_hands", .5) || P("credentials_exposed", .6) ? "revoke_sessions" : "require_reauth";
        }
        else if (P("malicious_content_in_mailboxes", .5)) action = a.C("attack_type") == "mail_campaign" && a.C("affected_scope") != "single_entity" ? "block_sender" : P("malicious_content_in_mailboxes", .7) ? "purge_mailboxes" : "quarantine_message";
        else if (P("attacker_persistence_present", .6) || P("malicious_process_running", .6))
        {
            var permitted = input["context"]!["asset"].S("environment", "dev") != "prod" || P("activity_ongoing", .5) && P("spread_beyond_initial_entity", .5);
            action = P("attacker_persistence_present", .6) && P("activity_ongoing", .5) && permitted ? "isolate_host" : P("malicious_process_running", .6) ? "kill_process" : "quarantine_file";
        }
        else action = P("attacker_modified_configuration", .6) ? "remove_forwarding_rules" : "escalate_urgent";
        return [Workflows.Single("playbook", action)];
    }
}
