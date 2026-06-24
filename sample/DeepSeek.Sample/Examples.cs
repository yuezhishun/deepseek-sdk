using DeepSeek;
using DeepSeek.Billing;
using DeepSeek.Chat;
using DeepSeek.Agents.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sample;

internal class Examples
{
    // user balance
    public static async Task GetUserBalanceAsync(DeepSeekClient client)
    {
        WriteExampleHeader("User Balance");
        var balance = client.GetBalanceClient();
        var response = await balance.GetBalanceAsync();
        foreach (var userBalance in response.Value.BalanceInfos)
        {
            Console.WriteLine($"Currency: {userBalance.Currency}, Total Balance: {userBalance.TotalBalance}, Granted Balance: {userBalance.GrantedBalance}, Topped Up Balance: {userBalance.ToppedUpBalance}");
        }
    }

    public static async Task GetModels(DeepSeekClient client)
    {
        WriteExampleHeader("Model List");
        var modelsClient = client.GetModelsClient();
        var response = await modelsClient.GetModelsAsync();
        foreach (var model in response.Value.Data) {
            Console.WriteLine($"Model: {model.Id}, OwnedBy: {model.OwnedBy},Object: {model.Object}");
        }
    }

    public static async Task RunStreamingAgentRoundAsync(
        AIAgent agent,
        AgentSession session,
        ChatClientAgentRunOptions options,
        int round,
        string userInput)
    {
        WriteRoundHeader(round);
        Console.WriteLine($"User: {userInput}");

        var sawThinkingPrefix = false;
        var sawAnswerPrefix = false;
        var sawToolResult = false;

        await foreach (var update in agent.RunStreamingAsync(userInput, session, options, CancellationToken.None))
        {
            var chatUpdate = update.AsChatResponseUpdate();
            if (chatUpdate is null)
            {
                continue;
            }

            if (chatUpdate.AdditionalProperties?.TryGetValue("is_reasoning", out var isReasoningValue) == true
                && isReasoningValue is true)
            {
                if (!sawThinkingPrefix)
                {
                    Console.Write("[Thinking]: ");
                    sawThinkingPrefix = true;
                }

                Console.Write(GetReasoningText(chatUpdate));
                continue;
            }

            foreach (var functionCall in chatUpdate.Contents.OfType<FunctionCallContent>())
            {
                Console.WriteLine();
                Console.WriteLine($"[Tool Call]: {functionCall.Name}({JsonSerializer.Serialize(functionCall.Arguments)})");
            }

            foreach (var functionResult in chatUpdate.Contents.OfType<FunctionResultContent>())
            {
                if (!sawToolResult)
                {
                    sawToolResult = true;
                }

                Console.WriteLine();
                Console.WriteLine($"[Tool Result]: {functionResult.Result}");
            }

            if (!string.IsNullOrWhiteSpace(chatUpdate.Text))
            {
                if (!sawAnswerPrefix)
                {
                    if (sawThinkingPrefix)
                    {
                        Console.WriteLine();
                    }

                    Console.Write("[Answer]: ");
                    sawAnswerPrefix = true;
                }

                Console.Write(chatUpdate.Text);
            }
        }

        Console.WriteLine();
    }

    // 强类型 JSON 示例：通过 IChatClient 直接调用
    public static async Task RunStructuredJsonWithChatClientAsync(IChatClient chatClient)
    {
        WriteExampleHeader("Structured JSON via IChatClient");

        var response = await chatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, BookDescription)],
            BookInfoSchemaOptions);

        var rawJson = response.Messages.FirstOrDefault()?.Text ?? response.Text ?? string.Empty;
        Console.Write("Raw JSON response: ");
        PrintBookInfo(rawJson);
    }

    // 强类型 JSON 示例：通过 IChatClient 流式调用
    public static async Task RunStructuredJsonWithChatClientStreamingAsync(IChatClient chatClient)
    {
        WriteExampleHeader("Structured JSON via IChatClient (Streaming)");

        var jsonBuilder = new StringBuilder();
        Console.Write("[Streaming JSON]: ");
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, BookDescription)], BookInfoSchemaOptions))
        {
            if (update.Text is { Length: > 0 } text)
            {
                jsonBuilder.Append(text);
                Console.Write(text);
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        PrintBookInfo(jsonBuilder.ToString());
    }

    // 强类型 JSON 示例：通过 AIAgent 流式调用
    public static async Task RunStructuredJsonWithAgentAsync(
        AIAgent agent,
        AgentSession session,
        int round,
        string userInput)
    {
        WriteRoundHeader(round);
        Console.WriteLine($"User: {userInput}");

        var options = new ChatClientAgentRunOptions
        {
            ChatOptions = BookInfoSchemaOptions
        };
        var jsonBuilder = new StringBuilder();
        Console.Write("[Response]: ");
        await foreach (var update in agent.RunStreamingAsync(userInput, session, options, CancellationToken.None))
        {
            var chatUpdate = update.AsChatResponseUpdate();
            if (chatUpdate?.Text is { Length: > 0 } text)
            {
                Console.Write(text);
                jsonBuilder.Append(text);
            }
        }
        Console.WriteLine();
        Console.WriteLine();
        PrintBookInfo(jsonBuilder.ToString());
    }

    // 强类型 JSON 示例：通过 AIAgent 非流式调用
    public static async Task RunStructuredJsonWithAgentNonStreamingAsync(
        AIAgent agent,
        AgentSession session,
        int round,
        string userInput)
    {
        WriteRoundHeader(round);
        Console.WriteLine($"User: {userInput}");

        var options = new ChatClientAgentRunOptions
        {
            ChatOptions = BookInfoSchemaOptions
        };

        var response = await agent.RunAsync(userInput, session, options, CancellationToken.None);

        Console.Write("Raw JSON response: ");
        PrintBookInfo(response.Text ?? string.Empty);
    }

    // Swagger → Agent Tools 示例
    public static async Task RunSwaggerToolsAsync(
        IChatClient chatClient,
        string swaggerJsonUrl,
        string userQuery)
    {
        WriteExampleHeader("Swagger to Agent Tools");

        Console.WriteLine($"Loading Swagger from: {swaggerJsonUrl}");

        using var httpClient = new HttpClient();
        var swaggerJson = await httpClient.GetStringAsync(swaggerJsonUrl);

        Console.WriteLine($"Swagger loaded ({swaggerJson.Length} chars)");
        Console.WriteLine();

        // Parse and show endpoints
        var (endpoints, serverUrl) = SwaggerParser.Parse(swaggerJson);
        Console.WriteLine($"Discovered {endpoints.Count} endpoints, server: {serverUrl}");
        foreach (var ep in endpoints)
        {
            Console.WriteLine($"  [{ep.HttpMethod}] {ep.PathTemplate} -> {ep.OperationId}");
        }
        Console.WriteLine();

        // Create tools
        var (tools, _) = SwaggerToolFactory.CreateTools(swaggerJson, httpClient);
        Console.WriteLine($"Created {tools.Count} tools");
        Console.WriteLine();

        // Create agent with swagger tools
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "SwaggerAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You have access to REST API tools converted from Swagger. Use them to answer user questions. Call the appropriate tool(s) to gather data, then provide a clear answer based on the results.",
                Tools = tools,
                AllowMultipleToolCalls = true,
            },
        });

        var session = await agent.CreateSessionAsync(CancellationToken.None);

        var options = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.Stream] = true,
                },
            },
        };

        await RunStreamingAgentRoundAsync(agent, session, options, 1, userQuery);
    }

    private static void WriteExampleHeader(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }

    private static void WriteRoundHeader(int round)
    {
        Console.WriteLine();
        Console.WriteLine($"Round {round}");
    }

    private static string GetReasoningText(ChatResponseUpdate chatUpdate)
    {
        var reasoningText = string.Concat(
            chatUpdate.Contents
                .OfType<TextReasoningContent>()
                .Select(static content => content.Text));

        return !string.IsNullOrWhiteSpace(reasoningText)
            ? reasoningText
            : chatUpdate.Text ?? string.Empty;
    }

    private sealed class BookInfo
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;
        [JsonPropertyName("publicationYear")]
        public int? PublicationYear { get; set; }
        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;
    }

    private const string BookDescription = @"我最近读了一本很好的书，叫《三体》，是刘慈欣写的科幻小说，2008年出版。
故事讲述地球人类文明和三体文明之间的信息交流、生死搏杀及两个文明在宇宙中的兴衰历程。
这本书获得了雨果奖，是亚洲首次获得该奖项的作品。";

    private static ChatOptions BookInfoSchemaOptions => new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema<BookInfo>(
            schemaName: "book_info",
            schemaDescription: "Extracted book information from user description.")
    };

    private static void PrintBookInfo(string rawJson)
    {
        Console.WriteLine(rawJson);
        try
        {
            var book = JsonSerializer.Deserialize<BookInfo>(rawJson);
            if (book is not null)
            {
                Console.WriteLine();
                Console.WriteLine("Deserialized BookInfo:");
                Console.WriteLine($"  Title: {book.Title}");
                Console.WriteLine($"  Author: {book.Author}");
                Console.WriteLine($"  PublicationYear: {book.PublicationYear}");
                Console.WriteLine($"  Genre: {book.Genre}");
                Console.WriteLine($"  Summary: {book.Summary}");
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Deserialization failed: {ex.Message}");
        }
    }
}
#pragma warning restore MAAI001
