using DeepSeek;
using DeepSeek.Agents.AI;
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

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose() 
        .WriteTo.File("D:/Download/agui-agent-llm-.log", 
            rollingInterval: RollingInterval.Day, 
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
    builder.AddSerilog(); 
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
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException("Configuration value 'apiKey' is required.");
}

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
    instructions: "You are a concise AG-UI demo assistant.");
app.MapAGUI("/agui", Agent);


app.Run();
