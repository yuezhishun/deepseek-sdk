using System.ClientModel.Primitives;

namespace DeepSeek;

public class DeepSeekClientOptions : ClientPipelineOptions
{
    private Uri? _endpoint;
    private string? _userAgentApplicationId;

    public Uri Endpoint
    {
        get => _endpoint ?? DeepSeekClientUtilities.DefaultEndpoint;
        set
        {
            AssertNotFrozen();
            _endpoint = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public string? UserAgentApplicationId
    {
        get => _userAgentApplicationId;
        set
        {
            AssertNotFrozen();
            _userAgentApplicationId = value;
        }
    }

    internal DeepSeekClientOptions CloneAndFreeze(
        bool disableMessageLogging = false,
        bool disableMessageContentLogging = false,
        PipelinePolicy? messageLoggingPolicyOverride = null)
    {
        var clone = new DeepSeekClientOptions
        {
            Endpoint = Endpoint,
            UserAgentApplicationId = UserAgentApplicationId,
            Transport = Transport,
            RetryPolicy = RetryPolicy,
            MessageLoggingPolicy = messageLoggingPolicyOverride ?? (disableMessageLogging || disableMessageContentLogging ? null : MessageLoggingPolicy),
            ClientLoggingOptions = CloneClientLoggingOptions(
                ClientLoggingOptions,
                disableMessageLogging,
                disableMessageContentLogging),
            NetworkTimeout = NetworkTimeout,
            EnableDistributedTracing = EnableDistributedTracing,
        };

        if (messageLoggingPolicyOverride is null &&
            clone.ClientLoggingOptions is not null &&
            (MessageLoggingPolicy is null || disableMessageLogging || disableMessageContentLogging))
        {
            clone.MessageLoggingPolicy = new MessageLoggingPolicy(clone.ClientLoggingOptions);
        }

        clone.Freeze();
        return clone;
    }

    private static ClientLoggingOptions? CloneClientLoggingOptions(
        ClientLoggingOptions? options,
        bool disableMessageLogging,
        bool disableMessageContentLogging)
    {
        if (options is null)
        {
            return null;
        }

        var clone = new ClientLoggingOptions
        {
            LoggerFactory = options.LoggerFactory,
            EnableLogging = disableMessageLogging ? false : options.EnableLogging,
            EnableMessageLogging = disableMessageLogging ? false : options.EnableMessageLogging,
            EnableMessageContentLogging = disableMessageLogging || disableMessageContentLogging ? false : options.EnableMessageContentLogging,
            MessageContentSizeLimit = options.MessageContentSizeLimit,
        };

        foreach (var headerName in options.AllowedHeaderNames)
        {
            clone.AllowedHeaderNames.Add(headerName);
        }

        foreach (var queryParameter in options.AllowedQueryParameters)
        {
            clone.AllowedQueryParameters.Add(queryParameter);
        }

        return clone;
    }
}
