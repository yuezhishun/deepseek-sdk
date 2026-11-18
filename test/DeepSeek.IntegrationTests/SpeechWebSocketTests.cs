using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DeepSeek.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeepSeek.IntegrationTests;

public sealed class SpeechWebSocketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpeechSocket_ReceivesFragmentedPacketsThroughServiceFinish(bool customAuthentication)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new List<string>();
        var finishSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAudioReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await ServeAsync(async (context, socket, token) =>
        {
            Assert.Equal("Bearer rotated-key", context.Request.Headers.Authorization);
            Assert.Equal("opus", context.Request.Query["format"]);
            Assert.Equal("tide", context.Request.Query["voice_id"]);
            Assert.Equal("false", context.Request.Query["filter_markdown"]);

            received.Add(await ReadTextEventAsync(socket, "text", token) ?? "");
            await socket.SendAsync(new byte[] { 1, 2 }.AsMemory(), WebSocketMessageType.Binary, false, token);
            await socket.SendAsync(new byte[] { 3 }.AsMemory(), WebSocketMessageType.Binary, true, token);
            received.Add(await ReadTextEventAsync(socket, "text", token) ?? "");
            Assert.Null(await ReadTextEventAsync(socket, "finish_text", token));

            await socket.SendAsync(new byte[] { 4, 5 }.AsMemory(), WebSocketMessageType.Binary, true, token);
            await SendEventAsync(socket, """{"event":"finish","code":"success"}""", token);
            finishSeen.TrySetResult();
        }, timeout.Token);

        var credential = new ApiKeyCredential("initial-key");
        var options = new DeepSeekClientOptions { Endpoint = new Uri(app.Urls.Single()) };
        var client = customAuthentication
            ? new DeepSeekClient(ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(credential, "X-Test-Key"), options)
            : new DeepSeekClient(credential, options);
        credential.Update("rotated-key");

        var audio = new List<byte[]>();
        await foreach (var frame in client.SynthesizeSpeechStreamingAsync(
            Segments(firstAudioReceived.Task, timeout.Token), new SpeechSynthesisOptions { Format = "opus", VoiceId = "tide", FilterMarkdown = false },
            customAuthentication ? credential : null, timeout.Token))
        {
            audio.Add(frame.ToArray());
            firstAudioReceived.TrySetResult();
        }

        await finishSeen.Task.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "Hello ", "world." }, received);
        Assert.Collection(audio, first => Assert.Equal(new byte[] { 1, 2, 3 }, first), last => Assert.Equal(new byte[] { 4, 5 }, last));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("service_error")]
    [InlineData("cancel")]
    public async Task SpeechSocket_RequiresSuccessfulFinishAfterPartialAudio(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stopClient = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var app = await ServeAsync(async (_, socket, token) =>
        {
            await ReadTextEventAsync(socket, "text", token);
            await ReadTextEventAsync(socket, "text", token);
            await ReadTextEventAsync(socket, "finish_text", token);
            await socket.SendAsync(new byte[] { 1, 2 }.AsMemory(), WebSocketMessageType.Binary, true, token);
            switch (scenario)
            {
                case "close":
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "no finish", token);
                    break;
                case "service_error":
                    await SendEventAsync(socket, """{"event":"finish","code":"service_error","message":"test failure"}""", token);
                    break;
                case "cancel":
                    // The peer stays open without a terminal event until the SDK cancels the read.
                    using (var receive = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        receive.CancelAfter(TimeSpan.FromSeconds(5));
                        try { await socket.ReceiveAsync(new byte[16].AsMemory(), receive.Token); }
                        catch (OperationCanceledException) when (receive.IsCancellationRequested) { }
                        catch (WebSocketException) { }
                    }
                    break;
            }
        }, timeout.Token);

        var client = new DeepSeekClient("loopback-key", new DeepSeekClientOptions { Endpoint = new Uri(app.Urls.Single()) });
        await using var updates = client.SynthesizeSpeechStreamingAsync(Segments(Task.CompletedTask, stopClient.Token),
            cancellationToken: stopClient.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(new byte[] { 1, 2 }, updates.Current.ToArray());

        if (scenario == "cancel")
        {
            var pending = updates.MoveNextAsync().AsTask();
            stopClient.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<IOException>(async () => await updates.MoveNextAsync());
            if (scenario == "service_error")
            {
                Assert.Equal("service_error", exception.Data["code"]);
                Assert.Equal("test failure", exception.Data["message"]);
            }
            else Assert.Contains("before a successful finish", exception.Message);
        }
    }

    [Fact]
    public async Task SpeechSocket_RejectsHttpOnlyFormatBeforeConnecting()
    {
        var client = new DeepSeekClient("loopback-key");
        await using var updates = client.SynthesizeSpeechStreamingAsync(Segments(Task.CompletedTask, CancellationToken.None),
            new SpeechSynthesisOptions { Format = "wav" }).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentException>(async () => await updates.MoveNextAsync());
    }

    private static async Task<WebApplication> ServeAsync(Func<HttpContext, WebSocket, CancellationToken, Task> handler, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/v1/tools/tts", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await handler(context, socket, token);
        });
        await app.StartAsync(token);
        return app;
    }

    private static async Task<string?> ReadTextEventAsync(WebSocket socket, string eventType, CancellationToken token)
    {
        var buffer = new byte[512];
        var message = await socket.ReceiveAsync(buffer.AsMemory(), token);
        Assert.True(message.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using var json = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
        Assert.Equal(eventType, json.RootElement.GetProperty("event").GetString());
        return json.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    private static ValueTask SendEventAsync(WebSocket socket, string json, CancellationToken token)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, token);

    private static async IAsyncEnumerable<string> Segments(Task firstAudioReceived, [EnumeratorCancellation] CancellationToken token)
    {
        yield return "Hello ";
        await firstAudioReceived.WaitAsync(token);
        yield return "world.";
    }
}
