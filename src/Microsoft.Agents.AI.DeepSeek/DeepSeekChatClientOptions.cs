using DeepSeek.Chat;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.DeepSeek;

public sealed class DeepSeekChatClientOptions
{
    public string? Instructions { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? MaxOutputTokens { get; set; }
    public ChatResponseFormat? ResponseFormat { get; set; }
    public IList<string>? StopSequences { get; set; }
    public IList<AITool>? Tools { get; set; }
    public ChatToolMode? ToolMode { get; set; }
    public ReasoningOptions? Reasoning { get; set; }
    public ThinkingMode? Thinking { get; set; }
    public ChatReasoningEffort? ReasoningEffort { get; set; }
    /// <summary>
    /// Sends <c>stream_options.include_usage</c> for streaming requests only.
    /// Non-streaming requests ignore this setting.
    /// </summary>
    public bool? IncludeUsage { get; set; }
    public bool? Logprobs { get; set; }
    public int? TopLogprobs { get; set; }
    public string? ToolChoiceName { get; set; }
    public string? UserId { get; set; }
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }

    internal ChatOptions ToChatOptions()
    {
        var options = new ChatOptions
        {
            Instructions = Instructions,
            Temperature = Temperature,
            TopP = TopP,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ResponseFormat,
            StopSequences = StopSequences,
            Tools = Tools,
            ToolMode = ToolMode,
            Reasoning = Reasoning,
        };

        var additionalProperties = DeepSeekChatRequestMapper.CloneAdditionalProperties(AdditionalProperties);
        if (Thinking is not null)
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.Thinking] = Thinking.Value == ThinkingMode.Enabled ? "enabled" : "disabled";
        }

        if (ReasoningEffort is not null)
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.ReasoningEffort] = ReasoningEffort.Value == ChatReasoningEffort.Max ? "max" : "high";
        }

        if (IncludeUsage is not null)
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.IncludeUsage] = IncludeUsage.Value;
        }

        if (Logprobs is not null)
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.Logprobs] = Logprobs.Value;
        }

        if (TopLogprobs is not null)
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.TopLogprobs] = TopLogprobs.Value;
        }

        if (!string.IsNullOrWhiteSpace(ToolChoiceName))
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.ToolChoiceName] = ToolChoiceName;
        }

        if (!string.IsNullOrWhiteSpace(UserId))
        {
            additionalProperties ??= [];
            additionalProperties[DeepSeekChatOptionKeys.UserId] = UserId;
        }

        options.AdditionalProperties = additionalProperties;
        return options;
    }
}
