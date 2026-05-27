using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json.Nodes;
using DeepSeek.Anthropic;
using DeepSeek.Chat;
using DeepSeek.Completions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using DeepSeek.Agents.AI;
using TestSupport;
using Xunit;
using AnthropicMessageModel = DeepSeek.Anthropic.AnthropicMessage;
using DeepSeekChatMessage = DeepSeek.Chat.ChatMessage;

namespace DeepSeek.IntegrationTests;

#pragma warning disable MAAI001
[Trait("Category", "Live")]
public class ChatIntegrationTests : IClassFixture<DeepSeekIntegrationFixture>
{
    private readonly DeepSeekIntegrationFixture _fixture;

    public ChatIntegrationTests(DeepSeekIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Models_List_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.ModelsClient.GetModelsAsync(CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.Object));
        Assert.NotEmpty(response.Value.Data);
        Assert.Contains(response.Value.Data, model => !string.IsNullOrWhiteSpace(model.Object));
        Assert.Contains(response.Value.Data, model => model.Id?.StartsWith("deepseek-", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Balance_GetUserBalance_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.BalanceClient.GetBalanceAsync(CreateRequestOptions());

        Assert.True(response.Value.IsAvailable);
        Assert.NotEmpty(response.Value.BalanceInfos);
        Assert.All(response.Value.BalanceInfos, info =>
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Currency));
            Assert.True(decimal.TryParse(info.TotalBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out _));
        });
    }

    [Fact]
    public async Task OpenAI_Chat_BasicExactReply_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.ChatClient.CompleteChatAsync(
            new ChatCompletionRequest
            {
                Messages = [new DeepSeekChatMessage { Role = "user", Content = "Reply with exactly: openai-basic-ok" }],
                Thinking = ThinkingMode.Disabled,
                Temperature = 0,
                MaxTokens = 32,
            },
            CreateRequestOptions());

        Assert.Equal("openai-basic-ok", response.Value.Choices[0].Message?.Content?.Trim());
    }

    [Fact]
    public async Task OpenAI_ChatStreaming_WithUsage_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var chunks = new List<ChatCompletion>();
        var textParts = new List<string>();

        await foreach (var chunk in _fixture.ChatClient.CompleteChatStreaming(
            new ChatCompletionRequest
            {
                Messages = [new DeepSeekChatMessage { Role = "user", Content = "Reply with exactly: stream-usage-ok" }],
                Thinking = ThinkingMode.Disabled,
                Temperature = 0,
                MaxTokens = 32,
                StreamOptions = new StreamOptions { IncludeUsage = true },
            },
            CreateRequestOptions()))
        {
            chunks.Add(chunk);
            var delta = chunk.Choices.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrWhiteSpace(delta))
            {
                textParts.Add(delta);
            }
        }

        Assert.NotEmpty(textParts);
        Assert.Contains("stream-usage-ok", string.Concat(textParts), StringComparison.Ordinal);
        var usageChunk = Assert.Single(chunks, chunk => chunk.Usage != null);
        Assert.True(usageChunk.Usage!.PromptTokens > 0);
        Assert.True(usageChunk.Usage.CompletionTokens > 0);
        Assert.True(usageChunk.Usage.TotalTokens > 0);
    }

    [Fact]
    public async Task OpenAI_Completions_Basic_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.CompletionsClient.CompleteTextAsync(
            new CompletionRequest
            {
                Prompt = "const answer = 6 * 7 = ",
                Temperature = 0,
                MaxTokens = 8,
                Stop = ["\n"],
            },
            CreateRequestOptions());

        var text = response.Value.Choices[0].Text?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.StartsWith("42", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_Completions_Fim_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.CompletionsClient.CompleteTextAsync(
            new CompletionRequest
            {
                Prompt = "public static int Add(int a, int b)\n{\n    ",
                Suffix = "\n}",
                Temperature = 0,
                MaxTokens = 32,
            },
            CreateRequestOptions());

        var text = response.Value.Choices[0].Text?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("return", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a", text, StringComparison.Ordinal);
        Assert.Contains("b", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_Messages_Basic_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.AnthropicClient.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                MaxTokens = 64,
                Temperature = 0,
                Messages =
                [
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "Reply with exactly: anthropic-basic-ok" }],
                    },
                ],
            },
            CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.Id));
        Assert.Equal("assistant", response.Value.Role);
        Assert.Contains(response.Value.Content, block => block.Type == "text" && block.Text?.Contains("anthropic-basic-ok", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Anthropic_Streaming_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var events = new List<AnthropicStreamEvent>();

        await foreach (var item in _fixture.AnthropicClient.CreateMessageStreaming(
            new AnthropicMessageRequest
            {
                MaxTokens = 64,
                Temperature = 0,
                Thinking = new AnthropicThinkingConfig { Type = "enabled" },
                OutputConfig = new AnthropicOutputConfig { Effort = "high" },
                Messages =
                [
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "Reply with exactly anthropic-stream-ok." }],
                    },
                ],
            },
            CreateRequestOptions()))
        {
            events.Add(item);
        }

        Assert.NotEmpty(events);
        Assert.Contains(events, item => !string.IsNullOrWhiteSpace(item.Delta?.Text));
        Assert.Contains(events, item => !string.IsNullOrWhiteSpace(item.Delta?.Thinking));
        Assert.Contains(events, item => string.Equals(item.Type, "message_stop", StringComparison.Ordinal) || item.Usage != null);
    }

    [Fact]
    public async Task OpenAI_Adapter_ReasoningAndToolMapping_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var tool = AIFunctionFactory.Create(
            (string city) => city,
            "echo_city",
            "Echoes the city argument.");
        var response = await _fixture.OpenAiChatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Call echo_city with city Hangzhou and do not provide a final prose answer.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Tools = [tool],
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            },
            _fixture.CreateToken());

        var message = Assert.Single(response.Messages);
        Assert.NotEmpty(message.Contents.OfType<TextReasoningContent>());
        var toolCall = Assert.Single(message.Contents.OfType<FunctionCallContent>());
        Assert.Equal("echo_city", toolCall.Name);
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(message.AdditionalProperties.ContainsKey("reasoning_content"));
    }

    [Fact]
    public async Task Anthropic_Adapter_ReasoningAndToolMapping_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var tool = AIFunctionFactory.Create(
            (string city) => city,
            "echo_city",
            "Echoes the city argument.");
        var response = await _fixture.AnthropicChatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Call echo_city with city Hangzhou and do not provide a final prose answer.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Tools = [tool],
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            },
            _fixture.CreateToken());

        var message = Assert.Single(response.Messages);
        Assert.NotEmpty(message.Contents.OfType<TextReasoningContent>());
        var toolCall = Assert.Single(message.Contents.OfType<FunctionCallContent>());
        Assert.Equal("echo_city", toolCall.Name);
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(message.AdditionalProperties.ContainsKey("reasoning_content"));
    }

    [Fact]
    public async Task OpenAI_Agent_MultiTurnConversation_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var agent = _fixture.ChatClient.AsAIAgent(
            instructions: "Follow exact reply formatting.",
            name: "TypedOpenAiConversationAgent");
        var session = await agent.CreateSessionAsync(_fixture.CreateToken());

        var round1 = await agent.RunAsync(
            "Remember the customer code coral-17 and reply with exactly coral-17.",
            session,
            null,
            _fixture.CreateToken());
        var round2 = await agent.RunAsync(
            "What customer code did I give you earlier? Reply with exactly that code.",
            session,
            null,
            _fixture.CreateToken());

        Assert.Equal("coral-17", round1.Text?.Trim());
        Assert.Equal("coral-17", round2.Text?.Trim());
    }

    [Fact]
    public async Task Anthropic_Agent_MultiTurnReasoningTools_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var callCount = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref callCount);
                return "tool-result-bravo";
            },
            "get_constant_result",
            "Returns the constant string tool-result-bravo.");
        var agent = _fixture.AnthropicClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                Name = "TypedAnthropicToolAgent",
                ChatOptions = new ChatOptions
                {
                    ModelId = DeepSeekTestModels.ProModel,
                    Instructions = "Call tools when asked and answer with the tool result verbatim.",
                    Tools = [tool],
                },
            });
        var session = await agent.CreateSessionAsync(_fixture.CreateToken());
        var options = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            },
        };

        var round1 = await agent.RunAsync(
            "Call the tool before answering. Return exactly the tool result and nothing else.",
            session,
            options,
            _fixture.CreateToken());
        var round2 = await agent.RunAsync(
            "What exact tool result was returned earlier? Reply with exactly that string.",
            session,
            options,
            _fixture.CreateToken());

        Assert.True(callCount > 0);
        Assert.Equal("tool-result-bravo", NormalizeExactText(round1.Text));
        Assert.Equal("tool-result-bravo", NormalizeExactText(round2.Text));
    }

    [Fact]
    public async Task Anthropic_Messages_ThinkingAndToolHistory_Live()
    {
        if (!IntegrationTestGuard.RequireConfigured(_fixture)) { return; }

        var response = await _fixture.AnthropicClient.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                MaxTokens = 96,
                Temperature = 0,
                Messages =
                [
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "Use the tool result and answer exactly with Weather: sunny." }],
                    },
                    new AnthropicMessageModel
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AnthropicContentBlock { Type = "thinking", Thinking = "Need a tool result first." },
                            new AnthropicContentBlock
                            {
                                Type = "tool_use",
                                Id = "tool_1",
                                Name = "lookup_weather",
                                Input = JsonNode.Parse("{\"city\":\"Hangzhou\"}"),
                            },
                        ],
                    },
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content =
                        [
                            new AnthropicContentBlock
                            {
                                Type = "tool_result",
                                ToolUseId = "tool_1",
                                Content = JsonValue.Create("Weather: sunny."),
                            },
                        ],
                    },
                ],
            },
            CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.StopReason));
        Assert.NotEmpty(response.Value.Content);
        Assert.Contains(response.Value.Content, block => block.Type == "text" && block.Text?.Contains("Weather: sunny.", StringComparison.Ordinal) == true);
    }

    private RequestOptions CreateRequestOptions()
        => new() { CancellationToken = _fixture.CreateToken() };

    private static string NormalizeExactText(string? text)
        => text?.Trim().Trim('"') ?? string.Empty;
}
#pragma warning restore MAAI001
