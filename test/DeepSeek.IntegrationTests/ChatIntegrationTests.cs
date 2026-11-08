using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
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

    [LiveFact]
    public async Task Models_List_Live()
    {
        var response = await _fixture.ModelsClient.GetModelsAsync(CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.Object));
        Assert.NotEmpty(response.Value.Data);
        Assert.Contains(response.Value.Data, model => !string.IsNullOrWhiteSpace(model.Object));
        Assert.Contains(response.Value.Data, model => model.Id?.StartsWith("deepseek-", StringComparison.Ordinal) == true);
    }

    [LiveFact]
    public async Task Balance_GetUserBalance_Live()
    {
        var response = await _fixture.BalanceClient.GetBalanceAsync(CreateRequestOptions());

        Assert.True(response.Value.IsAvailable);
        Assert.NotEmpty(response.Value.BalanceInfos);
        Assert.All(response.Value.BalanceInfos, info =>
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Currency));
            Assert.True(decimal.TryParse(info.TotalBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out _));
        });
    }

    [LiveFact]
    public async Task OpenAI_Chat_BasicExactReply_Live()
    {
        var response = await _fixture.ChatClient.CompleteChatAsync(
            new ChatCompletionRequest
            {
                Messages = [new DeepSeekChatMessage { Role = "user", Content = "Reply with exactly: openai-basic-ok" }],
                Thinking = ThinkingMode.Disabled,
                Temperature = 0,
                MaxTokens = 32,
            },
            CreateRequestOptions());

        Assert.Equal("openai-basic-ok", response.Value.Choices[0].Message?.Content?.Text?.Trim());
    }

    [LiveFact]
    public async Task OpenAI_ChatStreaming_WithUsage_Live()
    {
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
            var delta = chunk.Choices.FirstOrDefault()?.Delta?.Content?.Text;
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

    [LiveFact]
    public async Task OpenAI_Completions_Basic_Live()
    {
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

    [LiveFact]
    public async Task OpenAI_Completions_Fim_Live()
    {
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

    [LiveFact]
    public async Task Anthropic_Messages_Basic_Live()
    {
        var response = await _fixture.AnthropicClient.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                MaxTokens = 64,
                Temperature = 0,
                Thinking = new AnthropicThinkingConfig { Type = "disabled" },
                System = "Copy the user's ASCII token byte for byte. Output exactly anthropic-basic-ok with no extra characters.",
                Messages =
                [
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "anthropic-basic-ok" }],
                    },
                ],
            },
            CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.Id));
        Assert.Equal("assistant", response.Value.Role);
        Assert.Contains(response.Value.Content, block => block.Type == "text" && block.Text?.Contains("anthropic-basic-ok", StringComparison.Ordinal) == true);
    }

    [LiveFact]
    public async Task Anthropic_Streaming_Live()
    {
        var events = new List<AnthropicStreamEvent>();

        await foreach (var item in _fixture.AnthropicClient.CreateMessageStreaming(
            new AnthropicMessageRequest
            {
                MaxTokens = 1024,
                Temperature = 0,
                Thinking = new AnthropicThinkingConfig { Type = "enabled" },
                OutputConfig = new AnthropicOutputConfig { Effort = "high" },
                Messages =
                [
                    new AnthropicMessageModel
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "Calculate 37 * 43 step by step, then finish with anthropic-stream-ok." }],
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

    [LiveFact]
    public async Task OpenAI_Adapter_ReasoningAndToolMapping_Live()
    {
        var tool = AIFunctionFactory.Create(
            (string city) => city,
            "echo_city",
            "Echoes the city argument.");
        var response = await _fixture.Client.GetChatClient(DeepSeekTestModels.ProModel).AsIChatClient().GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Work out 37 * 43 before choosing a city: choose Hangzhou if the product is 1591, otherwise choose Shanghai. Think through the computation, then call echo_city with your chosen city and no final prose answer.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Tools = [tool],
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                MaxOutputTokens = 1024,
            },
            _fixture.CreateToken());

        Assert.Equal(DeepSeekTestModels.ProModel, response.ModelId);
        var message = Assert.Single(response.Messages);
        Assert.NotEmpty(message.Contents.OfType<TextReasoningContent>());
        var toolCall = Assert.Single(message.Contents.OfType<FunctionCallContent>());
        Assert.Equal("echo_city", toolCall.Name);
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(message.AdditionalProperties.ContainsKey("reasoning_content"));
    }

    [LiveFact]
    public async Task OpenAI_Adapter_JsonSchemaResponse_ReturnsJsonObject_Live()
    {
        var response = await _fixture.OpenAiChatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Create a leasing snapshot for a renter who wants a studio next month. Reply only with the JSON object.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.FlashModel,
                Temperature = 0,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<StructuredJsonEvidenceResponse>(
                    schemaName: "StructuredJsonEvidenceResponse",
                    schemaDescription: "A minimal leasing snapshot used to prove the DeepSeek adapter returned a JSON object."),
            },
            _fixture.CreateToken());

        var assistant = Assert.Single(response.Messages);
        string text = assistant.Text ?? response.Text ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(text));

        using JsonDocument document = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        JsonElement assistantMessage = document.RootElement.GetProperty("assistantMessage");
        Assert.Equal(JsonValueKind.String, assistantMessage.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(assistantMessage.GetString()));

        JsonElement state = document.RootElement.GetProperty("state");
        Assert.Equal(JsonValueKind.Object, state.ValueKind);

        JsonElement stage = state.GetProperty("stage");
        Assert.Equal(JsonValueKind.String, stage.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(stage.GetString()));
    }

    [LiveFact]
    public async Task OpenAI_Adapter_JsonObjectResponse_ReturnsParsableJsonObject_Live()
    {
        var response = await _fixture.OpenAiChatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return exactly one JSON object with properties answer and evidence. Set answer to json-object-ok and evidence to deepseek-json-proof.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.FlashModel,
                Temperature = 0,
                ResponseFormat = ChatResponseFormat.Json,
            },
            _fixture.CreateToken());

        var assistant = Assert.Single(response.Messages);
        string text = assistant.Text ?? response.Text ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(text));

        using JsonDocument document = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        JsonElement answer = document.RootElement.GetProperty("answer");
        Assert.Equal(JsonValueKind.String, answer.ValueKind);
        Assert.Equal("json-object-ok", answer.GetString());

        JsonElement evidence = document.RootElement.GetProperty("evidence");
        Assert.Equal(JsonValueKind.String, evidence.ValueKind);
        Assert.Equal("deepseek-json-proof", evidence.GetString());
    }

    [LiveFact]
    public async Task Anthropic_Adapter_ReasoningAndToolMapping_Live()
    {
        var tool = AIFunctionFactory.Create(
            (string city) => city,
            "echo_city",
            "Echoes the city argument.");
        var response = await _fixture.Client.GetAnthropicClient(DeepSeekTestModels.ProModel).AsIChatClient().GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Work out 37 * 43 before choosing a city: choose Hangzhou if the product is 1591, otherwise choose Shanghai. Think through the computation, then call echo_city with your chosen city and no final prose answer.")],
            new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Tools = [tool],
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                MaxOutputTokens = 1024,
            },
            _fixture.CreateToken());

        Assert.Equal(DeepSeekTestModels.ProModel, response.ModelId);
        var message = Assert.Single(response.Messages);
        Assert.NotEmpty(message.Contents.OfType<TextReasoningContent>());
        var toolCall = Assert.Single(message.Contents.OfType<FunctionCallContent>());
        Assert.Equal("echo_city", toolCall.Name);
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(message.AdditionalProperties.ContainsKey("reasoning_content"));
    }

    [LiveFact]
    public async Task OpenAI_Agent_MultiTurnConversation_Live()
    {
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

    [LiveFact]
    public async Task Anthropic_Agent_MultiTurnReasoningTools_Live()
    {
        var callCount = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref callCount);
                return "tool-result-bravo";
            },
            "get_constant_result",
            "Returns the constant string tool-result-bravo.");
        var agent = _fixture.Client.GetAnthropicClient(DeepSeekTestModels.ProModel).AsAIAgent(
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
        var invoker = Assert.IsAssignableFrom<FunctionInvokingChatClient>(agent.GetService(typeof(FunctionInvokingChatClient)));
        invoker.MaximumIterationsPerRequest = 3;
        var session = await agent.CreateSessionAsync(_fixture.CreateToken());
        var options = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                ModelId = DeepSeekTestModels.ProModel,
                ToolMode = ChatToolMode.Auto,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                MaxOutputTokens = 1024,
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

    [LiveFact]
    public async Task Anthropic_Messages_ThinkingAndToolHistory_Live()
    {
        var response = await _fixture.AnthropicClient.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                MaxTokens = 1024,
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

public sealed class StructuredJsonEvidenceResponse
{
    public StructuredJsonEvidenceState State { get; set; } = new();

    public string AssistantMessage { get; set; } = string.Empty;
}

public sealed class StructuredJsonEvidenceState
{
    public string Stage { get; set; } = string.Empty;
}
#pragma warning restore MAAI001
