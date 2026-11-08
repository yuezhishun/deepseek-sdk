using System.ClientModel.Primitives;
using System.Text;
using DeepSeek.Agents.AI;
using DeepSeek.Anthropic;
using DeepSeek.Chat;
using DeepSeek.Files;
using DeepSeek.Responses;
using DeepSeek.Search;
using Microsoft.Extensions.AI;
using TestSupport;
using Xunit;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using WireMessage = DeepSeek.Chat.ChatMessage;

namespace DeepSeek.IntegrationTests;

[Trait("Category", "Live")]
public sealed class ApiCompatibilityIntegrationTests(DeepSeekIntegrationFixture fixture) : IClassFixture<DeepSeekIntegrationFixture>
{
    private const string ImageBase64 = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAb0lEQVR4nO3PAQkAAAyEwO9feoshgnABdLep8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3IPanc8OLDQitxAAAAAElFTkSuQmCC";

    [LiveFact]
    public async Task SearchAndFetch_ReturnRealSearchResultsAndMarkdown()
    {
        var result = await fixture.Client.SearchAsync(new SearchRequest
        {
            Query = "site:api-docs.deepseek.com Search Fetch API",
            OriginalQuestion = "Where are the DeepSeek Search and Fetch API docs?",
            Count = 2,
            NeedContent = true,
        }, RequestOptions());
        Assert.NotEmpty(result.Value);
        Assert.All(result.Value, item =>
        {
            Assert.True(Uri.TryCreate(item.Url, UriKind.Absolute, out _));
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.InRange(item.RerankScore, 0, 3);
            Assert.InRange(item.Authority, -1, 4);
        });
        var page = await fixture.Client.FetchAsync("https://api-docs.deepseek.com/", RequestOptions());
        Assert.False(string.IsNullOrWhiteSpace(page.Value.Content));
        Assert.Equal(200, page.GetRawResponse().Status);
    }

    [LiveFact]
    public async Task Files_BothProtocolsShareUploadedImagesAndCleanUpTheirOwnFiles()
    {
        var openAi = fixture.Client.GetFilesClient();
        var anthropic = fixture.Client.GetAnthropicFilesClient();
        var uploaded = new List<(FilesClient Client, string Id)>();
        Exception? testFailure = null;
        try
        {
            foreach (var files in new[] { openAi, anthropic })
            {
                using var image = new MemoryStream(Convert.FromBase64String(ImageBase64));
                var response = await files.UploadFileAsync(image, "sdk-compat-" + Guid.NewGuid().ToString("N") + ".png", requestOptions: RequestOptions());
                Assert.False(string.IsNullOrWhiteSpace(response.Value.Id));
                uploaded.Add((files, response.Value.Id!));
                var other = ReferenceEquals(files, openAi) ? anthropic : openAi;
                var metadata = await other.RetrieveFileAsync(response.Value.Id!, RequestOptions());
                Assert.Equal(response.Value.Id, metadata.Value.Id);
                Assert.Equal(image.Length, metadata.Value.Bytes);
                Assert.True(metadata.Value.CreatedAt > 0);
                var page = await files.ListFilesAsync(new FileListOptions { Limit = 1 }, RequestOptions());
                Assert.NotNull(page.Value.Data);
            }

            foreach (var protocol in new[] { "chat", "anthropic", "responses" })
            {
                var client = CreateAdapter(protocol);
                var response = await client.GetResponseAsync(
                    [new AiMessage(ChatRole.User, [new TextContent("What is the dominant color? Reply with exactly red."), new HostedFileContent(uploaded[0].Id) { MediaType = "image/png" }])],
                    Options(), fixture.CreateToken());
                Assert.Equal("red", response.Text.Trim().TrimEnd('.').ToLowerInvariant());

                var result = await client.GetResponseAsync(
                [
                    new AiMessage(ChatRole.User, "Inspect the image returned by screenshot and reply with exactly red for its dominant color."),
                    new AiMessage(ChatRole.Assistant, [new FunctionCallContent("image_call", "screenshot", new Dictionary<string, object?>())]),
                    new AiMessage(ChatRole.Tool, [new FunctionResultContent("image_call", new List<AIContent> { new HostedFileContent(uploaded[1].Id) { MediaType = "image/png" } })]),
                ], Options(), fixture.CreateToken());
                Assert.Equal("red", result.Text.Trim().TrimEnd('.').ToLowerInvariant());
            }
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var file in uploaded)
            {
                try
                {
                    var deleted = await file.Client.DeleteFileAsync(file.Id, RequestOptions());
                    Assert.True(deleted.Value.Deleted);
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (cleanupFailures.Count > 0)
            {
                if (testFailure is not null) cleanupFailures.Insert(0, testFailure);
                throw new AggregateException("本轮创建的文件清理失败。", cleanupFailures);
            }
        }
    }

    [LiveFact]
    public async Task Vision_InlineAndUrlImages_AreAcceptedByAllThreeProtocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        {
            foreach (var image in new AIContent[]
            {
                new DataContent(Convert.FromBase64String(ImageBase64), "image/png"),
                new UriContent("https://cdn.deepseek.com/official_account.jpg", "image/jpeg"),
            })
            {
                var response = await CreateAdapter(protocol).GetResponseAsync(
                    [new AiMessage(ChatRole.User, [new TextContent("This is an image input protocol check. Reply with exactly vision-ok."), image])],
                    Options(), fixture.CreateToken());
                Assert.Equal("vision-ok", response.Text.Trim());
            }
        }
    }

    [LiveFact]
    public async Task Reasoning_AllSupportedEfforts_WorkAcrossAllThreeProtocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        {
            foreach (var effort in new[] { ReasoningEffort.None, ReasoningEffort.Low, ReasoningEffort.High, ReasoningEffort.ExtraHigh })
            {
                var options = Options();
                options.Reasoning = new ReasoningOptions { Effort = effort };
                options.MaxOutputTokens = effort == ReasoningEffort.None ? 64 : 1024;
                var response = await CreateAdapter(protocol).GetResponseAsync(
                    [new AiMessage(ChatRole.User, "Calculate 17 * 19. Reply with exactly 323.")], options, fixture.CreateToken());
                Assert.Equal("323", response.Text.Trim());
                if (effort == ReasoningEffort.None) Assert.Empty(response.Messages.SelectMany(x => x.Contents).OfType<TextReasoningContent>());
                else Assert.Contains(response.Messages.SelectMany(x => x.Contents), x => x is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text));
            }
        }
    }

    [LiveFact]
    public async Task ServerSearch_HistoryAndUsage_WorkForResponsesAndAnthropic()
    {
        foreach (var protocol in new[] { "responses", "anthropic" })
        {
            var client = CreateAdapter(protocol, DeepSeekTestModels.ProModel);
            var options = Options();
            options.MaxOutputTokens = 512;
            options.Tools = [new HostedWebSearchTool(new Dictionary<string, object?> { ["max_uses"] = 1 })];
            if (protocol == "anthropic") options.AdditionalProperties = new() { [DeepSeekChatOptionKeys.ToolChoiceName] = "web_search" };
            else options.ToolMode = ChatToolMode.RequireAny;
            var user = new AiMessage(ChatRole.User, "Search the official DeepSeek API documentation once and give one result URL.");
            var response = await client.GetResponseAsync([user], options, fixture.CreateToken());
            Assert.True(response.Usage?.AdditionalCounts?["tool_search"] > 0);
            Assert.False(string.IsNullOrWhiteSpace(response.Text));
            var continuation = await client.GetResponseAsync([user, .. response.Messages, new AiMessage(ChatRole.User, "Reply only with the hostname of the result you found.")], Options(), fixture.CreateToken());
            Assert.False(string.IsNullOrWhiteSpace(continuation.Text));

            var streaming = await client.GetStreamingResponseAsync([user], options, fixture.CreateToken()).ToChatResponseAsync();
            Assert.True(streaming.Usage?.AdditionalCounts?["tool_search"] > 0);
            var streamedContinuation = await client.GetResponseAsync([user, .. streaming.Messages, new AiMessage(ChatRole.User, "Reply only with the hostname of the result you found.")], Options(), fixture.CreateToken());
            Assert.False(string.IsNullOrWhiteSpace(streamedContinuation.Text));
        }
    }

    [LiveFact]
    public async Task StreamingTools_ProduceCompleteCallsAndAcceptTheirResultsAcrossProtocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        {
            var client = CreateAdapter(protocol);
            var options = Options();
            options.Tools = [AIFunctionFactory.Create((string city) => "sunny", "lookup_weather", "Get weather for a city.")];
            options.ToolMode = ChatToolMode.RequireAny;
            var user = new AiMessage(ChatRole.User, "Call lookup_weather with city Hangzhou.");
            var response = await client.GetStreamingResponseAsync([user], options, fixture.CreateToken()).ToChatResponseAsync();
            var call = Assert.Single(response.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>());
            Assert.Equal("lookup_weather", call.Name);
            Assert.Equal("Hangzhou", call.Arguments!["city"]!.ToString());
            Assert.True(response.Usage?.TotalTokenCount > 0);
            var continuation = await client.GetResponseAsync(
                [user, .. response.Messages, new AiMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "sunny")]), new AiMessage(ChatRole.User, "Reply with exactly sunny.")],
                Options(), fixture.CreateToken());
            Assert.Equal("sunny", continuation.Text.Trim());
        }
    }

    [LiveFact]
    public async Task JsonOutput_ProducesParseableObjectsAndSchemaFieldsOnSupportedProtocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        {
            foreach (var schema in new[] { false, true })
            {
                var options = Options();
                options.ResponseFormat = schema
                    ? ChatResponseFormat.ForJsonSchema<StructuredJsonEvidenceResponse>(schemaName: "evidence", schemaDescription: "A structured response")
                    : ChatResponseFormat.Json;
                var response = await CreateAdapter(protocol).GetResponseAsync(
                    [new AiMessage(ChatRole.User, "Return only this JSON object: {\"state\":{\"stage\":\"ok\"},\"assistantMessage\":\"ok\"}")], options, fixture.CreateToken());
                using var document = System.Text.Json.JsonDocument.Parse(response.Text);
                Assert.Equal("ok", document.RootElement.GetProperty("state").GetProperty("stage").GetString());
                Assert.Equal("ok", document.RootElement.GetProperty("assistantMessage").GetString());
            }
        }
    }

    [LiveFact]
    public async Task JsonOutput_StreamingPreservesStringContentsAcrossAllProtocolsAndBothModels()
    {
        foreach (var model in new[] { DeepSeekTestModels.FlashModel, DeepSeekTestModels.ProModel })
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        foreach (var schema in new[] { false, true })
        {
            var options = Options();
            options.Instructions = "字符串内容使用英语，原样保留输入内容。";
            options.ResponseFormat = schema ? ChatResponseFormat.ForJsonSchema<Dictionary<string, string>>() : ChatResponseFormat.Json;
            var response = await CreateAdapter(protocol, model).GetStreamingResponseAsync(
                [new AiMessage(ChatRole.User, "只返回这个 JSON 对象：{\"message\":\"hello world 中文 😀\\n\\\"quoted\\\"\"}")], options, fixture.CreateToken()).ToChatResponseAsync();
            using var document = System.Text.Json.JsonDocument.Parse(response.Text);
            Assert.Equal("hello world 中文 😀\n\"quoted\"", document.RootElement.GetProperty("message").GetString());
            Assert.True(response.Usage?.TotalTokenCount > 0);
        }
    }

    [LiveFact]
    public async Task JsonSchema_ArrayRootWorksInStreamingAndNonStreamingAcrossAllProtocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        foreach (var stream in new[] { false, true })
        {
            var options = Options();
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<int[]>();
            var messages = new[] { new AiMessage(ChatRole.User, "仅返回 JSON 数组 [1,2,3]，不要包装为对象。") };
            var client = CreateAdapter(protocol);
            var response = stream
                ? await client.GetStreamingResponseAsync(messages, options, fixture.CreateToken()).ToChatResponseAsync()
                : await client.GetResponseAsync(messages, options, fixture.CreateToken());
            using var document = System.Text.Json.JsonDocument.Parse(response.Text);
            Assert.Equal(new[] { 1, 2, 3 }, document.RootElement.EnumerateArray().Select(x => x.GetInt32()));
        }
    }

    private IChatClient CreateAdapter(string protocol, string? model = null)
        => protocol switch
        {
            "chat" => fixture.Client.GetChatClient(model ?? DeepSeekTestModels.FlashModel).AsIChatClient(),
            "anthropic" => fixture.Client.GetAnthropicClient(model ?? DeepSeekTestModels.FlashModel).AsIChatClient(),
            _ => fixture.Client.GetResponsesClient(model ?? DeepSeekTestModels.FlashModel).AsIChatClient(),
        };

    private static ChatOptions Options()
        => new() { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }, Temperature = 0, MaxOutputTokens = 128 };

    private RequestOptions RequestOptions()
        => new() { CancellationToken = fixture.CreateToken() };
}
