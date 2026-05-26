using DeepSeek;
using DeepSeek.Billing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        WriteExampleHeader("User Balance");
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
}
#pragma warning restore MAAI001
