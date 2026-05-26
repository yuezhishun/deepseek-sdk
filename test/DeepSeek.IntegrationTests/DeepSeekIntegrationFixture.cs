using DeepSeek.Anthropic;
using DeepSeek.Billing;
using DeepSeek.Chat;
using DeepSeek.Completions;
using DeepSeek.Models;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.DeepSeek;
using TestSupport;

namespace DeepSeek.IntegrationTests;

public sealed class DeepSeekIntegrationFixture : IDisposable
{
    public DeepSeekIntegrationFixture()
    {
        ApiKey = TestApiKeyProvider.GetApiKeyOrFallback();
        Client = new DeepSeekClient(ApiKey);
        ChatClient = Client.GetChatClient(DeepSeekTestModels.FlashModel);
        CompletionsClient = Client.GetCompletionsClient(DeepSeekTestModels.ProModel);
        ModelsClient = Client.GetModelsClient();
        BalanceClient = Client.GetBalanceClient();
        AnthropicClient = Client.GetAnthropicClient(DeepSeekTestModels.FlashModel);
        OpenAiChatClient = ChatClient.AsIChatClient();
        AnthropicChatClient = AnthropicClient.AsIChatClient();
    }

    public string ApiKey { get; }

    public DeepSeekClient Client { get; }

    public ChatClient ChatClient { get; }

    public CompletionsClient CompletionsClient { get; }

    public ModelsClient ModelsClient { get; }

    public BalanceClient BalanceClient { get; }

    public AnthropicClient AnthropicClient { get; }

    public IChatClient OpenAiChatClient { get; }

    public IChatClient AnthropicChatClient { get; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.Equals(ApiKey, "test-key", StringComparison.Ordinal);

    public CancellationToken CreateToken(int seconds = 120)
    {
        var source = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return source.Token;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
