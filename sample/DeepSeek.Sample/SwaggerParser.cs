using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sample;

public sealed record SwaggerEndpoint
{
    public required string OperationId { get; init; }
    public required string HttpMethod { get; init; }
    public required string PathTemplate { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonElement? ParametersSchema { get; init; }
    public JsonElement? ResponseSchema { get; init; }
    public Dictionary<string, string> ParamLocations { get; init; } = [];
    public string? RequestBodyContentType { get; init; }
    public JsonElement? RequestBodySchema { get; init; }
}

public static class SwaggerParser
{
    public static (List<SwaggerEndpoint> Endpoints, string ServerUrl) Parse(string openApiJson)
    {
        using var doc = JsonDocument.Parse(openApiJson);
        var root = doc.RootElement;

        // Detect OpenAPI version
        var isSwagger2 = root.TryGetProperty("swagger", out var swVer) &&
                         swVer.GetString()?.StartsWith("2") == true;

        // Server URL
        string serverUrl;
        if (isSwagger2)
        {
            var scheme = root.TryGetProperty("schemes", out var schemes) && schemes.GetArrayLength() > 0
                ? schemes[0].GetString() ?? "https"
                : "https";
            var host = root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
            var basePath = root.TryGetProperty("basePath", out var bp) ? bp.GetString() ?? "" : "";
            serverUrl = $"{scheme}://{host}{basePath}";
        }
        else
        {
            serverUrl = root.TryGetProperty("servers", out var servers) && servers.GetArrayLength() > 0
                ? servers[0].GetProperty("url").GetString() ?? ""
                : "";
        }

        // Build component/definition schemas for  resolution
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (isSwagger2)
        {
            if (root.TryGetProperty("definitions", out var defs))
            {
                foreach (var def in defs.EnumerateObject())
                    schemas[def.Name] = def.Value.Clone();
            }
        }
        else
        {
            if (root.TryGetProperty("components", out var comps) &&
                comps.TryGetProperty("schemas", out var compSchemas))
            {
                foreach (var s in compSchemas.EnumerateObject())
                    schemas[s.Name] = s.Value.Clone();
            }
        }

        var endpoints = new List<SwaggerEndpoint>();

        if (!root.TryGetProperty("paths", out var paths))
            return (endpoints, serverUrl);

        foreach (var pathEntry in paths.EnumerateObject())
        {
            var pathTemplate = pathEntry.Name;

            foreach (var methodEntry in pathEntry.Value.EnumerateObject())
            {
                var httpMethod = methodEntry.Name.ToUpperInvariant();
                if (httpMethod is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH"))
                    continue;

                var operation = methodEntry.Value;

                var operationId = operation.TryGetProperty("operationId", out var opId)
                    ? opId.GetString() ?? FallbackOperationId(httpMethod, pathTemplate)
                    : FallbackOperationId(httpMethod, pathTemplate);

                var summary = operation.TryGetProperty("summary", out var summ) ? summ.GetString() ?? "" : "";
                var description = operation.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";

                // Parse parameters
                var paramLocations = new Dictionary<string, string>(StringComparer.Ordinal);
                var schemaProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var requiredParams = new List<string>();

                if (operation.TryGetProperty("parameters", out var parameters))
                {
                    foreach (var param in parameters.EnumerateArray())
                    {
                        var paramName = param.GetProperty("name").GetString() ?? "";
                        var paramIn = param.TryGetProperty("in", out var pIn) ? pIn.GetString() ?? "" : "";

                        paramLocations[paramName] = paramIn;

                        if (param.TryGetProperty("required", out var req) && req.GetBoolean())
                            requiredParams.Add(paramName);

                        // Resolve schema (inline or )
                        JsonElement? paramSchema = null;
                        if (param.TryGetProperty("schema", out var schema))
                            paramSchema = ResolveSchema(schema, schemas);
                        else if (param.TryGetProperty("type", out var type))
                            paramSchema = JsonDocument.Parse($"{{\"type\":\"{type.GetString()}\"}}").RootElement.Clone();

                        if (paramSchema is not null)
                            schemaProps[paramName] = paramSchema.Value;
                    }
                }

                // Parse requestBody (OpenAPI 3.0) or body parameter (Swagger 2.0)
                JsonElement? requestBodySchema = null;
                string? requestBodyContentType = null;

                if (!isSwagger2 && operation.TryGetProperty("requestBody", out var requestBody))
                {
                    if (requestBody.TryGetProperty("content", out var content))
                    {
                        foreach (var ct in new[] { "application/json", "application/x-www-form-urlencoded", "multipart/form-data" })
                        {
                            if (content.TryGetProperty(ct, out var ctContent))
                            {
                                requestBodyContentType = ct;
                                if (ctContent.TryGetProperty("schema", out var rbSchema))
                                    requestBodySchema = ResolveSchema(rbSchema, schemas);
                                break;
                            }
                        }
                    }
                }
                else if (isSwagger2)
                {
                    // Swagger 2.0: body parameter
                    foreach (var param in (operation.TryGetProperty("parameters", out var sw2Params) ? sw2Params : default).EnumerateArray())
                    {
                        if (param.TryGetProperty("in", out var pIn) && pIn.GetString() == "body")
                        {
                            requestBodyContentType = "application/json";
                            if (param.TryGetProperty("schema", out var bodySchema))
                                requestBodySchema = ResolveSchema(bodySchema, schemas);
                            break;
                        }
                    }
                }

                // Merge request body schema into parameters schema
                if (requestBodySchema is not null)
                {
                    // If request body is an object, merge its properties into the top-level schema
                    if (requestBodySchema.Value.TryGetProperty("type", out var rbType) &&
                        rbType.GetString() == "object" &&
                        requestBodySchema.Value.TryGetProperty("properties", out var rbProps))
                    {
                        foreach (var prop in rbProps.EnumerateObject())
                        {
                            if (!schemaProps.ContainsKey(prop.Name))
                                schemaProps[prop.Name] = prop.Value.Clone();
                        }
                        if (requestBodySchema.Value.TryGetProperty("required", out var rbRequired))
                        {
                            foreach (var req in rbRequired.EnumerateArray())
                            {
                                var reqName = req.GetString();
                                if (reqName is not null && !requiredParams.Contains(reqName))
                                    requiredParams.Add(reqName);
                            }
                        }
                    }
                    else
                    {
                        // Non-object body, put under "body" key
                        schemaProps["body"] = requestBodySchema.Value;
                    }
                }

                // Build combined parameters schema
                JsonElement? parametersSchema = null;
                if (schemaProps.Count > 0)
                {
                    var propsObj = new JsonObject();
                    foreach (var (name, schema) in schemaProps)
                        propsObj[name] = JsonNode.Parse(schema.GetRawText());

                    var schemaObj = new JsonObject { ["type"] = "object", ["properties"] = propsObj };
                    if (requiredParams.Count > 0)
                    {
                        var reqArray = new JsonArray();
                        foreach (var rp in requiredParams)
                            reqArray.Add(rp);
                        schemaObj["required"] = reqArray;
                    }
                    parametersSchema = JsonDocument.Parse(schemaObj.ToJsonString()).RootElement.Clone();
                }

                // Parse response schema
                JsonElement? responseSchema = null;
                if (operation.TryGetProperty("responses", out var responses))
                {
                    foreach (var sc in new[] { "200", "201", "default" })
                    {
                        if (responses.TryGetProperty(sc, out var response) &&
                            response.TryGetProperty("content", out var respContent))
                        {
                            foreach (var ct in new[] { "application/json", "application/xml" })
                            {
                                if (respContent.TryGetProperty(ct, out var respCt) &&
                                    respCt.TryGetProperty("schema", out var respSchemaElem))
                                {
                                    responseSchema = ResolveSchema(respSchemaElem, schemas);
                                    break;
                                }
                            }
                            if (responseSchema is not null) break;
                        }
                    }
                }

                endpoints.Add(new SwaggerEndpoint
                {
                    OperationId = operationId,
                    HttpMethod = httpMethod,
                    PathTemplate = pathTemplate,
                    Summary = summary,
                    Description = description,
                    ParametersSchema = parametersSchema,
                    ResponseSchema = responseSchema,
                    ParamLocations = paramLocations,
                    RequestBodyContentType = requestBodyContentType,
                    RequestBodySchema = requestBodySchema,
                });
            }
        }

        return (endpoints, serverUrl);
    }

    private static string FallbackOperationId(string method, string path)
    {
        var clean = path.Replace("/", "_").Replace("{", "").Replace("}", "");
        return $"{method.ToLowerInvariant()}{clean}";
    }

    private static JsonElement ResolveSchema(JsonElement schema, Dictionary<string, JsonElement> schemas)
    {
        if (schema.TryGetProperty("", out var refVal))
        {
            var refPath = refVal.GetString() ?? "";
            // Handle #/components/schemas/Name or #/definitions/Name
            var parts = refPath.Split('/');
            var name = parts[^1];
            if (schemas.TryGetValue(name, out var resolved))
                return ResolveSchema(resolved, schemas); // Recursive for nested 
        }
        return schema.Clone();
    }
}
