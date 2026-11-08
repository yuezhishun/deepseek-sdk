using TestSupport;
using Xunit;

namespace DeepSeek.Tests;

public sealed class TestApiKeyProviderTests
{
    [Fact]
    public void GetApiKey_CiEnvironmentConfigured_DoesNotReadLocalCredentialFile()
    {
        var key = TestApiKeyProvider.GetApiKey(
            name => name == "DEEPSEEK_API_KEY" ? "  ci-test-credential  " : "alias-test-credential",
            () => throw new InvalidOperationException("环境变量应优先于本地文件。"));

        Assert.Equal("ci-test-credential", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void GetApiKey_CanonicalEnvironmentAbsent_AcceptsHyphenatedEnvironmentAlias(string? canonical)
    {
        var key = TestApiKeyProvider.GetApiKey(
            name => name == "DEEPSEEK_API_KEY" ? canonical : "alias-test-credential",
            () => throw new InvalidOperationException("别名已提供凭据，不应读取文件。"));

        Assert.Equal("alias-test-credential", key);
    }

    [Fact]
    public void GetApiKey_NoEnvironmentCredential_PreservesLocalDevelopmentFileFallback()
    {
        Assert.Equal("file-test-credential", TestApiKeyProvider.GetApiKey(_ => null, () => "file-test-credential"));
        Assert.Null(TestApiKeyProvider.GetApiKey(_ => null, () => null));
    }

    [Fact]
    public void GetApiKey_PlaceholderCiCredential_DoesNotHideMisconfigurationWithLocalFile()
    {
        var key = TestApiKeyProvider.GetApiKey(_ => "test-key", () => "file-test-credential");

        Assert.Equal("test-key", key);
    }
}
