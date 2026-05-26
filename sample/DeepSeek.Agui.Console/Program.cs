using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

var serverUrl = args.FirstOrDefault(arg => arg.StartsWith("--server=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
    ?? "http://localhost:5099/agui";

Console.WriteLine($"Connecting to AG-UI server at: {serverUrl}");

using HttpClient httpClient = new()
{
    Timeout = Timeout.InfiniteTimeSpan,
};

AGUIChatClient chatClient = new(httpClient, serverUrl);

AIAgent agent = chatClient.AsAIAgent(
    name: "deepseek-agui-console",
    description: "DeepSeek AG-UI console client");

AgentSession session = await agent.CreateSessionAsync();
List<ChatMessage> messages =
[
    new(ChatRole.System, "You are a concise AG-UI demo assistant.")
];

try
{
    while (true)
    {
        Console.Write("\nUser (:q or quit to exit): ");
        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("Request cannot be empty.");
            continue;
        }

        if (input is ":q" or "quit")
        {
            break;
        }

        messages.Add(new ChatMessage(ChatRole.User, input));

        bool isFirstUpdate = true;
        string? sessionId = null;

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            if (isFirstUpdate)
            {
                sessionId = chatUpdate.ConversationId;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[Run Started - Session: {chatUpdate.ConversationId}, Run: {chatUpdate.ResponseId}]");
                Console.ResetColor();
                isFirstUpdate = false;
            }

            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write(textContent.Text);
                    Console.ResetColor();
                }
                else if (content is TextReasoningContent reasoningContent)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(reasoningContent.Text);
                    Console.ResetColor();
                }
                else if (content is ErrorContent errorContent)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[Error: {errorContent.Message}]");
                    Console.ResetColor();
                }
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[Run Finished - Session: {sessionId}]");
        Console.ResetColor();
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nAn error occurred: {ex.Message}");
    Console.ResetColor();
}
