using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.ClientModel.Primitives;
using DeepSeek.Agents.AI;
using DeepSeek.Responses;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DeepSeek.IntegrationTests;

#pragma warning disable MAAI001
[Trait("Category", "Live")]
public sealed class ResponsesIntegrationTests : IClassFixture<DeepSeekIntegrationFixture>
{
    private const int LiveMaxOutputTokens = 256;

    private readonly DeepSeekIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ResponsesIntegrationTests(DeepSeekIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [LiveFact]
    public async Task Responses_Basic_Live()
    {
        var response = await _fixture.ResponsesClient.CreateResponseAsync(
            new ResponseRequest
            {
                Instructions = "Copy the user's ASCII token byte for byte on one line. Output exactly responses-basic-ok without Markdown, spaces, or any other characters.",
                Input = "responses-basic-ok",
                Reasoning = new ResponseReasoningOptions { Effort = "none" },
                Temperature = 0,
                MaxOutputTokens = LiveMaxOutputTokens,
            },
            CreateRequestOptions());

        Assert.False(string.IsNullOrWhiteSpace(response.Value.Id));
        Assert.Equal("completed", response.Value.Status);
        if (!GetResponseText(response.Value).Contains("responses-basic-ok", StringComparison.Ordinal))
        {
            _output.WriteLine("Responses 基础请求的原始响应：{0}", response.GetRawResponse().Content.ToString());
        }
        Assert.Contains("responses-basic-ok", GetResponseText(response.Value), StringComparison.Ordinal);
        Assert.NotNull(response.Value.Usage);
        Assert.True(response.Value.Usage!.TotalTokens > 0);
    }

    [LiveFact]
    public async Task Responses_Streaming_Live()
    {
        var events = new List<ResponseStreamEvent>();
        var text = new StringBuilder();

        await foreach (var eventData in _fixture.ResponsesClient.CreateResponseStreaming(
            new ResponseRequest
            {
                Instructions = "Copy the user's ASCII token byte for byte on one line. Output exactly responses-stream-ok without Markdown, spaces, or any other characters.",
                Input = "responses-stream-ok",
                Reasoning = new ResponseReasoningOptions { Effort = "none" },
                Temperature = 0,
                MaxOutputTokens = LiveMaxOutputTokens,
            },
            CreateRequestOptions()))
        {
            events.Add(eventData);
            if (eventData.Type == "response.output_text.delta")
            {
                text.Append(eventData.Delta);
            }
        }

        Assert.NotEmpty(events);
        if (!text.ToString().Contains("responses-stream-ok", StringComparison.Ordinal))
        {
            _output.WriteLine("Responses 流式终止响应：{0}", JsonSerializer.Serialize(events.LastOrDefault()?.Response));
        }
        Assert.Contains("responses-stream-ok", text.ToString(), StringComparison.Ordinal);
        Assert.Contains(events, eventData => eventData.Type == "response.output_text.delta");
        Assert.Contains(events, eventData => eventData.Type == "response.completed" && eventData.Response is not null);
    }

    [LiveFact]
    public async Task Responses_Basic_Pro_Live()
    {
        var response = await _fixture.ResponsesProClient.CreateResponseAsync(
            new ResponseRequest
            {
                Instructions = "Copy the user's ASCII token byte for byte on one line. Output exactly responses-pro-ok without Markdown, spaces, or any other characters.",
                Input = "responses-pro-ok",
                Reasoning = new ResponseReasoningOptions { Effort = "none" },
                Temperature = 0,
                MaxOutputTokens = LiveMaxOutputTokens,
            },
            CreateRequestOptions());

        Assert.Equal(DeepSeekTestModels.ProModel, response.Value.Model);
        Assert.Contains("responses-pro-ok", GetResponseText(response.Value), StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task Responses_JsonOutputAndFunctionTool_Live()
    {
        var response = await _fixture.ResponsesClient.CreateResponseAsync(
            new ResponseRequest
            {
                Instructions = "Return a JSON object. If the user asks for weather, call the tool.",
                Input = "Call lookup_weather for Hangzhou.",
                Text = new ResponseTextOptions { Format = ResponseTextFormat.JsonObject() },
                Tools =
                [
                    new ResponseFunctionTool
                    {
                        Name = "lookup_weather",
                        Description = "Look up weather without executing it in this test.",
                        Parameters = new { type = "object", properties = new { city = new { type = "string" } } },
                    },
                ],
                MaxOutputTokens = LiveMaxOutputTokens,
            },
            CreateRequestOptions());

        Assert.NotNull(response.Value.Output);
        Assert.Contains(response.Value.Output, static item => item.Type is "function_call" or "message");
    }

    [LiveFact]
    public async Task Responses_Adapter_MapsTextUsageAndModel_Live()
    {
        var response = await _fixture.ResponsesChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "responses-adapter-ok")],
            new ChatOptions
            {
                Instructions = "Copy the user's ASCII token byte for byte on one line. Output exactly responses-adapter-ok without Markdown, spaces, or any other characters.",
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
                ModelId = DeepSeekTestModels.FlashModel,
                Temperature = 0,
                MaxOutputTokens = LiveMaxOutputTokens,
            },
            _fixture.CreateToken());

        Assert.Equal(DeepSeekTestModels.FlashModel, response.ModelId);
        Assert.Contains("responses-adapter-ok", response.Text, StringComparison.Ordinal);
        Assert.NotNull(response.Usage);
        Assert.True(response.Usage!.TotalTokenCount > 0);
        Assert.NotNull(response.Messages[0].RawRepresentation);
        Assert.IsType<Response>(response.Messages[0].RawRepresentation);
    }

    [LiveFact]
    public async Task Responses_Agent_ThroughAgui_Live()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIServer();

        await using var app = builder.Build();
        var agent = _fixture.ResponsesClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                Name = "deepseek-responses-agui-test-agent",
                ChatOptions = new ChatOptions
                {
                    Instructions = "Copy the user's ASCII token byte for byte on one line. Output exactly responses-agui-ok without Markdown, spaces, or any other characters.",
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
                    Temperature = 0,
                    MaxOutputTokens = LiveMaxOutputTokens,
                },
            });
        app.MapAGUIServer("/agui", agent);

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var requestBody = JsonSerializer.Serialize(new
            {
                threadId = Guid.NewGuid().ToString("N"),
                runId = Guid.NewGuid().ToString("N"),
                messages = new[]
                {
                    new { role = "user", content = "responses-agui-ok" },
                },
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "/agui")
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                _fixture.CreateToken());
            var body = await response.Content.ReadAsStringAsync(_fixture.CreateToken());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("RUN_STARTED", body, StringComparison.Ordinal);
            Assert.Contains("TEXT_MESSAGE_CONTENT", body, StringComparison.Ordinal);
            Assert.Contains("responses-agui-ok", GetAguiText(body), StringComparison.Ordinal);
            Assert.Contains("RUN_FINISHED", body, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private RequestOptions CreateRequestOptions()
        => new() { CancellationToken = _fixture.CreateToken() };

    private static string GetResponseText(Response response)
        => string.Concat(
            response.Output
                .Where(static item => item.Type == "message")
                .SelectMany(static item => item.Content)
                .Where(static content => content.Type == "output_text")
                .Select(static content => content.Text));

    private static string GetAguiText(string body)
    {
        var text = new StringBuilder();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line["data: ".Length..]);
            var data = document.RootElement;
            if (data.TryGetProperty("type", out var type)
                && type.GetString() == "TEXT_MESSAGE_CONTENT"
                && data.TryGetProperty("delta", out var delta))
            {
                text.Append(delta.GetString());
            }
        }

        return text.ToString();
    }
}
#pragma warning restore MAAI001
