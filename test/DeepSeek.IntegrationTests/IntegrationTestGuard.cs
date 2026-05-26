namespace DeepSeek.IntegrationTests;

internal static class IntegrationTestGuard
{
    public static void RequireConfigured(DeepSeekIntegrationFixture fixture)
    {
        if (!fixture.IsConfigured)
        {
            throw new InvalidOperationException("Live tests require sample/appsettings.json apiKey.");
        }
    }
}
