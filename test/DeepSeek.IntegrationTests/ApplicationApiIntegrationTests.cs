using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeepSeek.Agents.AI;
using DeepSeek.Anthropic;
using DeepSeek.Files;
using DeepSeek.Responses;
using DeepSeek.Tools;
using Microsoft.Extensions.AI;
using Xunit;
using WireChatMessage = DeepSeek.Chat.ChatMessage;

namespace DeepSeek.IntegrationTests;

public sealed class ApplicationApiIntegrationTests(DeepSeekIntegrationFixture fixture) : IClassFixture<DeepSeekIntegrationFixture>
{
    [LiveFact]
    public async Task ApplicationTools_OcrReturnsOriginalPage()
    {
        using var document = CreatePdf("Copper Lantern 417");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await fixture.Client.OcrAsync(document, "ocr-check.pdf", "text_recognition",
            new RequestOptions { CancellationToken = timeout.Token });
        using var raw = result.GetRawResponse();
        Assert.Null(result.Value.Error);
        Assert.Equal(1, result.Value.PageCount);
        var page = Assert.Single(result.Value.Pages);
        Assert.Equal(0, page.Index);
        Assert.Equal("stop", page.FinishReason);
        Assert.Contains("Copper Lantern 417", page.Text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task ApplicationTools_HttpSpeechCanBeTranscribed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = new RequestOptions { CancellationToken = timeout.Token };
        var synthesis = await fixture.Client.SynthesizeSpeechAsync("Hello from DeepSeek. This is a test.", options: options);
        using var response = synthesis.GetRawResponse();
        using var wav = new MemoryStream();
        await synthesis.Value.CopyToAsync(wav, timeout.Token);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav.GetBuffer(), 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav.GetBuffer(), 8, 4));
        wav.Position = 0;
        var transcription = await fixture.Client.TranscribeSpeechAsync(wav, "speech.wav", options);
        using var raw = transcription.GetRawResponse();
        Assert.False(string.IsNullOrWhiteSpace(transcription.Value.RequestId));
        Assert.InRange(transcription.Value.DurationMs, 1, 120_000);
        Assert.Contains("hello", transcription.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task ApplicationTools_WebSocketSpeechDeliversCompletePcm()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        long bytes = 0;
        await foreach (var frame in fixture.Client.SynthesizeSpeechStreamingAsync(
            SpeechSegments(), new SpeechSynthesisOptions { Format = "pcm" }, cancellationToken: timeout.Token))
        {
            Assert.NotEmpty(frame.ToArray());
            bytes += frame.Length;
        }

        Assert.True(bytes > 1000);
        Assert.Equal(0, bytes % 2);
    }

    [LiveFact]
    public async Task Files_ParsedDocumentsCanBeReferencedAcrossProtocols()
    {
        var client = fixture.Client.GetFilesClient();
        var anthropic = fixture.Client.GetAnthropicFilesClient();
        var uploaded = new List<(FilesClient Owner, string Id)>();
        try
        {
            const string openAiText = "Copper Lantern 417";
            const string anthropicText = "Silver Kestrel 283";
            foreach (var (owner, label, purpose, fileName) in new[]
            {
                (client, openAiText, "text_recognition", "openai-api-check.docx"),
                (anthropic, anthropicText, "rich_layout_recognition", "anthropic-api-check.pdf"),
            })
            {
                using var document = fileName.EndsWith(".docx", StringComparison.Ordinal) ? CreateWordDocument(label) : CreatePdf(label);
                using var uploadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var upload = await owner.UploadFileAsync(document, fileName, new FileUploadOptions { Purpose = purpose },
                    new RequestOptions { CancellationToken = uploadTimeout.Token });
                using var raw = upload.GetRawResponse();
                Assert.False(string.IsNullOrWhiteSpace(upload.Value.Id));
                uploaded.Add((owner, upload.Value.Id));
            }
            Assert.NotEqual(uploaded[0].Id, uploaded[1].Id);

            var files = await client.ListFilesAsync(new FileListOptions { Purpose = "rich_layout_recognition", Order = "desc", Limit = 10 },
                new RequestOptions { CancellationToken = fixture.CreateToken(30) });
            using (files.GetRawResponse())
                Assert.Contains(files.Value.Data, file => file.Id == uploaded[1].Id);

            const string wordMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            foreach (var (adapter, id, label, mimeType) in new[]
            {
                (fixture.OpenAiChatClient, uploaded[1].Id, anthropicText, "application/pdf"),
                (fixture.ResponsesChatClient, uploaded[0].Id, openAiText, wordMimeType),
                (fixture.AnthropicChatClient, uploaded[0].Id, openAiText, wordMimeType),
            })
            {
                var document = new HostedFileContent(id) { MediaType = mimeType };
                var result = await adapter.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, [new TextContent("Read the attached inventory sheet. Reply with its printed inventory label exactly as written."), document])],
                    Options(thinking: false), fixture.CreateToken(60));
                Assert.True(result.Text.Contains(label, StringComparison.OrdinalIgnoreCase),
                    $"{adapter.GetType().Name} did not return the inventory label '{label}'. Response: {result.Text}");
            }

            var inline = await fixture.AnthropicChatClient.GetResponseAsync([new ChatMessage(ChatRole.User,
                [new TextContent("Quote the inventory label in the document."), new DataContent("Inventory label: Topaz Finch 952."u8.ToArray(), "text/plain")])],
                Options(thinking: false), fixture.CreateToken(60));
            Assert.Contains("Topaz Finch 952", inline.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            foreach (var (owner, id) in uploaded)
            {
                try
                {
                    var deletion = await owner.DeleteFileAsync(id, new RequestOptions { CancellationToken = fixture.CreateToken(30) });
                    using var raw = deletion.GetRawResponse();
                    Assert.True(deletion.Value.Deleted);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                }
            }
            if (cleanupErrors.Count > 0) throw new AggregateException("Failed to remove one or more test documents.", cleanupErrors);
        }
    }

    [LiveFact]
    public Task Chat_SearchReadAndReplay() => VerifyConversationAsync(fixture.OpenAiChatClient, supportsReminder: true);

    [LiveFact]
    public Task Responses_SearchReadAndReplay() => VerifyConversationAsync(fixture.ResponsesChatClient, supportsReminder: false);

    [LiveFact]
    public Task Messages_SearchReadAndReplay() => VerifyConversationAsync(fixture.AnthropicChatClient, supportsReminder: true);

    private async Task VerifyConversationAsync(IChatClient client, bool supportsReminder)
    {
        var plain = Options(thinking: false);
        plain.AdditionalProperties![DeepSeekChatOptionKeys.WebSearch] = true;
        plain.AdditionalProperties[DeepSeekChatOptionKeys.SafetyCheckLevel] = "high";
        plain.AdditionalProperties[DeepSeekChatOptionKeys.GenerateTitle] = true;
        List<ChatMessage> messages = [];
        if (supportsReminder)
            messages.Add(new ChatMessage(new ChatRole("latest_reminder"), "2026-11-18,Wednesday,Beijing,Web,English"));
        messages.Add(new ChatMessage(ChatRole.User, "Search the web for the official DeepSeek full suite API announcement. Answer in one sentence."));
        var first = await client.GetResponseAsync(messages, plain, fixture.CreateToken(75));
        Assert.False(string.IsNullOrWhiteSpace(first.Text));
        Assert.Contains("search", GetWebKinds(first));
        Assert.True(first.Usage?.AdditionalCounts?.TryGetValue("tool_search", out var searches) == true && searches > 0);
        Assert.True(first.AdditionalProperties?.TryGetValue("title", out var title) == true && title is string { Length: > 0 });

        var deep = Options(thinking: true);
        deep.AdditionalProperties![DeepSeekChatOptionKeys.WebSearch] = true;
        var second = await client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Use web search to verify the latest DeepSeek API announcement. Find two distinct official sources and give one short line.")],
            deep, fixture.CreateToken(120)).ToChatResponseAsync();
        Assert.False(string.IsNullOrWhiteSpace(second.Text));
        Assert.Contains("deep_search", GetWebKinds(second));

        var read = Options(thinking: false);
        read.AdditionalProperties![DeepSeekChatOptionKeys.ReadUrl] = true;
        var user = new ChatMessage(ChatRole.User, "Read https://example.com/ and tell me the title, without repeating the URL.");
        var third = await client.GetStreamingResponseAsync([user], read, fixture.CreateToken(75)).ToChatResponseAsync();
        Assert.Contains("Example Domain", third.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read_link", GetWebKinds(third));
        Assert.True(HasReadSignature(third));

        var continuation = await client.GetResponseAsync([user, .. third.Messages,
            new ChatMessage(ChatRole.User, "According to the same page, what is the domain for? Answer with a short exact quote.")],
            read, fixture.CreateToken(60));
        Assert.Contains("documentation", continuation.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string?> GetWebKinds(ChatResponse result)
    {
        foreach (var raw in GetNative(result))
        {
            if (raw is Response responses)
                foreach (var item in responses.Output.Where(static item => item.Type == "web_search_call")) yield return item.Kind;
            if (raw is AnthropicMessageResponse messages)
                foreach (var item in messages.Content.Where(static item => item.Type == "server_tool_use")) yield return item.Kind;
            if (raw is WireChatMessage { ServerToolCalls: { } steps })
                foreach (var call in steps.SelectMany(static step => step.GetProperty("calls").EnumerateArray())) yield return call.GetProperty("kind").GetString();
        }
    }

    private static bool HasReadSignature(ChatResponse result) => GetNative(result).Any(raw => raw switch
    {
        Response responses => responses.Output.Any(item => item.Kind == "read_link" && !string.IsNullOrEmpty(item.Signature)),
        AnthropicMessageResponse messages => messages.Content.Any(item => item.Kind == "read_link" && !string.IsNullOrEmpty(item.Signature)),
        WireChatMessage { ServerToolCalls: { } steps } => steps.SelectMany(step => step.GetProperty("calls").EnumerateArray())
            .Any(call => call.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String && signature.GetString()!.Length > 0),
        _ => false,
    });

    private static IEnumerable<object?> GetNative(ChatResponse response)
        => response.Messages.SelectMany(static message => message.Contents.Select(static content => content.RawRepresentation).Append(message.RawRepresentation));

    private static ChatOptions Options(bool thinking) => new()
    {
        MaxOutputTokens = thinking ? 4096 : 512,
        AdditionalProperties = new AdditionalPropertiesDictionary { [DeepSeekChatOptionKeys.Thinking] = thinking ? "enabled" : "disabled" },
    };

    private static async IAsyncEnumerable<string> SpeechSegments()
    {
        yield return "Hello from ";
        await Task.Yield();
        yield return "DeepSeek.";
    }

    private static MemoryStream CreatePdf(string text)
    {
        var page = $"BT\n/F1 24 Tf\n72 720 Td\n(Inventory sheet) Tj\n0 -44 Td\n({text}) Tj\n0 -44 Td\n(Inventory label: {text}) Tj\nET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {page.Length} >>\nstream\n{page}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        List<int> offsets = [];
        foreach (var obj in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{obj}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return new MemoryStream(Encoding.ASCII.GetBytes(pdf.ToString()));
    }

    private static MemoryStream CreateWordDocument(string label)
    {
        var stream = new MemoryStream();
        using (var package = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            Write("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Write("word/document.xml", $"""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>Inventory label: {label}</w:t></w:r></w:p></w:body>
                </w:document>
                """);

            void Write(string name, string xml)
            {
                using var entry = package.CreateEntry(name).Open();
                using var writer = new StreamWriter(entry);
                writer.Write(xml);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
