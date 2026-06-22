using AGUIDojoServer.AgenticUI;
using AGUIDojoServer.BackendToolRendering;
using AGUIDojoServer.PredictiveStateUpdates;
using AGUIDojoServer.SharedState;
using DeepSeek;
using DeepSeek.Agents.AI;
using DeepSeek.Chat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Text.Json;

namespace AGUIDojoServer;

internal static class ChatClientAgentFactory
{
    private static DeepSeekClient? s_openAIClient;
    private static string? s_modelName;

    public static void Initialize(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        s_modelName = configuration["DeepSeek:Model"] ?? "deepseek-v4-flash";
        var apiKey = configuration["apiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Configuration value 'apiKey' is required.");
        }

        var loggingSection = configuration.GetSection("DeepSeek:Logging");
        var enablePipelineLogging = loggingSection.GetValue<bool>("Enabled");
        var enableDistributedTracing = loggingSection.GetValue<bool>("EnableDistributedTracing");
        var messageContentSizeLimit = loggingSection.GetValue<int?>("MessageContentSizeLimit") ?? 4096;

        var clientOptions = new DeepSeekClientOptions
        {
            EnableDistributedTracing = enableDistributedTracing,
            AllowMessageContentLogging = enablePipelineLogging,
            ClientLoggingOptions = new ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = enablePipelineLogging,
                EnableMessageLogging = enablePipelineLogging,
                EnableMessageContentLogging = enablePipelineLogging,
                MessageContentSizeLimit = messageContentSizeLimit,
            },
        };

        s_openAIClient = new DeepSeekClient(apiKey, clientOptions);
    }

    public static ChatClientAgent CreateAgenticChat()
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        return chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "AgenticChat",
            Description = "A simple chat agent using DeepSeek",
        });
    }

    public static ChatClientAgent CreateBackendToolRendering()
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        return chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "BackendToolRenderer",
            Description = "An agent that can render backend tools using OpenAI",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(
                        GetWeather,
                        name: "get_weather",
                        description: "Get the weather for a given location.",
                        AGUIDojoServerSerializerContext.Default.Options),
                ],
            },
        });
    }

    public static ChatClientAgent CreateHumanInTheLoop()
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        return chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HumanInTheLoopAgent",
            Description = "An agent that involves human feedback in its decision-making process using OpenAI",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a planning assistant that must explicitly ask for human confirmation before proceeding.

                    When the user asks for a plan:
                    - Call the `generate_task_steps` tool with a `steps` array.
                    - Every step must be an object with `description` and `status`.
                    - Use `enabled` for steps that should be selected by default.
                    - Only use `executing` if a step is actively being performed.
                    - Do not send a normal assistant message before the tool call.
                    - Do not call `generate_task_steps` twice in a row.

                    After the tool returns:
                    - If the user rejects the plan, ask what should change and wait for more guidance.
                    - If the user accepts the plan, summarize only the accepted steps and continue from that approved plan.
                    - Keep the response concise and grounded in the approved steps.
                    """
            },
        });
    }

    public static ChatClientAgent CreateToolBasedGenerativeUI()
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        return chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "ToolBasedGenerativeUIAgent",
            Description = "An agent that uses tools to generate user interfaces using OpenAI",
        });
    }

    public static AIAgent CreateAgenticUI(JsonSerializerOptions options)
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);
        var baseAgent = chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "AgenticUIAgent",
            Description = "An agent that generates agentic user interfaces using OpenAI",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    Build plans in the same event shape as the official demo.
                    IMPORTANT:
                    - First call the `create_plan` tool to set the initial state of the steps.
                    - Then call the `update_plan_step` tool sequentially to complete each step.
                    - Do NOT output a markdown table.
                    - Do NOT output a numbered list or bullet list of steps.
                    - Do NOT repeat, restate, or summarize the plan body in assistant text.
                    - Do NOT confirm intermediate progress in assistant text.
                    - Do NOT ask the user for additional information or next steps
                    - Do NOT leave a plan hanging, always complete the plan via `update_plan_step` if one is ongoing.
                    - Continue calling update_plan_step until all steps are marked as completed.
                    - After all steps are completed, output exactly one short closing sentence only.
                    - The closing sentence should be similar to: "The plan has been fully completed."

                    Only one plan can be active at a time, so do not call the `create_plan` tool
                    again until all the steps in current plan are completed.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(
                        AgenticPlanningTools.CreatePlan,
                        name: "create_plan",
                        description: "Create a plan with multiple steps.",
                        AGUIDojoServerSerializerContext.Default.Options),
                    AIFunctionFactory.Create(
                        AgenticPlanningTools.UpdatePlanStepAsync,
                        name: "update_plan_step",
                        description: "Update a step in the plan with new description or status.",
                        AGUIDojoServerSerializerContext.Default.Options)
                ],
                AllowMultipleToolCalls = false,
            }
        });

        return new AgenticUIAgent(baseAgent, options);
    }

    public static AIAgent CreateSharedState(JsonSerializerOptions options)
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        var baseAgent = chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "SharedStateAgent",
            Description = "An agent that demonstrates shared state patterns using OpenAI",
        });

        return new SharedStateAgent(baseAgent, options);
    }

    public static AIAgent CreatePredictiveStateUpdates(JsonSerializerOptions options)
    {
        var chatClient = s_openAIClient!.GetChatClient(s_modelName!);

        var baseAgent = chatClient.AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "PredictiveStateUpdatesAgent",
            Description = "An agent that demonstrates predictive state updates using OpenAI",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a document editor assistant. When asked to write or edit content:
                    
                    IMPORTANT:
                    - Use the `write_document` tool with the full document text in Markdown format
                    - Format the document extensively so it's easy to read
                    - You can use all kinds of markdown (headings, lists, bold, etc.)
                    - However, do NOT use italic or strike-through formatting
                    - You MUST write the full document, even when changing only a few words
                    - When making edits to the document, try to make them minimal - do not change every word
                    - Keep stories SHORT!
                    - After you are done writing the document you MUST call a confirm_changes tool after you call write_document
                    
                    After the user confirms the changes, provide a brief summary of what you wrote.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(
                        WriteDocument,
                        name: "write_document",
                        description: "Write a document. Use markdown formatting to format the document.",
                        AGUIDojoServerSerializerContext.Default.Options)
                ]
            }
        });

        return new PredictiveStateUpdatesAgent(baseAgent, options);
    }

    [Description("Get the weather for a given location.")]
    private static WeatherInfo GetWeather([Description("The location to get the weather for.")] string location) => new()
    {
        Temperature = 20,
        Conditions = "sunny",
        Humidity = 50,
        WindSpeed = 10,
        FeelsLike = 25
    };

    [Description("Write a document in markdown format.")]
    private static string WriteDocument([Description("The document content to write.")] string document)
    {
        return "Document written successfully";
    }
}
