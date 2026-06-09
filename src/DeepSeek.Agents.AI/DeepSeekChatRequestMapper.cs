using DeepSeek.Chat;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using WireChatMessage = DeepSeek.Chat.ChatMessage;
using WireChatTool = DeepSeek.Chat.ChatTool;
using WireChatToolChoice = DeepSeek.Chat.ChatToolChoice;
using WireChatToolChoiceFunction = DeepSeek.Chat.ChatToolChoiceFunction;
using WireChatToolFunction = DeepSeek.Chat.ChatToolFunction;

namespace DeepSeek.Agents.AI;

internal static class DeepSeekChatRequestMapper
{
    internal const string DisableStreamingToolContinuationKey = "deepseek_internal_disable_streaming_tool_continuation";
    private const string JsonPrompt = 
@"你必须严格只输出一个合法的 JSON 实例，不要有任何额外的文字说明、标记、前缀或后缀。
不要使用 Markdown 代码块（如 ```json）包裹。
响应体必须以 JSON 的第一个字符（{ 或 [）开始，以最后一个字符（} 或 ]）结束。
JSON 必须语法完全正确：所有键用双引号，不允许尾随逗号，不允许注释，所有双引号、反斜杠和控制字符必须正确转义。
如果内部需要表示 JSON 字符串，必须使用转义后的双引号。

重要：下面提供了一个 JSON Schema，你必须根据它生成一个符合该模式的数据实例，而不是输出 Schema 本身。
如果 Schema 的 ""type"" 是 ""object""，就输出一个 JSON 对象（以 { 开始）；如果是 ""array""，就输出一个 JSON 数组（以 [ 开始）。

示例（仅用于说明模式与实例的关系）：
Schema:
{""$schema"":""https://json-schema.org/draft/2020-12/schema"",""type"":""object"",""properties"":{""name"":{""type"":""string""},""age"":{""type"":""integer""}}}
对应的正确输出：
{""name"":""Alice"",""age"":30}

现在，请根据以下 JSON Schema 以及对话上下文生成所需的数据实例：";
    private static readonly JsonSerializerOptions CompactJsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static ChatOptions MergeChatOptions(ChatOptions? defaults, ChatOptions? overrides)
    {
        var merged = defaults?.Clone() ?? new ChatOptions();
        if (overrides is null)
        {
            return merged;
        }

        merged.Instructions = overrides.Instructions ?? merged.Instructions;
        merged.Temperature = overrides.Temperature ?? merged.Temperature;
        merged.MaxOutputTokens = overrides.MaxOutputTokens ?? merged.MaxOutputTokens;
        merged.TopP = overrides.TopP ?? merged.TopP;
        merged.TopK = overrides.TopK ?? merged.TopK;
        merged.Seed = overrides.Seed ?? merged.Seed;
        merged.Reasoning = overrides.Reasoning ?? merged.Reasoning;
        merged.ResponseFormat = overrides.ResponseFormat ?? merged.ResponseFormat;
        merged.StopSequences = overrides.StopSequences ?? merged.StopSequences;
        merged.AllowMultipleToolCalls = overrides.AllowMultipleToolCalls ?? merged.AllowMultipleToolCalls;
        merged.ToolMode = overrides.ToolMode ?? merged.ToolMode;
        merged.Tools = overrides.Tools ?? merged.Tools;
        merged.AdditionalProperties = MergeAdditionalProperties(merged.AdditionalProperties, overrides.AdditionalProperties);
        return merged;
    }

    internal static AdditionalPropertiesDictionary? MergeAdditionalProperties(
        AdditionalPropertiesDictionary? defaults,
        AdditionalPropertiesDictionary? overrides)
    {
        if (defaults is null && overrides is null)
        {
            return null;
        }

        var result = new AdditionalPropertiesDictionary();
        if (defaults is not null)
        {
            foreach (var pair in defaults)
            {
                result[pair.Key] = pair.Value;
            }
        }

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    internal static AdditionalPropertiesDictionary? CloneAdditionalProperties(AdditionalPropertiesDictionary? properties)
        => MergeAdditionalProperties(properties, null);

    internal static ChatCompletionRequest MapToChatRequest(IEnumerable<AiChatMessage> messages, ChatOptions? options, bool stream)
    {
        var jsonResponseFormat = options?.ResponseFormat as ChatResponseFormatJson;
        var request = new ChatCompletionRequest
        {
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            MaxTokens = options?.MaxOutputTokens,
            ResponseFormat = jsonResponseFormat is not null ? new ResponseFormat { Type = ChatResponseFormatTypes.JsonObject } : null,
            Stop = options?.StopSequences?.ToList(),
        };

        var instructions = BuildInstructions(options?.Instructions, jsonResponseFormat);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            request.Messages.Add(new WireChatMessage { Role = "system", Content = instructions });
        }

        foreach (var message in messages)
        {
            foreach (var mappedMessage in MapMessages(message))
            {
                request.Messages.Add(mappedMessage);
            }
        }

        request.Tools = MapTools(options?.Tools);
        request.ToolChoice = MapToolChoice(options);
        ApplyAdditionalProperties(request, options, stream);
        request.Stream = stream;

        if (options?.Reasoning is not null)
        {
            request.Thinking = request.Thinking ?? ThinkingMode.Enabled;
            request.ReasoningEffort ??= options.Reasoning.Effort == ReasoningEffort.ExtraHigh
                ? ChatReasoningEffort.Max
                : ChatReasoningEffort.High;
        }

        return request;
    }

    private static string? BuildInstructions(string? instructions, ChatResponseFormatJson? responseFormat)
    {
        var augmentation = BuildJsonResponseFormatInstructions(responseFormat);
        if (string.IsNullOrWhiteSpace(augmentation))
        {
            return instructions;
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            return augmentation;
        }

        return (instructions ?? string.Empty).TrimEnd() + Environment.NewLine + Environment.NewLine + augmentation;
    }

    private static string? BuildJsonResponseFormatInstructions(ChatResponseFormatJson? responseFormat)
    {
        if (responseFormat is null)
        {
            return null;
        }

        JsonElement? responseSchema = responseFormat.Schema;
        var hasSchema = responseSchema is JsonElement presentSchema && presentSchema.ValueKind != JsonValueKind.Undefined;

        if (!hasSchema)
        {
            return """
                You must reply with valid json.
                Return only a single JSON object.
                Do not wrap the response in Markdown fences.
                Do not add explanatory text before or after the json object.
                """;
        }

        JsonElement schemaElement = responseSchema!.Value;
        return new StringBuilder()
            .AppendLine(JsonPrompt)
            .AppendLine(SerializeCompactJson(schemaElement))
            .ToString()
            .TrimEnd();
    }

    internal static ChatResponse MapToChatResponse(ChatCompletion response)
    {
        var choice = response.Choices.FirstOrDefault();
        var message = choice?.Message;
        var assistant = new AiChatMessage(ChatRole.Assistant, message?.Content ?? string.Empty)
        {
            RawRepresentation = message,
        };

        var reasoningContent = message?.ReasoningContent;
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            assistant.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_content"] = reasoningContent,
            };
            assistant.Contents.Add(new TextReasoningContent(reasoningContent));
        }

        if (message?.ToolCalls is { } toolCalls)
        {
            foreach (var toolCall in toolCalls)
            {
                var function = toolCall.Function;
                assistant.Contents.Add(new FunctionCallContent(
                    toolCall.Id ?? string.Empty,
                    function?.Name ?? string.Empty,
                    ParseArguments(function?.Arguments)));
            }
        }

        var result = new ChatResponse(new[] { assistant })
        {
            ModelId = response.Model,
            AdditionalProperties = new AdditionalPropertiesDictionary(),
        };

        if (response.Usage is not null)
        {
            result.Usage = new UsageDetails
            {
                InputTokenCount = response.Usage.PromptTokens,
                OutputTokenCount = response.Usage.CompletionTokens,
                TotalTokenCount = response.Usage.TotalTokens,
            };
            result.AdditionalProperties["usage_total_tokens"] = response.Usage.TotalTokens;
        }

        var finishReason = choice?.FinishReason;
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            result.AdditionalProperties["finish_reason"] = finishReason;
        }

        return result;
    }

    private static IEnumerable<WireChatMessage> MapMessages(AiChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            yield return new WireChatMessage { Role = "system", Content = message.Text };
            yield break;
        }

        if (message.Role == ChatRole.User)
        {
            yield return new WireChatMessage { Role = "user", Content = message.Text };
            yield break;
        }

        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count == 0)
            {
                yield return new WireChatMessage
                {
                    Role = "tool",
                    Content = SerializeToolResult(message.Text),
                    ToolCallId = TryGetString(message, "tool_call_id"),
                };
                yield break;
            }

            foreach (var result in results)
            {
                yield return new WireChatMessage
                {
                    Role = "tool",
                    Content = SerializeToolResult(result.Result),
                    ToolCallId = result.CallId,
                };
            }

            yield break;
        }

        var assistant = new WireChatMessage
        {
            Role = "assistant",
            Content = message.Text,
            ReasoningContent = TryGetReasoningContent(message),
        };

        var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            assistant.ToolCalls = functionCalls.Select(static call => new ToolCall
            {
                Id = call.CallId,
                Function = new ToolCallFunction
                {
                    Name = call.Name,
                    Arguments = JsonSerializer.Serialize(call.Arguments),
                },
            }).ToList();
        }

        yield return assistant;
    }

    private static IList<WireChatTool>? MapTools(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        return tools.Select(MapTool).ToList();
    }

    private static WireChatTool MapTool(AITool tool)
    {
        if (tool.GetService<DelegatingAIFunction>() is { } delegatingFunction)
        {
            return CreateWireChatTool(
                delegatingFunction.Name,
                delegatingFunction.Description,
                delegatingFunction.JsonSchema.GetRawText(),
                delegatingFunction.AdditionalProperties);
        }

        if (tool.GetService<AIFunction>() is { } function)
        {
            return CreateWireChatTool(function.Name, function.Description, function.JsonSchema.GetRawText(), function.AdditionalProperties);
        }

        if (tool.GetService<AIFunctionDeclaration>() is { } declaration)
        {
            return CreateWireChatTool(declaration.Name, declaration.Description, declaration.JsonSchema.GetRawText(), declaration.AdditionalProperties);
        }

        throw new NotSupportedException("Unsupported AITool type: " + tool.GetType().FullName);
    }

    private static WireChatTool CreateWireChatTool(
        string name,
        string? description,
        string jsonSchema,
        IReadOnlyDictionary<string, object?>? additionalProperties = null)
    {
        return new WireChatTool
        {
            Function = new WireChatToolFunction
            {
                Name = name,
                Description = description,
                Parameters = JsonNode.Parse(jsonSchema) ?? new JsonObject(),
                Strict = TryGetStrictValue(additionalProperties),
            },
        };
    }

    private static object? MapToolChoice(ChatOptions? options)
    {
        switch (options?.ToolMode)
        {
            case AutoChatToolMode:
                return "auto";
            case RequiredChatToolMode:
                return "required";
            case NoneChatToolMode:
                return "none";
        }

        if (options?.AdditionalProperties is not null &&
            options.AdditionalProperties.TryGetValue(DeepSeekChatOptionKeys.ToolChoiceName, out var value) &&
            value is string name &&
            !string.IsNullOrWhiteSpace(name))
        {
            return new WireChatToolChoice
            {
                Function = new WireChatToolChoiceFunction { Name = name },
            };
        }

        return null;
    }

    private static void ApplyAdditionalProperties(ChatCompletionRequest request, ChatOptions? options, bool stream)
    {
        var properties = options?.AdditionalProperties;
        if (properties is null)
        {
            return;
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.Thinking, out var thinking))
        {
            request.Thinking = thinking switch
            {
                bool enabled => enabled ? ThinkingMode.Enabled : ThinkingMode.Disabled,
                string text when text == "enabled" => ThinkingMode.Enabled,
                string text when text == "disabled" => ThinkingMode.Disabled,
                _ => request.Thinking,
            };
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.ReasoningEffort, out var effort) && effort is string effortText)
        {
            request.ReasoningEffort = effortText == "max" ? ChatReasoningEffort.Max : ChatReasoningEffort.High;
        }

        if (stream &&
            properties.TryGetValue(DeepSeekChatOptionKeys.IncludeUsage, out var includeUsage) &&
            includeUsage is bool includeUsageValue)
        {
            request.StreamOptions = new StreamOptions { IncludeUsage = includeUsageValue };
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.Logprobs, out var logprobs) && logprobs is bool logprobsValue)
        {
            request.Logprobs = logprobsValue;
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.TopLogprobs, out var topLogprobs) && topLogprobs is int topLogprobsValue)
        {
            request.TopLogprobs = topLogprobsValue;
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.UserId, out var userId) && userId is string userIdValue)
        {
            request.UserId = userIdValue;
        }
    }

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (json is null || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
    }

    private static string? TryGetString(AiChatMessage message, string key)
        => message.AdditionalProperties is not null && message.AdditionalProperties.TryGetValue(key, out var value) ? value as string : null;

    private static string? TryGetReasoningContent(AiChatMessage message)
    {
        var reasoning = TryGetString(message, "reasoning_content");
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            return reasoning;
        }

        return message.Contents.OfType<TextReasoningContent>().FirstOrDefault(static content => !string.IsNullOrWhiteSpace(content.Text))?.Text;
    }

    private static bool? TryGetStrictValue(IReadOnlyDictionary<string, object?>? properties)
        => properties is not null && properties.TryGetValue("strict", out var value) && value is bool strict ? strict : null;

    private static string SerializeToolResult(object? result)
    {
        return result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement json => json.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };
    }

    private static string SerializeCompactJson(JsonElement json)
        => JsonSerializer.Serialize(json, CompactJsonSerializerOptions);
}
