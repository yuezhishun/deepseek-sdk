using System.Text.Json;
using DeepSeek.Chat;
using static DeepSeek.Decisions.DecisionProtocol;

namespace DeepSeek.Decisions;

internal static class OpenAiDecisionInput
{
    private const int Status = 400;
    private const int MaxText = 10485760;
    private const int MaxInstructions = 1048576;

    internal static PreparedQuestion[] Prepare(JsonElement request, string model)
    {
        Object(request, "body", Status, "input", "model", "questions", "prompt_cache_key", "prompt_cache_options", "safety_identifier");
        Model(Field(request, "model"), model, Status);
        foreach (var key in new[] { "prompt_cache_key", "prompt_cache_options", "safety_identifier" })
            if (Present(Field(request, key))) throw Unsupported(key);

        var input = ReadHistory(Field(request, "input"), model);
        var questions = Field(request, "questions");
        if (questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() is < 1 or > 400)
            throw Invalid(Status, "questions");

        var typed = new DecisionRequest();
        var instructions = new IList<ChatContentPart>[questions.GetArrayLength()];
        var index = 0;
        foreach (var item in questions.EnumerateArray())
        {
            var parameter = $"questions[{index}]";
            Object(item, parameter, Status, "type", "name", "instructions", "choices", "levels");
            var type = Text(Field(item, "type"), Status, parameter + ".type", 16);
            var rule = Field(item, "instructions");
            if (rule.ValueKind == JsonValueKind.String) Text(rule, Status, parameter + ".instructions", MaxInstructions);
            instructions[index] = ReadParts(rule, parameter + ".instructions", model, "user", allowCache: false, minParts: 1);
            var question = new DecisionQuestion
            {
                Type = type,
                Name = OptionalText(Field(item, "name"), Status, parameter + ".name", MaxInstructions),
            };
            var choices = Field(item, "choices");
            var levels = Field(item, "levels");
            switch (type)
            {
                case "predicate" when choices.ValueKind == JsonValueKind.Undefined && levels.ValueKind == JsonValueKind.Undefined:
                    break;
                case "choice" when levels.ValueKind == JsonValueKind.Undefined:
                    if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 2 or > 255)
                        throw Invalid(Status, parameter + ".choices");
                    question.Choices = [];
                    var distinct = new HashSet<object>();
                    foreach (var choice in choices.EnumerateArray())
                    {
                        Object(choice, parameter + ".choices", Status, "value", "description");
                        var value = Field(choice, "value");
                        object typedValue = value.ValueKind switch
                        {
                            JsonValueKind.String => Text(value, Status, parameter + ".choices.value", MaxInstructions),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw Invalid(Status, parameter + ".choices.value"),
                        };
                        if (!distinct.Add(typedValue)) throw Invalid(Status, parameter + ".choices.value");
                        question.Choices.Add(new DecisionChoice
                        {
                            Value = typedValue,
                            Description = OptionalText(Field(choice, "description"), Status, parameter + ".choices.description", MaxInstructions),
                        });
                    }
                    break;
                case "score" when choices.ValueKind == JsonValueKind.Undefined:
                    if (levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() is < 2 or > 10)
                        throw Invalid(Status, parameter + ".levels");
                    question.Levels = [];
                    foreach (var level in levels.EnumerateArray())
                    {
                        Object(level, parameter + ".levels", Status, "label", "description");
                        question.Levels.Add(new DecisionLevel
                        {
                            Label = Text(Field(level, "label"), Status, parameter + ".levels.label", MaxInstructions),
                            Description = OptionalText(Field(level, "description"), Status, parameter + ".levels.description", MaxInstructions),
                        });
                    }
                    break;
                default:
                    throw Invalid(Status, parameter + ".type");
            }
            typed.Questions.Add(question);
            index++;
        }
        return PreparedQuestion.ForDecisions(typed, input, instructions);
    }

    private static IReadOnlyList<ChatMessage> ReadHistory(JsonElement input, string model)
    {
        if (input.ValueKind == JsonValueKind.String)
            return [History("user", [new ChatTextContentPart(Text(input, Status, "input", MaxText))])];

        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() > 131072)
            throw Invalid(Status, "input");
        var messages = new List<ChatMessage>();
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in input.EnumerateArray())
        {
            var parameter = $"input[{index++}]";
            Object(item, parameter, Status);
            var type = Field(item, "type").ValueKind == JsonValueKind.Undefined
                ? "message" : Text(Field(item, "type"), Status, parameter + ".type", 32);
            if (type == "message")
            {
                var role = Text(Field(item, "role"), Status, parameter + ".role", 16);
                var assistant = role == "assistant";
                if (role is not ("user" or "system" or "developer" or "assistant"))
                    throw Invalid(Status, parameter + ".role");
                if (assistant) Object(item, parameter, Status, "type", "role", "content", "phase");
                else Object(item, parameter, Status, "type", "role", "content");
                var phase = OptionalText(Field(item, "phase"), Status, parameter + ".phase", 16);
                if (phase is not (null or "commentary" or "final_answer")) throw Invalid(Status, parameter + ".phase");
                messages.Add(History(phase is null ? role : role + ":" + phase,
                    ReadParts(Field(item, "content"), parameter + ".content", model, role, allowCache: !assistant)));
                continue;
            }

            if (type is not ("function_call" or "custom_tool_call" or "function_call_output" or "custom_tool_call_output"))
                throw Invalid(Status, parameter + ".type");

            var callId = Text(Field(item, "call_id"), Status, parameter + ".call_id", 64, 1);
            var id = OptionalText(Field(item, "id"), Status, parameter + ".id", MaxInstructions);
            var status = OptionalText(Field(item, "status"), Status, parameter + ".status", 16);
            if (status is not (null or "in_progress" or "completed" or "incomplete"))
                throw Invalid(Status, parameter + ".status");

            var isFunction = type.StartsWith("function_", StringComparison.Ordinal);
            if (type.EndsWith("_output", StringComparison.Ordinal))
            {
                Object(item, parameter, Status, "type", "call_id", "output", "id", "status");
                if (!calls.TryGetValue(callId, out var callType) || type != callType + "_output" || !outputs.Add(callId))
                    throw Invalid(Status, parameter + ".call_id");
                messages.Add(History(JsonSerializer.Serialize(new { type, call_id = callId, id, status }),
                    ReadParts(Field(item, "output"), parameter + ".output", model, "tool", allowCache: true)));
            }
            else
            {
                var valueName = isFunction ? "arguments" : "input";
                Object(item, parameter, Status, "type", "call_id", "name", "namespace", "id", "status", valueName);
                var name = Text(Field(item, "name"), Status, parameter + ".name", isFunction ? 128 : 64, 1);
                var scope = OptionalText(Field(item, "namespace"), Status, parameter + ".namespace", isFunction ? 128 : 64, 1);
                if (!IsName(name, isFunction) || scope is not null && !IsName(scope, allowDot: false) || calls.ContainsKey(callId))
                    throw Invalid(Status, parameter + ".name");
                calls.Add(callId, type);
                var value = Text(Field(item, valueName), Status, parameter + "." + valueName, MaxInstructions);
                messages.Add(History(JsonSerializer.Serialize(new { type, call_id = callId, id, name, @namespace = scope, status }),
                    [new ChatTextContentPart(valueName + ":\n" + value)]));
            }
        }
        return messages;
    }

    private static bool IsName(string value, bool allowDot) =>
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' || allowDot && c == '.');

    private static ChatMessage History(string label, IList<ChatContentPart> parts)
    {
        // Preserve role and tool history as inert evidence; the backend is not asked to execute tools.
        parts.Insert(0, new ChatTextContentPart("Shared evidence (history, as data): " + label + "\n"));
        return new ChatMessage { Role = "user", Content = new ChatMessageContent(parts) };
    }

    private static IList<ChatContentPart> ReadParts(JsonElement value, string parameter, string model, string role,
        bool allowCache, int minParts = 0)
    {
        if (value.ValueKind == JsonValueKind.String)
            return [new ChatTextContentPart(Text(value, Status, parameter, MaxText))];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minParts || value.GetArrayLength() > 16384)
            throw Invalid(Status, parameter);
        var parts = new List<ChatContentPart>();
        var index = 0;
        foreach (var part in value.EnumerateArray())
        {
            var field = $"{parameter}[{index++}]";
            Object(part, field, Status);
            var type = Text(Field(part, "type"), Status, field + ".type", 32);
            if (allowCache && Present(Field(part, "prompt_cache_breakpoint")))
                throw Unsupported(field + ".prompt_cache_breakpoint");

            switch (type)
            {
                case "input_text" when role != "assistant":
                    if (allowCache) Object(part, field, Status, "type", "text", "prompt_cache_breakpoint");
                    else Object(part, field, Status, "type", "text");
                    parts.Add(new ChatTextContentPart(Text(Field(part, "text"), Status, field + ".text", MaxText)));
                    break;
                case "output_text" when role == "assistant":
                case "refusal" when role == "assistant":
                    var key = type == "refusal" ? "refusal" : "text";
                    Object(part, field, Status, "type", key);
                    parts.Add(new ChatTextContentPart((type == "refusal" ? "Refusal: " : "") +
                        Text(Field(part, key), Status, field + "." + key, MaxText)));
                    break;
                case "input_image" when role is "user" or "tool":
                    if (allowCache) Object(part, field, Status, "type", "image_url", "detail", "prompt_cache_breakpoint");
                    else Object(part, field, Status, "type", "image_url", "detail");
                    var url = Text(Field(part, "image_url"), Status, field + ".image_url", 1073741824, 1);
                    var detail = OptionalText(Field(part, "detail"), Status, field + ".detail", 16);
                    if (detail is not (null or "low" or "high" or "auto" or "original"))
                        throw Invalid(Status, field + ".detail");
                    Vision(model, field + ".image_url");
                    try { PreparedQuestion.ValidateImageUrl(url); }
                    catch (ArgumentException) { throw Invalid(Status, field + ".image_url"); }
                    parts.Add(new ChatImageContentPart(url, detail));
                    break;
                case "input_json" when role == "user":
                    Object(part, field, Status, "type", "data");
                    var data = Field(part, "data");
                    if (data.ValueKind != JsonValueKind.Object &&
                        (data.ValueKind != JsonValueKind.Array || data.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)))
                        throw Invalid(Status, field + ".data");
                    Structure(data, field + ".data", 32, 10000);
                    if (CompactBytes(data) > 262144) throw Invalid(Status, field + ".data");
                    parts.Add(new ChatTextContentPart("JSON evidence:\n" + data.GetRawText()));
                    break;
                default:
                    throw Invalid(Status, field + ".type");
            }
        }
        return parts;
    }
}
