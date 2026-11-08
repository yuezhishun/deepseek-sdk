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
    /// Chat-completions only. The Anthropic adapter ignores this key.
    /// </summary>
    public const string Logprobs = "logprobs";

    /// <summary>
    /// Chat-completions only. The Anthropic adapter ignores this key.
    /// </summary>
    public const string TopLogprobs = "top_logprobs";

    /// <summary>
    /// Chat-completions and Anthropic adapters support named tool selection.
    /// </summary>
    public const string ToolChoiceName = "tool_choice_name";

    /// <summary>
    /// Chat 使用 user_id，Responses 使用 user，Anthropic 使用 metadata.user_id。
    /// </summary>
    public const string UserId = "user_id";

    /// <summary>Default detail value applied to image content parts.</summary>
    public const string ImageDetail = "image_detail";

    /// <summary>Provider file ID used by image-capable content adapters.</summary>
    public const string FileId = "file_id";
}
