using DeepSeek.Anthropic;
using DeepSeek.Chat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DeepSeek.Agents.AI;

#pragma warning disable MAAI001
public static class DeepSeekChatClientExtensions
{
    public static IChatClient AsIChatClient(this ChatClient client, DeepSeekChatClientOptions? options = null)
    {
        _ = client ?? throw new ArgumentNullException(nameof(client));
        return new DeepSeekChatClient(client, options);
    }

    public static IChatClient AsIChatClient(this AnthropicClient client, DeepSeekChatClientOptions? options = null)
    {
        _ = client ?? throw new ArgumentNullException(nameof(client));
        return new DeepSeekAnthropicChatClient(client, options);
    }

    public static ChatClientAgent AsAIAgent(
        this ChatClient client,
        string? instructions = null,
        string? name = null,
        string? description = null,
        IList<AITool>? tools = null,
        Func<IChatClient, IChatClient>? clientFactory = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null)
        => client.AsAIAgent(CreateOptions(instructions, name, description, tools), clientFactory, loggerFactory, services);

    public static ChatClientAgent AsAIAgent(
        this ChatClient client,
        ChatClientAgentOptions options,
        Func<IChatClient, IChatClient>? clientFactory = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null)
    {
        _ = client ?? throw new ArgumentNullException(nameof(client));
        _ = options ?? throw new ArgumentNullException(nameof(options));
        return new ChatClientAgent(ResolveChatClient(client.AsIChatClient(), clientFactory), NormalizeOptions(options), loggerFactory, services);
    }

    public static ChatClientAgent AsAIAgent(
        this AnthropicClient client,
        ChatClientAgentOptions options,
        Func<IChatClient, IChatClient>? clientFactory = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null)
    {
        _ = client ?? throw new ArgumentNullException(nameof(client));
        _ = options ?? throw new ArgumentNullException(nameof(options));
        return new ChatClientAgent(ResolveChatClient(client.AsIChatClient(), clientFactory), NormalizeOptions(options), loggerFactory, services);
    }

    internal static ChatClientAgentOptions CreateOptions(string? instructions, string? name, string? description, IList<AITool>? tools)
    {
        return new ChatClientAgentOptions
        {
            Name = name,
            Description = description,
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools,
            },
        };
    }

    internal static ChatClientAgentOptions NormalizeOptions(ChatClientAgentOptions options)
    {
        var normalized = options.Clone();
        if (!normalized.UseProvidedChatClientAsIs)
        {
            normalized.RequirePerServiceCallChatHistoryPersistence = true;
            normalized.ChatOptions ??= new ChatOptions();
            normalized.ChatOptions.AdditionalProperties = DeepSeekChatRequestMapper.MergeAdditionalProperties(
                normalized.ChatOptions.AdditionalProperties,
                new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatRequestMapper.DisableStreamingToolContinuationKey] = true,
                });
        }

        return normalized;
    }

    internal static IChatClient ResolveChatClient(IChatClient chatClient, Func<IChatClient, IChatClient>? clientFactory)
        => clientFactory is null ? chatClient : clientFactory(chatClient);
}
#pragma warning restore MAAI001
