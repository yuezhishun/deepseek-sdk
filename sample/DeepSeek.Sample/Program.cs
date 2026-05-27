using DeepSeek.Agents.AI;
using DeepSeek;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sample;
using Serilog;
using System.ClientModel.Primitives;

string FlashModel = "deepseek-v4-flash";

// 从appsettings.json读取秘钥
var builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>();
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddFilter("System.ClientModel", LogLevel.Trace);

    // 配置 Serilog，将日志写入文件
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose() // 保留 SDK pipeline trace 日志
        .WriteTo.File("D:/Download/agent-llm-.log", // 文件路径，支持 rolling
            rollingInterval: RollingInterval.Day, // 每天生成一个新文件
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
    //builder.AddConsole();
    builder.AddSerilog(); // 将 Serilog 挂接到 ILoggerFactory
});
var configuration = builder.Build();

var apiKey = configuration["apiKey"];
var loggingSection = configuration.GetSection("DeepSeek:Logging");
var enablePipelineLogging = loggingSection.GetValue<bool>("Enabled");
var enableDistributedTracing = loggingSection.GetValue<bool>("EnableDistributedTracing");
var messageContentSizeLimit = loggingSection.GetValue<int?>("MessageContentSizeLimit") ?? 4096;

if (apiKey == null)
{
    Console.WriteLine("apiKey is null");
    return;
}
var clientOptions = new DeepSeekClientOptions
{
    EnableDistributedTracing = enableDistributedTracing,
    ClientLoggingOptions = new ClientLoggingOptions
    {
        LoggerFactory = loggerFactory,
        EnableLogging = enablePipelineLogging,
        EnableMessageLogging = enablePipelineLogging,
        EnableMessageContentLogging = enablePipelineLogging,
        MessageContentSizeLimit = messageContentSizeLimit,
    },
};

var sdk = new DeepSeekClient(apiKey, clientOptions);
var chatClient = sdk.GetChatClient(FlashModel).AsIChatClient(new(){IncludeUsage = true}); 

var weatherTool = AIFunctionFactory.Create(
    TestTools.GetWeather,
    nameof(TestTools.GetWeather),
    "Get weather information by city and date.");
var timeTool = AIFunctionFactory.Create(
    TestTools.GetCurrentTimeByTimezone,
    nameof(TestTools.GetCurrentTimeByTimezone),
    "Get the current local time for a timezone id.");

var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "DeepSeekToolAgent",
    //RequirePerServiceCallChatHistoryPersistence = true,
    ChatOptions = new ChatOptions
    {
        Instructions = "Use tools whenever they can improve accuracy. Think first, call tools if needed, wait until you have all required tool results, then answer with those tool results.",
        Tools = [weatherTool, timeTool],
        AllowMultipleToolCalls = true,
    },
});
    //.AsBuilder().UseLogging(loggerFactory).Build();
var session = await agent.CreateSessionAsync(CancellationToken.None);
var options = new ChatClientAgentRunOptions
{
    ChatOptions = new ChatOptions
    {
        ToolMode = ChatToolMode.Auto,
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [DeepSeekChatOptionKeys.Stream] = true,
            [DeepSeekChatOptionKeys.IncludeUsage] = true,
        },
    },
};
Console.WriteLine();
var round1Question = "杭州今天天气怎么样？北京时间现在几点？";
await Examples.RunStreamingAgentRoundAsync(agent, session, options, 1, round1Question);

var round2Question = "我明天下午出门需要戴墨镜吗？";
await Examples.RunStreamingAgentRoundAsync(agent, session, options, 2, round2Question);


//await Examples.GetModels(sdk);
//await Examples.GetUserBalanceAsync(sdk);


Console.WriteLine("done");
Console.ReadKey();