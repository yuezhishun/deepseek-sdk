using System.Text.Json;

namespace TestSupport;

internal static class TestApiKeyProvider
{
    public static string? GetApiKey()
        => GetApiKey(Environment.GetEnvironmentVariable, GetFileApiKey);

    internal static string? GetApiKey(Func<string, string?> readEnvironment, Func<string?> readFile)
    {
        var key = readEnvironment("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            key = readEnvironment("deepseek-api-key");
        }

        return string.IsNullOrWhiteSpace(key) ? readFile() : key.Trim();
    }

    private static string? GetFileApiKey()
    {
        var appSettingsPath = FindSampleAppSettingsPath();
        if (appSettingsPath is null || !File.Exists(appSettingsPath))
        {
            return null;
        }

        using var stream = File.OpenRead(appSettingsPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("apiKey", out var apiKeyElement))
        {
            return null;
        }

        var apiKey = apiKeyElement.GetString();
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public static string GetApiKeyOrFallback(string fallback = "test-key")
    {
        return GetApiKey() ?? fallback;
    }

    private static string? FindSampleAppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
