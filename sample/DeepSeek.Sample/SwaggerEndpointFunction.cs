using Microsoft.Extensions.AI;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Sample;

internal sealed class SwaggerEndpointFunction : AIFunction
{
    private readonly string _httpMethod;
    private readonly string _pathTemplate;
    private readonly string _serverUrl;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, string> _paramLocations;
    private readonly string? _requestBodyContentType;
    private readonly JsonElement? _requestBodySchema;

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }
    public override JsonElement? ReturnJsonSchema { get; }
    public override MethodInfo? UnderlyingMethod => null;
    public override JsonSerializerOptions? JsonSerializerOptions => null;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => null;

    public SwaggerEndpointFunction(
        string name,
        string description,
        JsonElement jsonSchema,
        JsonElement? returnJsonSchema,
        string httpMethod,
        string pathTemplate,
        string serverUrl,
        HttpClient httpClient,
        IReadOnlyDictionary<string, string> paramLocations,
        string? requestBodyContentType,
        JsonElement? requestBodySchema)
    {
        Name = name;
        Description = description;
        JsonSchema = jsonSchema;
        ReturnJsonSchema = returnJsonSchema;
        _httpMethod = httpMethod;
        _pathTemplate = pathTemplate;
        _serverUrl = serverUrl.TrimEnd('/');
        _httpClient = httpClient;
        _paramLocations = paramLocations;
        _requestBodyContentType = requestBodyContentType;
        _requestBodySchema = requestBodySchema;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        // 1. Build URL: replace path params, add query params
        var url = _serverUrl + _pathTemplate;
        var queryParams = new List<string>();

        foreach (var arg in arguments)
        {
            var argValue = SerializeArg(arg.Value);
            if (argValue is null) continue;

            if (_paramLocations.TryGetValue(arg.Key, out var location))
            {
                switch (location)
                {
                    case "path":
                        url = url.Replace($"{{{arg.Key}}}", Uri.EscapeDataString(argValue));
                        break;
                    case "query":
                        queryParams.Add($"{Uri.EscapeDataString(arg.Key)}={Uri.EscapeDataString(argValue)}");
                        break;
                    case "header":
                        break;
                }
            }
            else
            {
                queryParams.Add($"{Uri.EscapeDataString(arg.Key)}={Uri.EscapeDataString(argValue)}");
            }
        }

        if (queryParams.Count > 0)
            url += "?" + string.Join("&", queryParams);

        // 2. Build request
        var request = new HttpRequestMessage(new HttpMethod(_httpMethod), url);

        // 3. Add header params
        foreach (var arg in arguments)
        {
            if (_paramLocations.TryGetValue(arg.Key, out var location) && location == "header")
            {
                var argValue = SerializeArg(arg.Value);
                if (argValue is not null)
                    request.Headers.TryAddWithoutValidation(arg.Key, argValue);
            }
        }

        // 4. Set request body
        if (_requestBodyContentType is not null && arguments.Count > 0)
        {
            var bodyParams = new Dictionary<string, object?>();
            foreach (var arg in arguments)
            {
                if (!_paramLocations.TryGetValue(arg.Key, out var loc) ||
                    (loc != "path" && loc != "query" && loc != "header"))
                {
                    bodyParams[arg.Key] = arg.Value;
                }
            }

            if (bodyParams.Count > 0)
            {
                var json = JsonSerializer.Serialize(bodyParams);
                request.Content = new StringContent(json, Encoding.UTF8, _requestBodyContentType);
            }
        }

        // 5. Send request
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return $"HTTP {response.StatusCode}: {responseBody}";
            }

            try
            {
                using var jsonDoc = JsonDocument.Parse(responseBody);
                return jsonDoc.RootElement.Clone();
            }
            catch
            {
                return responseBody;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string? SerializeArg(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is JsonElement je) return je.GetRawText();
        return JsonSerializer.Serialize(value);
    }
}
