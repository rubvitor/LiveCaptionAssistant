using System.Text.Json;
using LiveCaptionAssistant.Core.Assistant;

namespace LiveCaptionAssistant.Infrastructure.OpenAI;

/// <summary>
/// Maps OpenAI error payloads to <see cref="ChatServiceException"/> with user-facing messages,
/// including the ChatGPT plan usage codes documented for "Sign in with ChatGPT".
/// </summary>
internal static class OpenAiErrorParser
{
    public static ChatServiceException FromHttpResponse(int statusCode, string? body, string? requestId)
    {
        string? code = null;
        string? message = null;
        string? parameter = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    (code, message, parameter) = Read(error);
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
                {
                    message = detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.GetRawText();
                }
            }
            catch (JsonException)
            {
                message = body.Length > 300 ? body[..300] : body;
            }
        }

        return new ChatServiceException(Describe(code, message, parameter, statusCode), code, statusCode, requestId);
    }

    public static ChatServiceException FromErrorObject(JsonElement error, string? requestId)
    {
        var (code, message, parameter) = Read(error);
        return new ChatServiceException(Describe(code, message, parameter, statusCode: null), code, statusCode: null, requestId);
    }

    internal static string Describe(string? code, string? message, string? parameter, int? statusCode) => code switch
    {
        ChatServiceException.UsageLimitExceededCode =>
            "You've reached the ChatGPT usage limit for this app. Review your plan and app limits in ChatGPT Settings > Usage.",
        "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" =>
            "ChatGPT usage is temporarily unavailable. Try again in a moment.",
        "subscription_sharing_user_not_eligible" =>
            "ChatGPT plan usage isn't available for this account or workspace.",
        "subscription_sharing_unsupported_capability" =>
            $"This option isn't supported with ChatGPT plan usage{(parameter is null ? string.Empty : $" ({parameter})")}. Try another model or reasoning effort.",
        "subscription_sharing_route_not_supported" =>
            "This request type isn't supported with ChatGPT plan usage.",
        ChatServiceException.InvalidUserCode =>
            "Your ChatGPT session couldn't be validated. Sign in again.",
        "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
            "This app isn't authorized to use your ChatGPT plan. Sign in again and allow plan usage.",
        _ when statusCode == 401 => "ChatGPT rejected the session. Sign in again.",
        _ when statusCode == 503 && message is null => "ChatGPT is temporarily unavailable. Try again shortly.",
        _ => message ?? (statusCode is { } status ? $"The request failed with HTTP {status}." : "The request failed."),
    };

    private static (string? Code, string? Message, string? Parameter) Read(JsonElement error) =>
        (GetString(error, "code"), GetString(error, "message"), GetString(error, "param"));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
