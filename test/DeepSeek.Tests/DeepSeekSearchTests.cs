using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeek.Search;
using Xunit;

namespace DeepSeek.Tests;

public sealed class DeepSeekSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_RequestedContentAndLocale_AreSentWithoutLosingRankingOrRawResponse(bool synchronous)
    {
        var handler = new RecordingHandler("""
            {"results":[{"rerank_score":2.4,"title":"DeepSeek","url":"https://api-docs.deepseek.com/",
            "snippet":"short","summary":"long","content":"# Docs","site_name":null,"site_icon":null,
            "date":"2026-10-17T00:00:00Z","authority":4,"future":"retained"}]}
            """);
        var client = CreateClient(handler);
        var request = new SearchRequest
        {
            Query = "DeepSeek API",
            Count = 5,
            OriginalQuestion = "如何调用？",
            Localization = "127.0.0.1",
            NeedContent = true,
        };

        var response = synchronous ? client.Search(request) : await client.SearchAsync(request);

        Assert.Equal("/v1/tools/search", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(request.Query, body.RootElement.GetProperty("query").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(request.OriginalQuestion, body.RootElement.GetProperty("ori_question").GetString());
        Assert.Equal(request.Localization, body.RootElement.GetProperty("localization").GetString());
        Assert.True(body.RootElement.GetProperty("need_content").GetBoolean());
        var result = Assert.Single(response.Value);
        Assert.Equal(2.4, result.RerankScore);
        Assert.Equal("# Docs", result.Content);
        Assert.Equal(4, result.Authority);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T00:00:00Z"), result.Date);
        Assert.Null(result.SiteName);
        Assert.Contains("future", response.GetRawResponse().Content.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fetch_ValidPage_ReturnsMarkdownAndPreservesTheResponse(bool synchronous)
    {
        var handler = new RecordingHandler("""{"title":"Documentation","content":"# Documentation\nAPI details"}""");
        var client = CreateClient(handler);

        var result = synchronous ? client.Fetch("https://example.com/article") : await client.FetchAsync("https://example.com/article");

        Assert.Equal("/v1/tools/fetch", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("https://example.com/article", body.RootElement.GetProperty("url").GetString());
        Assert.Equal("Documentation", result.Value.Title);
        Assert.Equal("# Documentation\nAPI details", result.Value.Content);
        Assert.Equal(200, result.GetRawResponse().Status);
    }

    [Fact]
    public async Task Search_NoMatches_IsAValidEmptyResult()
    {
        var result = await CreateClient(new RecordingHandler("{\"results\":[]}")).SearchAsync(new SearchRequest { Query = "no-match" });
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Search_UnicodeLimit_CountsCharactersInsteadOfUtf16CodeUnits()
    {
        var handler = new RecordingHandler("{\"results\":[]}");
        var client = CreateClient(handler);
        var query = string.Concat(Enumerable.Repeat("\U0001F600", 128));

        await client.SearchAsync(new SearchRequest { Query = query, OriginalQuestion = query });
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(new SearchRequest { Query = query + "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(new SearchRequest { Query = "ok", OriginalQuestion = query + "x" }));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task Search_InvalidCount_IsRejectedBeforeSending(int count)
    {
        var handler = new RecordingHandler("{\"results\":[]}");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateClient(handler).SearchAsync(new SearchRequest { Query = "ok", Count = count }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task Search_CountBoundaries_AreAccepted(int count)
    {
        var handler = new RecordingHandler("{\"results\":[]}");
        await CreateClient(handler).SearchAsync(new SearchRequest { Query = "ok", Count = count });
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task Search_EmptyQuery_IsRejectedBeforeSending(string query)
    {
        var handler = new RecordingHandler("{\"results\":[]}");
        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).SearchAsync(new SearchRequest { Query = query }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("ftp://example.com/a")]
    [InlineData("https://user:password@example.com/a")]
    [InlineData("/relative")]
    [InlineData("")]
    public async Task Fetch_UnsupportedOrCredentialBearingUrl_IsRejectedBeforeSending(string url)
    {
        var handler = new RecordingHandler("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).FetchAsync(url));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_OversizedUrl_IsRejectedBeforeSending()
    {
        var handler = new RecordingHandler("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).FetchAsync("https://example.com/" + new string('a', 8192)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Search_ServiceFailure_PreservesStatusAndErrorDetails(int status)
    {
        var handler = new RecordingHandler("""{"error":{"message":"search failed","type":"invalid_request_error","code":"invalid_request_error"}}""", status);
        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => CreateClient(handler).SearchAsync(new SearchRequest { Query = "ok" }));
        Assert.Equal(status, exception.Status);
        Assert.Contains("search failed", exception.Message);
    }

    [Fact]
    public async Task Search_CancelledRequest_DoesNotProduceASuccessResult()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient(new RecordingHandler("{\"results\":[]}")).SearchAsync(
            new SearchRequest { Query = "ok" }, new RequestOptions { CancellationToken = source.Token }));
    }

    [Fact]
    public async Task Search_UnbufferedRequest_IsRejectedForTypedResults()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient(new RecordingHandler("{\"results\":[]}")).SearchAsync(
            new SearchRequest { Query = "ok" }, new RequestOptions { BufferResponse = false }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SearchAndFetch_CancellationInterruptsPendingWork(bool fetch, bool synchronous)
    {
        var client = CreateClient(new RecordingHandler("{}", waitForCancellation: true));
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var options = new RequestOptions { CancellationToken = source.Token };
        if (synchronous)
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                if (fetch) client.Fetch("https://example.com", options);
                else client.Search(new SearchRequest { Query = "ok" }, options);
            });
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (fetch) await client.FetchAsync("https://example.com", options);
                else await client.SearchAsync(new SearchRequest { Query = "ok" }, options);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAndFetch_NetworkTimeout_DoesNotBecomeAnEmptySuccess(bool fetch)
    {
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(new RecordingHandler("{}", waitForCancellation: true))),
            RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = TimeSpan.FromMilliseconds(100),
        });
        // System.ClientModel CancellationHelper reports network timeout as TaskCanceledException.
        var exception = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            if (fetch) await client.FetchAsync("https://example.com");
            else await client.SearchAsync(new SearchRequest { Query = "ok" });
        });
        Assert.Contains("ClientPipelineOptions.NetworkTimeout", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fetch_ServiceFailure_PreservesStatusInBothEntrypoints(bool synchronous)
    {
        var client = CreateClient(new RecordingHandler("{\"error\":{\"message\":\"fetch failed\"}}", 429));
        var exception = synchronous
            ? Assert.Throws<DeepSeekException>(() => client.Fetch("https://example.com"))
            : await Assert.ThrowsAsync<DeepSeekException>(() => client.FetchAsync("https://example.com"));
        Assert.Equal(429, exception.Status);
        Assert.Contains("fetch failed", exception.Message);
    }

    private static DeepSeekClient CreateClient(RecordingHandler handler)
        => new("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0),
        });

    private sealed class RecordingHandler(string json, int status = 200, bool waitForCancellation = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public int Calls { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (waitForCancellation) cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Send(request, cancellationToken);
        }
    }
}
