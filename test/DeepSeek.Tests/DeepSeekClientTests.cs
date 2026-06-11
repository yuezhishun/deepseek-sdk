using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Channels;
using DeepSeek;
using DeepSeek.Anthropic;
using DeepSeek.Chat;
using DeepSeek.Completions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeepSeek.Tests;

public class DeepSeekClientTests
{
    [Fact]
    public async Task ChatClient_SendsExpectedHeadersPathAndBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            UserAgentApplicationId = "tests",
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            Thinking = ThinkingMode.Enabled,
            ReasoningEffort = ChatReasoningEffort.High,
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("tests", string.Join(" ", handler.LastRequest.Headers.UserAgent.Select(static part => part.ToString())));

        var body = handler.LastRequestBody!;
        Assert.Contains("\"model\":\"deepseek-v4-pro\"", body);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", body);
        Assert.Contains("\"reasoning_effort\":\"high\"", body);
    }

    [Fact]
    public async Task ChatClient_RequestBody_UsesRelaxedJsonEscapingForEmbeddedJsonAndUnicode()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "请按这个 JSON 返回：{\"question\":\"今天天气\",\"answer\":\"晴天😀\"}",
                },
            ],
        });

        var body = Assert.IsType<string>(handler.LastRequestBody);
        Assert.DoesNotContain("\\u0022question\\u0022", body, StringComparison.Ordinal);
        Assert.Contains("\\\"question\\\":\\\"今天天气\\\"", body, StringComparison.Ordinal);
        Assert.Contains("请按这个 JSON 返回", body, StringComparison.Ordinal);
        Assert.Contains("今天天气", body, StringComparison.Ordinal);
        Assert.Contains("晴天", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_MapsErrorToDeepSeekException()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid request\",\"type\":\"invalid_request_error\"}}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }));

        Assert.Equal(400, exception.Status);
        Assert.Equal("invalid request", exception.Message);
        Assert.Equal("https://api.deepseek.com/chat/completions", exception.RequestUri.ToString());
        Assert.Contains("invalid_request_error", exception.ResponseContent);
    }

    [Fact]
    public async Task ChatClient_ParsesStreamingServerSentEvents()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var chunks = new List<ChatCompletion>();
        await foreach (var chunk in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hel", chunks[0].Choices[0].Delta?.Content);
        Assert.Equal("lo", chunks[1].Choices[0].Delta?.Content);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_WithDefaultLogging_DoesNotConsumeResponseStream()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new RecordingLoggerProvider());
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var chunks = new List<ChatCompletion>();
        await foreach (var chunk in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hel", chunks[0].Choices[0].Delta?.Content);
        Assert.Equal("lo", chunks[1].Choices[0].Delta?.Content);
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var client = CreateChatClient(handler);
        var firstChunk = new TaskCompletionSource<ChatCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CompleteChatStreaming(
                new ChatCompletionRequest
                {
                    Messages = [new ChatMessage { Role = "user", Content = "hello" }],
                },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Choices[0].Delta?.Content);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_SendsStreamOptionsInRequestBody()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        await foreach (var _ in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            StreamOptions = new StreamOptions { IncludeUsage = true },
        }))
        {
        }

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"stream_options\":{\"include_usage\":true}", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteChatAsync(
            new ChatCompletionRequest
            {
                Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CompleteChatAsync'.", exception.Message);
    }

    [Fact]
    public async Task CompletionClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetCompletionsClient("deepseek-v4-pro");
        var firstChunk = new TaskCompletionSource<Completion>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CompleteTextStreaming(
                new CompletionRequest { Prompt = "hello" },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"choices\":[{\"text\":\"Hel\"}]}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Choices[0].Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletionClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetCompletionsClient("deepseek-v4-pro");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteTextAsync(
            new CompletionRequest { Prompt = "hello" },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CompleteTextAsync'.", exception.Message);
    }

    [Fact]
    public async Task AnthropicClient_NonStreamingRequest_SendsStreamFalseInRequestBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        var response = await client.CreateMessageAsync(new AnthropicMessageRequest
        {
            Stream = true,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                },
            ],
        });

        Assert.Equal("ok", response.Value.Content[0].Text);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":false", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicClient_StreamingRequest_SendsStreamTrueInRequestBody()
    {
        var sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"ok\"}}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        await foreach (var _ in client.CreateMessageStreaming(new AnthropicMessageRequest
        {
            Stream = false,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                },
            ],
        }))
        {
        }

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);
        var firstChunk = new TaskCompletionSource<AnthropicStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CreateMessageStreaming(
                new AnthropicMessageRequest
                {
                    Messages =
                    [
                        new AnthropicMessage
                        {
                            Role = "user",
                            Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                        },
                    ],
                },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"Hel\"}}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Delta?.Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnthropicClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                Messages =
                [
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                    },
                ],
            },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CreateMessageAsync'.", exception.Message);
    }

    [Fact]
    public void ClientOptions_CloneAndFreeze_DisablesMessageContentLoggingByDefault()
    {
        var options = new DeepSeekClientOptions
        {
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = NullLoggerFactory.Instance,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 2048,
            },
        };
        options.ClientLoggingOptions.AllowedHeaderNames.Add("x-test-header");
        options.ClientLoggingOptions.AllowedQueryParameters.Add("api-version");

        var client = new DeepSeekClient("test-key", options);
        var frozenLoggingOptions = Assert.IsType<System.ClientModel.Primitives.ClientLoggingOptions>(client.Options.ClientLoggingOptions);

        Assert.NotSame(options.ClientLoggingOptions, frozenLoggingOptions);
        Assert.Same(NullLoggerFactory.Instance, frozenLoggingOptions.LoggerFactory);
        Assert.True(frozenLoggingOptions.EnableLogging);
        Assert.True(frozenLoggingOptions.EnableMessageLogging);
        Assert.False(frozenLoggingOptions.EnableMessageContentLogging);
        Assert.Equal(2048, frozenLoggingOptions.MessageContentSizeLimit);
        Assert.Contains("x-test-header", frozenLoggingOptions.AllowedHeaderNames);
        Assert.Contains("api-version", frozenLoggingOptions.AllowedQueryParameters);

        options.ClientLoggingOptions.MessageContentSizeLimit = 1024;
        options.ClientLoggingOptions.AllowedHeaderNames.Add("x-second-header");

        Assert.Equal(2048, frozenLoggingOptions.MessageContentSizeLimit);
        Assert.DoesNotContain("x-second-header", frozenLoggingOptions.AllowedHeaderNames);
    }

    [Fact]
    public void ClientOptions_CloneAndFreeze_EnablesMessageContentLogging_WhenExplicitlyAllowed()
    {
        var options = new DeepSeekClientOptions
        {
            AllowMessageContentLogging = true,
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = NullLoggerFactory.Instance,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 2048,
            },
        };

        var client = new DeepSeekClient("test-key", options);
        var frozenLoggingOptions = Assert.IsType<System.ClientModel.Primitives.ClientLoggingOptions>(client.Options.ClientLoggingOptions);

        Assert.True(client.Options.AllowMessageContentLogging);
        Assert.True(frozenLoggingOptions.EnableMessageContentLogging);
    }

    [Fact]
    public async Task ChatClient_EmitsPipelineMetadataLogs_WhenMessageContentLoggingIsNotOptedIn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content);
        Assert.Contains(provider.Entries, static entry => entry.Category.StartsWith("System.ClientModel", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("Bearer test-key", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("hello", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("\"choices\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatClient_EmitsPipelineLogsAndMessageContent_WhenExplicitlyOptedIn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            AllowMessageContentLogging = true,
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content);
        Assert.Contains(provider.Entries, static entry => entry.Category.StartsWith("System.ClientModel", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("Bearer test-key", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("\"choices\"", StringComparison.Ordinal));
    }

    private static ChatClient CreateChatClient(RecordingHandler handler)
    {
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");
    }

    private static RecordingHandler CreateStreamingHandler(Stream stream)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        return handler;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private Action<HttpResponseMessage>? _configureResponse;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public void ConfigureResponse(Action<HttpResponseMessage> configureResponse)
        {
            _configureResponse = configureResponse;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var response = _responseFactory(request);
            _configureResponse?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void WriteChunk(string chunk)
            => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(chunk));

        public void Complete()
            => _chunks.Writer.TryComplete();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_currentChunk is not null && _offset < _currentChunk.Length)
                {
                    var bytesToCopy = Math.Min(buffer.Length, _currentChunk.Length - _offset);
                    _currentChunk.AsMemory(_offset, bytesToCopy).CopyTo(buffer);
                    _offset += bytesToCopy;
                    if (_offset >= _currentChunk.Length)
                    {
                        _currentChunk = null;
                        _offset = 0;
                    }

                    return bytesToCopy;
                }

                if (await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                    _chunks.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _offset = 0;
                    continue;
                }

                return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly List<LogEntry> _entries;

        public RecordingLogger(string categoryName, List<LogEntry> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(_categoryName, logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
