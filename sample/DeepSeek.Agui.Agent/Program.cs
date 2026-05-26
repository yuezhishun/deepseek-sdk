using DeepSeek;
using Microsoft.Agents.AI.DeepSeek;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Serilog;
using System.ClientModel.Primitives;

var builder = WebApplication.CreateBuilder(args);
const string AguiCorsPolicy = "AguiDevClient";

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddFilter("System.ClientModel", LogLevel.Trace);

    // 配置 Serilog，将日志写入文件
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose() // 保留 SDK pipeline trace 日志
        .WriteTo.File("D:/Download/agui-agent-llm-.log", // 文件路径，支持 rolling
            rollingInterval: RollingInterval.Day, // 每天生成一个新文件
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
    builder.AddSerilog(); // 将 Serilog 挂接到 ILoggerFactory
});
builder.Services.AddAGUI();
builder.Services.AddCors(options =>
{
    options.AddPolicy(AguiCorsPolicy, policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors(AguiCorsPolicy);

app.MapGet("/agent/health", () => Results.Ok(new
{
    status = "ok",
    utc = DateTimeOffset.UtcNow,
    endpoints = new[] { "/agui" }
}));
var apiKey = app.Configuration["apiKey"];
var model = app.Configuration["DeepSeek:Model"] ?? "deepseek-v4-flash";
var loggingSection = app.Configuration.GetSection("DeepSeek:Logging");
var enablePipelineLogging = loggingSection.GetValue<bool>("Enabled");
var enableDistributedTracing = loggingSection.GetValue<bool>("EnableDistributedTracing");
var messageContentSizeLimit = loggingSection.GetValue<int?>("MessageContentSizeLimit") ?? 4096;
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

var chatClient = new DeepSeekClient(apiKey, clientOptions).GetChatClient(model); 
var Agent = chatClient.AsAIAgent(
    name: "DeepSeekAgUiHostedAgent",
    instructions: "You are a concise AG-UI demo assistant. Use tools when the user asks about listings, pricing, or scheduling.");
app.MapAGUI("/agui", Agent);


app.Run();
