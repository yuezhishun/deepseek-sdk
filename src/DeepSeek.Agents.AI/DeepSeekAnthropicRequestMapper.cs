using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Anthropic;
using Microsoft.Extensions.AI;

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
            System = options?.Instructions,
        };

        foreach (var message in messages)
        {
            request.Messages.Add(MapMessage(message));
        }

        if (options?.Reasoning is not null)
        {
            request.Thinking = new AnthropicThinkingConfig { Type = "enabled" };
            request.OutputConfig = new AnthropicOutputConfig
            {
                Effort = options.Reasoning.Effort == ReasoningEffort.ExtraHigh ? "max" : "high",
            };
        }

        if (options?.Tools is not null && options.Tools.Count > 0)
        {
            request.Tools = options.Tools.Select(static tool =>
            {
                if (tool is DelegatingAIFunction delegatingFunction)
                {
                    return new AnthropicTool
                    {
                        Name = delegatingFunction.Name,
                        Description = delegatingFunction.Description,
                        InputSchema = JsonNode.Parse(delegatingFunction.JsonSchema.GetRawText()) ?? new JsonObject(),
                    };
                }

                if (tool is AIFunction function)
                {
                    return new AnthropicTool
                    {
                        Name = function.Name,
                        Description = function.Description,
                        InputSchema = JsonNode.Parse(function.JsonSchema.GetRawText()) ?? new JsonObject(),
                    };
                }

                throw new NotSupportedException("Unsupported AITool type: " + tool.GetType().FullName);
            }).ToList();
        }

        request.ToolChoice = options?.ToolMode switch
        {
            AutoChatToolMode => new AnthropicToolChoice { Type = "auto" },
            RequiredChatToolMode => new AnthropicToolChoice { Type = "any" },
            NoneChatToolMode => new AnthropicToolChoice { Type = "none" },
            _ => null,
        };

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
            Usage = response.Usage is null
                ? null
                : new UsageDetails
                {
                    InputTokenCount = response.Usage.InputTokens,
                    OutputTokenCount = response.Usage.OutputTokens,
                    TotalTokenCount = (response.Usage.InputTokens ?? 0) + (response.Usage.OutputTokens ?? 0),
                },
        };
    }

    private static AnthropicMessage MapMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            var result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            return new AnthropicMessage
            {
                Role = "user",
                Content =
                [
                    new AnthropicContentBlock
                    {
                        Type = "tool_result",
                        ToolUseId = result?.CallId,
                        Content = JsonValue.Create(result?.Result?.ToString() ?? message.Text),
                    },
                ],
            };
        }

        var blocks = new List<AnthropicContentBlock>();
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            blocks.Add(new AnthropicContentBlock { Type = "text", Text = message.Text });
        }

        if (message.Role == ChatRole.Assistant &&
            message.AdditionalProperties is not null &&
            message.AdditionalProperties.TryGetValue("reasoning_content", out var reasoningValue) &&
            reasoningValue is string reasoning &&
            !string.IsNullOrWhiteSpace(reasoning))
        {
            blocks.Insert(0, new AnthropicContentBlock { Type = "thinking", Thinking = reasoning });
        }

        foreach (var functionCall in message.Contents.OfType<FunctionCallContent>())
        {
            blocks.Add(new AnthropicContentBlock
            {
                Type = "tool_use",
                Id = functionCall.CallId,
                Name = functionCall.Name,
                Input = JsonSerializer.SerializeToNode(functionCall.Arguments) as JsonNode,
            });
        }

        return new AnthropicMessage
        {
            Role = message.Role == ChatRole.Assistant ? "assistant" : "user",
            Content = blocks,
        };
    }
}
