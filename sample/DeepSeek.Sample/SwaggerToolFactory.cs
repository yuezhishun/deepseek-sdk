using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Sample;

public static class SwaggerToolFactory
{
    public static (IList<AITool> Tools, string ServerUrl) CreateTools(
        string openApiJson,
        HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient();

        var (endpoints, serverUrl) = SwaggerParser.Parse(openApiJson);

        var tools = new List<AITool>(endpoints.Count);
        foreach (var ep in endpoints)
        {
            var tool = new SwaggerEndpointFunction(
                name: ep.OperationId,
                description: !string.IsNullOrWhiteSpace(ep.Description) ? ep.Description : ep.Summary,
                jsonSchema: ep.ParametersSchema ?? JsonElementFromObject(new { type = "object", properties = new { } }),
                returnJsonSchema: ep.ResponseSchema,
                httpMethod: ep.HttpMethod,
                pathTemplate: ep.PathTemplate,
                serverUrl: serverUrl,
                httpClient: httpClient,
                paramLocations: ep.ParamLocations,
                requestBodyContentType: ep.RequestBodyContentType,
                requestBodySchema: ep.RequestBodySchema);

            tools.Add(tool);
        }

        return (tools, serverUrl);
    }

    private static JsonElement JsonElementFromObject(object obj)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }
}
