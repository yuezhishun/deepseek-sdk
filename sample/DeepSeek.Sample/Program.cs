using DeepSeek;
using DeepSeek.Agents.AI;
using DeepSeek.Chat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sample;
using Serilog;
using System.ClientModel.Primitives;

string FlashModel = "deepseek-v4-flash";


var builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>();

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddFilter("System.ClientModel", LogLevel.Trace);


    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose() 
        .WriteTo.File("D:/Download/agent-llm-.log", 
            rollingInterval: RollingInterval.Minute, 
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    builder.AddSerilog(); 
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
        AllowMultipleToolCalls = true
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
//var round1Question = "杭州今天天气怎么样？北京时间现在几点？";
//await Examples.RunStreamingAgentRoundAsync(agent, session, options, 1, round1Question);

//var round2Question = "我明天下午出门需要戴墨镜吗？";
//await Examples.RunStreamingAgentRoundAsync(agent, session, options, 2, round2Question);


await Examples.GetModels(sdk);
await Examples.GetUserBalanceAsync(sdk);

// 强类型 JSON 示例
var jsonAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "StructuredJsonAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "请根据用户描述提取结构化书籍信息，仅输出 JSON。"
    },
});
var jsonSession = await jsonAgent.CreateSessionAsync(CancellationToken.None);

const string bookDescription = @"我最近读了一本很好的书，叫《三体》，是刘慈欣写的科幻小说，2008年出版。
故事讲述地球人类文明和三体文明之间的信息交流、生死搏杀及两个文明在宇宙中的兴衰历程。
这本书获得了雨果奖，是亚洲首次获得该奖项的作品。";

await Examples.RunStructuredJsonWithChatClientAsync(chatClient);
await Examples.RunStructuredJsonWithChatClientStreamingAsync(chatClient);
await Examples.RunStructuredJsonWithAgentAsync(jsonAgent, jsonSession, 1, bookDescription);
await Examples.RunStructuredJsonWithAgentNonStreamingAsync(jsonAgent, jsonSession, 2, bookDescription);

// Swagger → Agent Tools 示例
// 取消注释并替换为实际的 Swagger JSON URL 即可运行
// const string swaggerUrl = "https://petstore.swagger.io/v2/swagger.json";
// const string swaggerQuery = "What pets are available?";
// await Examples.RunSwaggerToolsAsync(chatClient, swaggerUrl, swaggerQuery);

Console.WriteLine("done");
Console.ReadKey();
