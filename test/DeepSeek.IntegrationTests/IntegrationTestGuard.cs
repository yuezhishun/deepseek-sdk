namespace DeepSeek.IntegrationTests;

internal static class IntegrationTestGuard
{
    public static bool RequireConfigured(DeepSeekIntegrationFixture fixture)
    {
        return fixture.IsConfigured;
    }
}
