using TestSupport;
using Xunit;

namespace DeepSeek.IntegrationTests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        var key = TestApiKeyProvider.GetApiKey();
        if ((string.IsNullOrWhiteSpace(key) || key == "test-key")
            && Environment.GetEnvironmentVariable("DEEPSEEK_REQUIRE_LIVE_TESTS") != "true")
        {
            Skip = "未配置 DEEPSEEK_API_KEY、deepseek-api-key 或本地 appsettings.json API Key。";
        }
    }
}
