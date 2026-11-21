namespace DeepSeek.Decisions;

/// <summary>Safe validation or capability error at a decision protocol boundary.</summary>
public sealed class DecisionProtocolException : Exception
{
    public DecisionProtocolException(int statusCode, string code, string message, string? parameter = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Parameter = parameter;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string? Parameter { get; }
}
