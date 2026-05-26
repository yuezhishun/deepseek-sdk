using System.ClientModel;
using System.ClientModel.Primitives;

namespace DeepSeek;

internal static class DeepSeekClientUtilities
{
    public static readonly Uri DefaultEndpoint = new("https://api.deepseek.com");

    public static ClientPipeline CreatePipeline(DeepSeekClientOptions options, AuthenticationPolicy authenticationPolicy)
        => CreatePipelineCore(options.CloneAndFreeze(), authenticationPolicy);

    private static ClientPipeline CreatePipelineCore(DeepSeekClientOptions frozen, AuthenticationPolicy authenticationPolicy)
    {
        ArgumentGuard.ThrowIfNull(frozen, nameof(frozen));
        ArgumentGuard.ThrowIfNull(authenticationPolicy, nameof(authenticationPolicy));

        return ClientPipeline.Create(
            frozen,
            perCallPolicies:
            [
                CreateUserAgentPolicy(frozen),
                authenticationPolicy,
            ],
            perTryPolicies: [],
            beforeTransportPolicies:
            [
                new DeepSeekRequestHeadersPolicy(),
            ]);
    }

    public static AuthenticationPolicy CreateAuthenticationPolicy(ApiKeyCredential credential)
    {
        ArgumentGuard.ThrowIfNull(credential, nameof(credential));
        return ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(credential);
    }

    public static Uri CombinePath(Uri endpoint, string relativePath)
    {
        return new Uri(endpoint, relativePath);
    }

    private static PipelinePolicy CreateUserAgentPolicy(DeepSeekClientOptions options)
    {
        var applicationId = string.IsNullOrWhiteSpace(options.UserAgentApplicationId)
            ? "DeepSeek"
            : options.UserAgentApplicationId;
        return new UserAgentPolicy(typeof(DeepSeekClient).Assembly, applicationId);
    }

    private sealed class DeepSeekRequestHeadersPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ApplyHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ApplyHeaders(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private static void ApplyHeaders(PipelineMessage message)
        {
            if (!message.Request.Headers.TryGetValue("Accept", out _))
            {
                message.Request.Headers.Set("Accept", "application/json");
            }
        }
    }
}
