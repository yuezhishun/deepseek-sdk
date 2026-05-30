using AGUIDojoServer;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Options;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

//builder.Services.AddHttpLogging(logging =>
//{
//    logging.LoggingFields = HttpLoggingFields.RequestPropertiesAndHeaders | HttpLoggingFields.RequestBody
//        | HttpLoggingFields.ResponsePropertiesAndHeaders | HttpLoggingFields.ResponseBody;
//    logging.RequestBodyLogLimit = int.MaxValue;
//    logging.ResponseBodyLogLimit = int.MaxValue;
//});
var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(LogLevel.Trace);
    logging.AddFilter("System.ClientModel", LogLevel.Trace);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.File(
            "logs/AGUIDojoServer-llm-.log",
            rollingInterval: RollingInterval.Hour,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
    logging.AddSerilog();
});

builder.Services.AddHttpClient().AddLogging();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Add(AGUIDojoServerSerializerContext.Default));
builder.Services.AddAGUI();

WebApplication app = builder.Build();

//app.UseHttpLogging();

// Initialize the factory
ChatClientAgentFactory.Initialize(app.Configuration, loggerFactory);

// Map the AG-UI agent endpoints for different scenarios
app.MapAGUI("/agentic_chat", ChatClientAgentFactory.CreateAgenticChat());

app.MapAGUI("/backend_tool_rendering", ChatClientAgentFactory.CreateBackendToolRendering());

app.MapAGUI("/human_in_the_loop", ChatClientAgentFactory.CreateHumanInTheLoop());

app.MapAGUI("/tool_based_generative_ui", ChatClientAgentFactory.CreateToolBasedGenerativeUI());

var jsonOptions = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
app.MapAGUI("/agentic_generative_ui", ChatClientAgentFactory.CreateAgenticUI(jsonOptions.Value.SerializerOptions));

app.MapAGUI("/shared_state", ChatClientAgentFactory.CreateSharedState(jsonOptions.Value.SerializerOptions));

app.MapAGUI("/predictive_state_updates", ChatClientAgentFactory.CreatePredictiveStateUpdates(jsonOptions.Value.SerializerOptions));

app.MapAGUI("/custom_streaming", new CustomStreamingAgent(jsonOptions.Value.SerializerOptions));

await app.RunAsync();

public partial class Program { }
