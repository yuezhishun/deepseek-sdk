namespace DeepSeek.Agents.AI;

/// <summary>
/// DeepSeek-specific keys for <see cref="Microsoft.Extensions.AI.ChatOptions.AdditionalProperties"/>.
/// </summary>
public static class DeepSeekChatOptionKeys
{
    /// <summary>
    /// Compatibility key retained for callers that write directly to
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.AdditionalProperties"/>.
    /// Public streaming and non-streaming entrypoints ignore this key.
    /// </summary>
    public const string Stream = "stream";

    /// <summary>
    /// Streaming-only key for <c>stream_options.include_usage</c>.
    /// Non-streaming requests ignore this key.
    /// </summary>
    public const string IncludeUsage = "include_usage";
    public const string Thinking = "thinking";
    public const string ReasoningEffort = "reasoning_effort";

    /// <summary>
    /// Chat-completions only. The Anthropic adapter currently ignores this key.
    /// </summary>
    public const string Logprobs = "logprobs";

    /// <summary>
    /// Chat-completions only. The Anthropic adapter currently ignores this key.
    /// </summary>
    public const string TopLogprobs = "top_logprobs";

    /// <summary>
    /// Chat-completions only. The Anthropic adapter currently ignores this key.
    /// </summary>
    public const string ToolChoiceName = "tool_choice_name";

    /// <summary>
    /// Chat-completions only. The Anthropic adapter currently ignores this key.
    /// </summary>
    public const string UserId = "user_id";
}
