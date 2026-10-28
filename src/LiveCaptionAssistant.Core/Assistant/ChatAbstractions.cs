namespace LiveCaptionAssistant.Core.Assistant;

/// <param name="ReasoningEffort"><see langword="null"/> lets the model use its default effort.</param>
public sealed record ChatRequest(string ModelId, ReasoningEffort? ReasoningEffort, string Instructions, string Input,
    ResponseSpeed Speed = ResponseSpeed.Standard);

public enum ResponseSpeed
{
    Standard,
    Fast,
}

public sealed record ChatResponseUpdate(string? TextDelta, string? ServiceTier = null);

public interface IChatResponseClient
{
    /// <summary>Streams answer text and the processing tier reported by the service.</summary>
    IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(ChatRequest request, CancellationToken cancellationToken);
}

public interface IModelCatalogClient
{
    /// <summary>Models available to the signed-in ChatGPT account, in the server's order.</summary>
    Task<IReadOnlyList<ChatModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken);
}

public sealed class ChatServiceException : Exception
{
    public const string UsageLimitExceededCode = "subscription_sharing_usage_limit_exceeded";
    public const string InvalidUserCode = "subscription_sharing_invalid_user";

    public ChatServiceException()
    {
    }

    public ChatServiceException(string message)
        : base(message)
    {
    }

    public ChatServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ChatServiceException(string message, string? errorCode, int? statusCode, string? requestId = null)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        RequestId = requestId;
    }

    public string? ErrorCode { get; }

    public int? StatusCode { get; }

    public string? RequestId { get; }

    public bool IsUsageLimit => ErrorCode == UsageLimitExceededCode;
}
