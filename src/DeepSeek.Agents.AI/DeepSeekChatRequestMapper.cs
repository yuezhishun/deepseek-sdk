using DeepSeek.Chat;
using Json.Schema;
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
    private const string JsonPrompt = """
        不要添加解释、Markdown 代码块或其他前后缀。
        不要翻译字段名或枚举值；字符串内容的语言遵循调用方要求。
        """;
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
        if (overrides.Reasoning is not null)
        {
            foreach (var key in new[] { DeepSeekChatOptionKeys.Thinking, DeepSeekChatOptionKeys.ReasoningEffort })
            {
                if (overrides.AdditionalProperties?.ContainsKey(key) != true)
                {
                    merged.AdditionalProperties?.Remove(key);
                }
            }
        }
        else if (overrides.AdditionalProperties?.ContainsKey(DeepSeekChatOptionKeys.ReasoningEffort) == true
            || overrides.AdditionalProperties?.ContainsKey(DeepSeekChatOptionKeys.Thinking) == true)
        {
            merged.Reasoning = null;
        }
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
            request.Messages.Add(new WireChatMessage { Role = "system", Content = instructions! });
        }

        foreach (var message in messages)
        {
            foreach (var mappedMessage in MapMessages(message, options))
            {
                request.Messages.Add(mappedMessage);
            }
        }

        request.Tools = MapTools(options?.Tools);
        request.ToolChoice = MapToolChoice(options);
        ApplyAdditionalProperties(request, options, stream);
        request.Stream = stream;

        if (GetReasoningEffort(options) is { } effort)
        {
            request.ReasoningEffort = ParseReasoningEffort(effort);
            request.Thinking ??= effort == "none" ? ThinkingMode.Disabled : ThinkingMode.Enabled;
        }

        return request;
    }

    internal static string? BuildInstructions(string? instructions, ChatResponseFormatJson? responseFormat)
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
            return "仅输出一个合法的 JSON 对象。" + Environment.NewLine + JsonPrompt;
        }

        JsonElement schemaElement = responseSchema!.Value;
        return new StringBuilder()
            .AppendLine("仅输出符合下方 JSON Schema 的 JSON 结果，不要输出 Schema 本身。")
            .AppendLine(JsonPrompt)
            .AppendLine("字段名、类型、必填项、枚举及额外字段限制均以 Schema 为准。")
            .AppendLine()
            .AppendLine("JSON Schema：")
            .AppendLine(SerializeCompactJson(schemaElement))
            .ToString()
            .TrimEnd();
    }

    internal static void ValidateJsonResponse(string text, ChatOptions? options, object? rawResponse, bool complete, bool hasToolCalls)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson format) return;

        try
        {
            if (!complete) throw new JsonException("JSON 响应未完整结束，不能作为完整结果使用。");
            // Tool turns may legitimately have no text; the final assistant turn must satisfy the format.
            if (hasToolCalls) return;
            if (string.IsNullOrWhiteSpace(text)) throw new JsonException("JSON 响应为空或仅包含空白。");
            using var document = JsonDocument.Parse(text);
            if (format.Schema is JsonElement schema && schema.ValueKind != JsonValueKind.Undefined)
            {
                var validator = JsonSchema.Build(schema, new BuildOptions
                {
                    Dialect = Dialect.Draft202012,
                    SchemaRegistry = new(),
                });
                if (!validator.Evaluate(document.RootElement).IsValid)
                    throw new JsonException("响应 JSON 不符合指定的 JSON Schema。");
            }
            else if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("普通 JSON 模式要求返回一个 JSON 对象。");
            }
        }
        catch (JsonException exception)
        {
            var error = new JsonException(exception.Message, exception);
            error.Data["RawRepresentation"] = rawResponse;
            throw error;
        }
    }

    internal static ChatResponse MapToChatResponse(ChatCompletion response)
    {
        var choice = response.Choices.FirstOrDefault();
        var message = choice?.Message;
        var assistant = new AiChatMessage(ChatRole.Assistant, message?.Content?.Text ?? string.Empty)
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
            result.Usage = MapUsage(response.Usage);
            result.AdditionalProperties["usage_total_tokens"] = response.Usage.TotalTokens;
        }

        var finishReason = choice?.FinishReason;
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            result.AdditionalProperties["finish_reason"] = finishReason;
        }

        return result;
    }

    internal static UsageDetails MapUsage(TokenUsage usage)
        => new()
        {
            InputTokenCount = usage.PromptTokens,
            OutputTokenCount = usage.CompletionTokens,
            TotalTokenCount = usage.TotalTokens,
            CachedInputTokenCount = usage.PromptCacheHitTokens ?? usage.PromptTokensDetails?.CachedTokens,
            ReasoningTokenCount = usage.CompletionTokensDetails?.ReasoningTokens,
            AdditionalCounts = usage.PromptCacheMissTokens is { } misses
                ? new AdditionalPropertiesDictionary<long> { ["prompt_cache_miss_tokens"] = misses } : null,
        };

    private static IEnumerable<WireChatMessage> MapMessages(AiChatMessage message, ChatOptions? options)
    {
        var detail = GetImageDetail(options);
        if (message.Role == ChatRole.System)
        {
            yield return new WireChatMessage { Role = "system", Content = MapContent(message, detail) };
            yield break;
        }

        if (message.Role == ChatRole.User)
        {
            yield return new WireChatMessage { Role = "user", Content = MapContent(message, detail) };
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
                    Content = result.Result is IEnumerable<AIContent> contents
                        ? MapContent(new AiChatMessage(ChatRole.Tool, contents.ToList()), detail)
                        : new ChatMessageContent(SerializeToolResult(result.Result)),
                    ToolCallId = result.CallId,
                };
            }

            yield break;
        }

        var assistant = new WireChatMessage
        {
            Role = "assistant",
            Content = MapContent(message, detail),
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

    private static ChatMessageContent? MapContent(AiChatMessage message, string? detail)
    {
        var parts = new List<ChatContentPart>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(new ChatTextContentPart(text.Text));
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    parts.Add(new ChatImageContentPart(data.Uri, detail));
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image"):
                    parts.Add(new ChatImageContentPart(uri.Uri.ToString(), detail));
                    break;
                case HostedFileContent file when file.HasTopLevelMediaType("image") || file.MediaType is null:
                    parts.Add(new ChatFileContentPart(file.FileId));
                    break;
                case TextReasoningContent:
                case FunctionCallContent:
                case FunctionResultContent:
                    break;
                default:
                    throw new NotSupportedException("Unsupported chat content type: " + content.GetType().FullName);
            }
        }

        if (parts.Count > 0)
        {
            return new ChatMessageContent(parts);
        }

        return string.IsNullOrWhiteSpace(message.Text) ? null : new ChatMessageContent(message.Text!);
    }

    internal static string? GetReasoningEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.ReasoningEffort, out var value) == true
            && value is string effort)
        {
            return ParseReasoningEffort(effort).ToString().ToLowerInvariant();
        }

        return options?.Reasoning is { } reasoning ? reasoning.Effort switch
        {
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.ExtraHigh => "max",
            _ => "high",
        } : null;
    }

    internal static ThinkingMode? GetThinking(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.Thinking, out var value) == true
            ? value switch
            {
                bool enabled => enabled ? ThinkingMode.Enabled : ThinkingMode.Disabled,
                "enabled" => ThinkingMode.Enabled,
                "disabled" => ThinkingMode.Disabled,
                _ => null,
            }
            : null;

    private static ChatReasoningEffort ParseReasoningEffort(string effort)
        => effort switch
        {
            "none" => ChatReasoningEffort.None,
            "low" or "minimal" => ChatReasoningEffort.Low,
            "high" or "medium" or "xhigh" => ChatReasoningEffort.High,
            "max" or "ultra" => ChatReasoningEffort.Max,
            _ => throw new ArgumentException("Unsupported reasoning effort: " + effort, nameof(effort)),
        };

    private static string? GetImageDetail(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.ImageDetail, out var value) == true
            ? value as string
            : null;

    private static IList<WireChatTool>? MapTools(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        // Deduplicate by function name — DeepSeek API rejects duplicate tool names.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WireChatTool>(tools.Count);
        foreach (var tool in tools)
        {
            var wireTool = MapTool(tool);
            var name = wireTool.Function?.Name;
            if (name is not null && seen.Add(name))
            {
                result.Add(wireTool);
            }
        }

        return result;
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
            request.ReasoningEffort = ParseReasoningEffort(effortText);
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
