using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Anthropic;
using Microsoft.Extensions.AI;
using ThinkingMode = DeepSeek.Chat.ThinkingMode;

namespace DeepSeek.Agents.AI;

internal static class DeepSeekAnthropicRequestMapper
{
    internal static AnthropicMessageRequest MapRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var request = new AnthropicMessageRequest
        {
            MaxTokens = options?.MaxOutputTokens,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            StopSequences = options?.StopSequences?.ToList(),
            System = DeepSeekChatRequestMapper.BuildInstructions(options?.Instructions, options?.ResponseFormat as ChatResponseFormatJson),
        };

        foreach (var message in messages)
        {
            request.Messages.Add(MapMessage(message));
        }

        var effort = DeepSeekChatRequestMapper.GetReasoningEffort(options);
        var thinking = DeepSeekChatRequestMapper.GetThinking(options);
        if (effort is not null || thinking is not null)
        {
            request.Thinking = new AnthropicThinkingConfig
            {
                Type = (thinking ?? (effort == "none" ? ThinkingMode.Disabled : ThinkingMode.Enabled)) == ThinkingMode.Disabled
                    ? "disabled" : "enabled",
            };
            if (effort is not null) request.OutputConfig = new AnthropicOutputConfig { Effort = effort };
        }

        if (options?.Tools is not null && options.Tools.Count > 0)
        {
            request.Tools = options.Tools.Select(static tool =>
            {
                if (tool.GetService<HostedWebSearchTool>() is { } search)
                {
                    return new AnthropicTool
                    {
                        Type = "web_search_20250305",
                        Name = "web_search",
                        MaxUses = search.AdditionalProperties?.TryGetValue("max_uses", out var maximum) == true && maximum is int count ? count : null,
                    };
                }

                if (tool.GetService<DelegatingAIFunction>() is { } delegatingFunction)
                {
                    return new AnthropicTool
                    {
                        Name = delegatingFunction.Name,
                        Description = delegatingFunction.Description,
                        InputSchema = JsonNode.Parse(delegatingFunction.JsonSchema.GetRawText()) ?? new JsonObject(),
                    };
                }

                if (tool.GetService<AIFunction>() is { } function)
                {
                    return new AnthropicTool
                    {
                        Name = function.Name,
                        Description = function.Description,
                        InputSchema = JsonNode.Parse(function.JsonSchema.GetRawText()) ?? new JsonObject(),
                    };
                }

                if (tool.GetService<AIFunctionDeclaration>() is { } declaration)
                {
                    return new AnthropicTool
                    {
                        Name = declaration.Name,
                        Description = declaration.Description,
                        InputSchema = JsonNode.Parse(declaration.JsonSchema.GetRawText()) ?? new JsonObject(),
                    };
                }

                throw new NotSupportedException("Unsupported AITool type: " + tool.GetType().FullName);
            }).ToList();
        }

        request.ToolChoice = options?.ToolMode switch
        {
            AutoChatToolMode => new AnthropicToolChoice { Type = "auto" },
            RequiredChatToolMode required when !string.IsNullOrWhiteSpace(required.RequiredFunctionName)
                => new AnthropicToolChoice { Type = "tool", Name = required.RequiredFunctionName },
            RequiredChatToolMode => new AnthropicToolChoice { Type = "any" },
            NoneChatToolMode => new AnthropicToolChoice { Type = "none" },
            _ => null,
        };

        if (options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.ToolChoiceName, out var namedTool) == true
            && namedTool is string namedToolName && !string.IsNullOrWhiteSpace(namedToolName))
        {
            request.ToolChoice = new AnthropicToolChoice { Type = "tool", Name = namedToolName };
        }

        if (options?.AdditionalProperties?.TryGetValue(DeepSeekChatOptionKeys.UserId, out var userId) == true
            && userId is string userIdValue && !string.IsNullOrWhiteSpace(userIdValue))
        {
            request.Metadata = new AnthropicMetadata { UserId = userIdValue };
        }

        request.Stream = stream;
        return request;
    }

    internal static ChatResponse MapResponse(AnthropicMessageResponse response)
    {
        var text = string.Concat(response.Content.Where(static block => block.Type == "text").Select(static block => block.Text));
        var message = new ChatMessage(ChatRole.Assistant, text ?? string.Empty)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary(),
            RawRepresentation = response,
        };

        var reasoning = response.Content.FirstOrDefault(static block => block.Type == "thinking")?.Thinking;
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            message.AdditionalProperties["reasoning_content"] = reasoning;
            message.Contents.Add(new TextReasoningContent(reasoning));
        }

        foreach (var toolUse in response.Content.Where(static block => block.Type == "tool_use"))
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(toolUse.Input?.ToJsonString() ?? "{}") ?? [];
            message.Contents.Add(new FunctionCallContent(toolUse.Id ?? string.Empty, toolUse.Name ?? string.Empty, args));
        }

        return new ChatResponse([message])
        {
            ModelId = response.Model,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["finish_reason"] = response.StopReason ?? string.Empty,
            },
            Usage = response.Usage is null ? null : MapUsage(response.Usage),
        };
    }

    internal static UsageDetails MapUsage(AnthropicUsage usage)
    {
        var input = (usage.InputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0) + (usage.CacheCreationInputTokens ?? 0);
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = input + (usage.OutputTokens ?? 0),
            CachedInputTokenCount = usage.CacheReadInputTokens,
            AdditionalCounts = usage.ServerToolUse is null ? null : new AdditionalPropertiesDictionary<long>(usage.ServerToolUse),
        };
    }

    private static AnthropicMessage MapMessage(ChatMessage message)
    {
        var original = message.RawRepresentation as AnthropicMessageResponse
            ?? message.Contents.Select(static content => content.RawRepresentation).OfType<AnthropicMessageResponse>().LastOrDefault();
        if (message.Role == ChatRole.Assistant && original is { Content.Count: > 0 })
        {
            return new AnthropicMessage { Role = "assistant", Content = original.Content.ToList() };
        }

        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count == 0)
            {
                results.Add(new FunctionResultContent(
                    TryGetString(message, "tool_use_id") ?? string.Empty,
                    message.Text));
            }

            return new AnthropicMessage
            {
                Role = "user",
                Content = results.Select(static result => new AnthropicContentBlock
                    {
                        Type = "tool_result",
                        ToolUseId = result.CallId,
                        Content = MapToolOutput(result.Result),
                    })
                    .ToList(),
            };
        }

        var blocks = new List<AnthropicContentBlock>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    blocks.Add(new AnthropicContentBlock { Type = "text", Text = text.Text });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    blocks.Add(new AnthropicContentBlock
                    {
                        Type = "image",
                        Source = new AnthropicImageSource
                        {
                            Type = "base64",
                            MediaType = data.MediaType,
                            Data = data.Base64Data.ToString(),
                        },
                    });
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image"):
                    blocks.Add(new AnthropicContentBlock
                    {
                        Type = "image",
                        Source = new AnthropicImageSource { Type = "url", Url = uri.Uri.ToString() },
                    });
                    break;
                case HostedFileContent file when file.HasTopLevelMediaType("image") || file.MediaType is null:
                    blocks.Add(new AnthropicContentBlock
                    {
                        Type = "image",
                        Source = new AnthropicImageSource { Type = "file", FileId = file.FileId },
                    });
                    break;
                case TextReasoningContent:
                    break;
                case FunctionCallContent functionCall:
                    blocks.Add(new AnthropicContentBlock
                    {
                        Type = "tool_use",
                        Id = functionCall.CallId,
                        Name = functionCall.Name,
                        Input = JsonSerializer.SerializeToNode(functionCall.Arguments) as JsonNode,
                    });
                    break;
                case FunctionResultContent:
                    break;
                default:
                    throw new NotSupportedException("Unsupported Anthropic content type: " + content.GetType().FullName);
            }
        }

        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            blocks.Add(new AnthropicContentBlock { Type = "text", Text = message.Text });
        }

        var reasoning = TryGetReasoning(message);
        if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(reasoning))
        {
            blocks.Insert(0, new AnthropicContentBlock { Type = "thinking", Thinking = reasoning });
        }

        return new AnthropicMessage
        {
            Role = message.Role == ChatRole.Assistant ? "assistant" : "user",
            Content = blocks,
        };
    }

    private static JsonNode? MapToolOutput(object? result)
    {
        if (result is not IEnumerable<AIContent> contents)
        {
            return JsonValue.Create(result switch
            {
                null => string.Empty,
                string text => text,
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(result),
            });
        }

        var blocks = MapMessage(new ChatMessage(ChatRole.User, contents.ToList())).Content;
        var output = new JsonArray();
        foreach (var block in blocks)
        {
            if (block.Type == "text")
            {
                output.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text });
            }
            else if (block.Source is { } source)
            {
                var image = new JsonObject { ["type"] = source.Type };
                if (source.MediaType is not null) image["media_type"] = source.MediaType;
                if (source.Data is not null) image["data"] = source.Data;
                if (source.Url is not null) image["url"] = source.Url;
                if (source.FileId is not null) image["file_id"] = source.FileId;
                output.Add(new JsonObject { ["type"] = "image", ["source"] = image });
            }
        }

        return output;
    }

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
}
