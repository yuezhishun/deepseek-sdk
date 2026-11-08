using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Responses;
using ThinkingMode = DeepSeek.Chat.ThinkingMode;
using Microsoft.Extensions.AI;

namespace DeepSeek.Agents.AI;

internal static class DeepSeekResponsesRequestMapper
{
    internal static ResponseRequest MapRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var request = new ResponseRequest
        {
            Instructions = options?.Instructions,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            MaxOutputTokens = options?.MaxOutputTokens,
            Input = new ResponseInput(MapMessages(messages, options)),
            Reasoning = MapReasoning(options),
            Text = MapText(options?.ResponseFormat),
            Tools = MapTools(options?.Tools),
            ToolChoice = MapToolChoice(options),
            Stream = stream,
        };

        ApplyAdditionalProperties(request, options);
        return request;
    }

    internal static ChatResponse MapResponse(Response response)
    {
        var message = new ChatMessage(ChatRole.Assistant, GetText(response.Output))
        {
            RawRepresentation = response,
            AdditionalProperties = new AdditionalPropertiesDictionary(),
        };

        var reasoning = GetReasoning(response.Output);
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            message.AdditionalProperties!["reasoning_content"] = reasoning;
            message.Contents.Add(new TextReasoningContent(reasoning));
        }

        var unknownItems = new List<ResponseOutputItem>();
        var webSearchItems = new List<ResponseOutputItem>();
        foreach (var item in response.Output)
        {
            if (string.Equals(item.Type, "function_call", StringComparison.Ordinal))
            {
                message.Contents.Add(CreateFunctionCall(item));
            }
            else if (string.Equals(item.Type, "function_call_output", StringComparison.Ordinal))
            {
                message.Contents.Add(new FunctionResultContent(item.CallId ?? string.Empty, item.Output ?? item.Arguments ?? string.Empty));
            }
            else if (item.Type is not "message" and not "reasoning" and not "web_search_call")
            {
                unknownItems.Add(item);
            }
            else if (item.Type == "web_search_call")
            {
                webSearchItems.Add(item);
            }
        }

        var result = new ChatResponse([message])
        {
            ModelId = response.Model,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["response_status"] = response.Status ?? string.Empty,
            },
        };

        if (response.Usage is not null)
        {
            result.Usage = MapUsage(response.Usage);
            result.AdditionalProperties!["usage_cached_tokens"] = response.Usage.InputTokensDetails?.CachedTokens;
            result.AdditionalProperties["usage_reasoning_tokens"] = response.Usage.OutputTokensDetails?.ReasoningTokens;
        }

        if (response.Error is not null)
        {
            result.AdditionalProperties!["response_error"] = response.Error;
        }

        if (unknownItems.Count > 0)
        {
            result.AdditionalProperties!["responses_unknown_output_items"] = unknownItems;
        }

        if (webSearchItems.Count > 0)
        {
            result.AdditionalProperties!["responses_web_search_calls"] = webSearchItems;
        }

        return result;
    }

    internal static ChatResponseUpdate MapTextUpdate(ResponseStreamEvent eventData, string text)
        => new(ChatRole.Assistant, text)
        {
            RawRepresentation = eventData,
        };

    internal static ChatResponseUpdate MapReasoningUpdate(ResponseStreamEvent eventData, string reasoning)
        => new(ChatRole.Assistant, [new TextReasoningContent(reasoning)])
        {
            RawRepresentation = eventData,
            AdditionalProperties = CreateEventProperties(eventData, reasoning),
        };

    internal static ChatResponseUpdate MapFunctionCallUpdate(ResponseStreamEvent eventData, ResponseOutputItem item)
    {
        var call = CreateFunctionCall(item);
        return new ChatResponseUpdate(ChatRole.Assistant, [call])
        {
            RawRepresentation = eventData,
            AdditionalProperties = CreateEventProperties(eventData, null),
        };
    }

    internal static ChatResponseUpdate MapFunctionResultUpdate(ResponseStreamEvent eventData, ResponseOutputItem item)
        => new(ChatRole.Tool, [new FunctionResultContent(item.CallId ?? string.Empty, item.Output ?? item.Arguments ?? string.Empty)])
        {
            RawRepresentation = eventData,
            AdditionalProperties = CreateEventProperties(eventData, null),
        };

    internal static ChatResponseUpdate MapFinalUpdate(ResponseStreamEvent eventData)
    {
        var properties = CreateEventProperties(eventData, null);
        if (eventData.Response is not null)
        {
            properties["response"] = eventData.Response;
            if (eventData.Response.Usage is not null)
            {
                properties["usage_total_tokens"] = eventData.Response.Usage.TotalTokens;
                properties["usage_input_tokens"] = eventData.Response.Usage.InputTokens;
                properties["usage_output_tokens"] = eventData.Response.Usage.OutputTokens;
            }
        }

        var update = new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
        {
            RawRepresentation = eventData,
            AdditionalProperties = properties,
        };
        if (eventData.Response?.Usage is { } usage)
        {
            update.Contents.Add(new UsageContent(MapUsage(usage)));
        }
        if (eventData.Response is { Output.Count: > 0 } original)
        {
            update.Contents.Add(new AIContent { RawRepresentation = original });
        }

        return update;
    }

    internal static UsageDetails MapUsage(ResponseUsage usage)
        => new()
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalTokens,
            CachedInputTokenCount = usage.InputTokensDetails?.CachedTokens,
            ReasoningTokenCount = usage.OutputTokensDetails?.ReasoningTokens,
            AdditionalCounts = usage.ServerToolUse is null ? null : new AdditionalPropertiesDictionary<long>(usage.ServerToolUse),
        };

    internal static ChatResponseUpdate MapRawEvent(ResponseStreamEvent eventData)
        => new(ChatRole.Assistant, (string?)null)
        {
            RawRepresentation = eventData,
            AdditionalProperties = CreateEventProperties(eventData, null),
        };

    internal static FunctionCallContent CreateFunctionCall(ResponseOutputItem item)
        => new(
            item.CallId ?? item.Id ?? string.Empty,
            item.Name ?? string.Empty,
            ParseArguments(item.Arguments));

    private static IList<ResponseInputItem> MapMessages(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var result = new List<ResponseInputItem>();
        var detail = GetImageDetail(options);
        foreach (var message in messages)
        {
            var original = message.RawRepresentation as Response
                ?? message.Contents.Select(static content => content.RawRepresentation).OfType<Response>().LastOrDefault();
            if (message.Role == ChatRole.Assistant && original is { Output.Count: > 0 })
            {
                result.AddRange(original.Output.Select(static item => new ResponseInputItem
                {
                    Type = item.Type,
                    Id = item.Id,
                    Role = item.Role,
                    Content = item.Content.Count == 0 ? null : new ResponseContentValue(item.Content),
                    CallId = item.CallId,
                    Name = item.Name,
                    Arguments = item.Arguments,
                    Input = item.Input,
                    Output = item.Output,
                    Status = item.Status,
                    Summary = item.Summary,
                    EncryptedContent = item.EncryptedContent,
                    Action = item.Action,
                    AdditionalProperties = item.AdditionalProperties,
                }));
                continue;
            }

            if (message.Role == ChatRole.Tool)
            {
                var functionResults = message.Contents.OfType<FunctionResultContent>().ToList();
                if (functionResults.Count == 0)
                {
                    result.Add(new ResponseInputFunctionCallOutput
                    {
                        CallId = TryGetString(message, "tool_call_id"),
                        Output = SerializeResult(message.Text),
                    });
                }
                else
                {
                    result.AddRange(functionResults.Select(static item => new ResponseInputFunctionCallOutput
                    {
                        CallId = item.CallId,
                        Output = MapFunctionOutput(item.Result),
                    }));
                }

                continue;
            }

            var role = message.Role == ChatRole.System ? "system" : message.Role == ChatRole.Assistant ? "assistant" : "user";
            var reasoning = TryGetReasoning(message);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                result.Add(new ResponseInputReasoning
                {
                    Content = new List<ResponseContentPart> { new ResponseReasoningTextContent { Text = reasoning } },
                });
            }

            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            var content = MapContent(message, detail);
            if (content is not null || calls.Count == 0)
            {
                result.Add(new ResponseInputMessage
                {
                    Role = role,
                    Content = content,
                });
            }

            foreach (var call in calls)
            {
                result.Add(new ResponseInputFunctionCall
                {
                    CallId = call.CallId,
                    Name = call.Name,
                    Arguments = JsonSerializer.Serialize(call.Arguments),
                });
            }
        }

        return result;
    }

    private static ResponseContentValue? MapContent(ChatMessage message, string? detail)
    {
        var parts = new List<ResponseContentPart>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(new ResponseInputTextContent { Text = text.Text });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    parts.Add(new ResponseInputImageContent { ImageUrl = data.Uri, Detail = detail });
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image"):
                    parts.Add(new ResponseInputImageContent { ImageUrl = uri.Uri.ToString(), Detail = detail });
                    break;
                case HostedFileContent file when file.HasTopLevelMediaType("image") || file.MediaType is null:
                    parts.Add(new ResponseInputImageContent { FileId = file.FileId, Detail = detail });
                    break;
                case TextReasoningContent:
                case FunctionCallContent:
                case FunctionResultContent:
                    break;
                default:
                    throw new NotSupportedException("Unsupported Responses content type: " + content.GetType().FullName);
            }
        }

        return parts.Count == 0
            ? (string.IsNullOrWhiteSpace(message.Text) ? null : new ResponseContentValue(message.Text!))
            : new ResponseContentValue(parts);
    }

    private static ResponseContentValue MapFunctionOutput(object? result)
    {
        if (result is IEnumerable<AIContent> contents)
        {
            var parts = new List<ResponseContentPart>();
            foreach (var content in contents)
            {
                switch (content)
                {
                    case TextContent text:
                        parts.Add(new ResponseInputTextContent { Text = text.Text });
                        break;
                    case DataContent data when data.HasTopLevelMediaType("image"):
                        parts.Add(new ResponseInputImageContent { ImageUrl = data.Uri });
                        break;
                    case UriContent uri when uri.HasTopLevelMediaType("image"):
                        parts.Add(new ResponseInputImageContent { ImageUrl = uri.Uri.ToString() });
                        break;
                    case HostedFileContent file when file.HasTopLevelMediaType("image") || file.MediaType is null:
                        parts.Add(new ResponseInputImageContent { FileId = file.FileId });
                        break;
                    default:
                        throw new NotSupportedException("Unsupported Responses function output content type: " + content.GetType().FullName);
                }
            }

            return new ResponseContentValue(parts);
        }

        return SerializeResult(result);
    }

    private static ResponseReasoningOptions? MapReasoning(ChatOptions? options)
    {
        var effort = DeepSeekChatRequestMapper.GetReasoningEffort(options);
        var thinking = DeepSeekChatRequestMapper.GetThinking(options);
        if (thinking == ThinkingMode.Disabled) effort = "none";
        else if (thinking == ThinkingMode.Enabled && (effort is null or "none")) effort = "high";
        return effort is null ? null : new ResponseReasoningOptions { Effort = effort };
    }

    private static ResponseTextOptions? MapText(ChatResponseFormat? format)
    {
        if (format is not ChatResponseFormatJson json)
        {
            return null;
        }

        var schema = json.Schema;
        if (schema is JsonElement schemaElement && schemaElement.ValueKind != JsonValueKind.Undefined)
        {
            return new ResponseTextOptions
            {
                Format = new ResponseTextFormat
                {
                    Type = "json_schema",
                    Name = "response",
                    Schema = JsonNode.Parse(schemaElement.GetRawText()),
                    Strict = true,
                },
            };
        }

        return new ResponseTextOptions
        {
            Format = new ResponseTextFormat { Type = "json_object" },
        };
    }

    private static IList<ResponseTool>? MapTools(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var result = new List<ResponseTool>(tools.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var mapped = MapTool(tool);
            if (seen.Add(mapped.Name ?? mapped.Type))
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private static ResponseTool MapTool(AITool tool)
    {
        if (tool.GetService<HostedWebSearchTool>() is { } search)
        {
            return new ResponseWebSearchTool
            {
                AdditionalProperties = search.AdditionalProperties?.ToDictionary(
                    static pair => pair.Key, static pair => JsonSerializer.SerializeToElement(pair.Value)),
            };
        }

        if (tool.GetService<DelegatingAIFunction>() is { } delegatingFunction)
        {
            return CreateTool(delegatingFunction.Name, delegatingFunction.Description, delegatingFunction.JsonSchema, delegatingFunction.AdditionalProperties);
        }

        if (tool.GetService<AIFunction>() is { } function)
        {
            return CreateTool(function.Name, function.Description, function.JsonSchema, function.AdditionalProperties);
        }

        if (tool.GetService<AIFunctionDeclaration>() is { } declaration)
        {
            return CreateTool(declaration.Name, declaration.Description, declaration.JsonSchema, declaration.AdditionalProperties);
        }

        throw new NotSupportedException("Unsupported AITool type: " + tool.GetType().FullName);
    }

    private static ResponseTool CreateTool(string name, string? description, JsonElement schema, IReadOnlyDictionary<string, object?>? additionalProperties)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = JsonNode.Parse(schema.GetRawText()) ?? new JsonObject(),
            Strict = additionalProperties is not null && additionalProperties.TryGetValue("strict", out var value) && value is bool strict ? strict : null,
        };

    private static object? MapToolChoice(ChatOptions? options)
    {
        switch (options?.ToolMode)
        {
            case AutoChatToolMode:
                return "auto";
            case RequiredChatToolMode required when !string.IsNullOrWhiteSpace(required.RequiredFunctionName):
                return new ResponseToolChoice { Type = "function", Name = required.RequiredFunctionName };
            case RequiredChatToolMode:
                return "required";
            case NoneChatToolMode:
                return "none";
        }

        if (options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.ToolChoiceName, out var value) == true && value is string name && !string.IsNullOrWhiteSpace(name))
        {
            return new ResponseToolChoice { Type = "function", Name = name };
        }

        return null;
    }

    private static void ApplyAdditionalProperties(ResponseRequest request, ChatOptions? options)
    {
        var properties = options?.AdditionalProperties;
        if (properties is null)
        {
            return;
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.TopLogprobs, out var topLogprobs) && topLogprobs is int topLogprobsValue)
        {
            request.TopLogprobs = topLogprobsValue;
        }

        if (properties.TryGetValue(DeepSeekChatOptionKeys.UserId, out var userId) && userId is string userIdValue)
        {
            request.User = userIdValue;
        }
    }

    private static Dictionary<string, object?> ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments!) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GetText(IEnumerable<ResponseOutputItem> output)
        => string.Concat(output.Where(static item => item.Type == "message")
            .SelectMany(static item => item.Content ?? [])
            .Where(static content => content.Type == "output_text")
            .Select(static content => content.Text));

    private static string GetReasoning(IEnumerable<ResponseOutputItem> output)
        => string.Concat(output.Where(static item => item.Type == "reasoning")
            .SelectMany(static item => item.Content ?? [])
            .Where(static content => content.Type == "reasoning_text")
            .Select(static content => content.Text));

    private static string? TryGetReasoning(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue("reasoning_content", out var value) == true && value is string reasoning)
        {
            return reasoning;
        }

        return message.Contents.OfType<TextReasoningContent>().FirstOrDefault()?.Text;
    }

    private static string? TryGetString(ChatMessage message, string key)
        => message.AdditionalProperties?.TryGetValue(key, out var value) == true ? value as string : null;

    private static string SerializeResult(object? value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value),
        };

    private static string? GetImageDetail(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.ImageDetail, out var value) == true
            ? value as string
            : null;

    private static AdditionalPropertiesDictionary CreateEventProperties(ResponseStreamEvent eventData, string? reasoning)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            ["responses_event_type"] = eventData.Type ?? eventData.Event ?? string.Empty,
        };

        if (eventData.SequenceNumber is not null)
        {
            properties["responses_sequence_number"] = eventData.SequenceNumber.Value;
        }

        if (reasoning is not null)
        {
            properties["reasoning_content"] = reasoning;
            properties["is_reasoning"] = true;
        }

        return properties;
    }
}
