using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptionAssistant.Core.Assistant;

namespace LiveCaptionAssistant.Infrastructure.OpenAI;

internal sealed record ResponsesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("input")] IReadOnlyList<ResponsesInputMessage> Input,
    [property: JsonPropertyName("reasoning")] ResponsesReasoning? Reasoning,
    [property: JsonPropertyName("service_tier")] string ServiceTier,
    [property: JsonPropertyName("store")] bool Store,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record ResponsesInputMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ResponsesReasoning([property: JsonPropertyName("effort")] string Effort);

internal readonly record struct ResponseStreamUpdate(string? TextDelta, bool IsCompleted, string? ServiceTier = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResponsesRequest))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext;

internal static class ResponsesProtocol
{
    /// <summary>
    /// Builds a request body that is valid for ChatGPT plan usage: <c>store</c> false, <c>stream</c> true,
    /// instructions instead of system messages, and none of the fields that flow rejects.
    /// </summary>
    public static byte[] CreateRequestBody(ChatRequest request)
    {
        var body = new ResponsesRequest(
            request.ModelId,
            request.Instructions,
            [new ResponsesInputMessage("user", request.Input)],
            request.ReasoningEffort is { } effort ? new ResponsesReasoning(effort.Value) : null,
            // The ChatGPT OAuth endpoint accepts the documented priority alias, but currently rejects fast.
            request.Speed == ResponseSpeed.Fast ? "priority" : "default",
            Store: false,
            Stream: true);
        return JsonSerializer.SerializeToUtf8Bytes(body, OpenAiJsonContext.Default.ResponsesRequest);
    }

    /// <summary>Interprets one streaming event; throws <see cref="ChatServiceException"/> for terminal failures.</summary>
    public static ResponseStreamUpdate ParseEvent(string json, string? requestId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "response.output_text.delta":
                return new ResponseStreamUpdate(root.TryGetProperty("delta", out var delta) ? delta.GetString() : null, IsCompleted: false);

            case "response.completed":
                var serviceTier = root.TryGetProperty("response", out var completedResponse)
                    && completedResponse.ValueKind == JsonValueKind.Object
                    && completedResponse.TryGetProperty("service_tier", out var tier)
                    && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;
                return new ResponseStreamUpdate(null, IsCompleted: true, serviceTier);

            case "response.failed":
                if (root.TryGetProperty("response", out var failed)
                    && failed.TryGetProperty("error", out var failure)
                    && failure.ValueKind == JsonValueKind.Object)
                {
                    throw OpenAiErrorParser.FromErrorObject(failure, requestId);
                }

                throw new ChatServiceException("The model could not produce an answer.", "response_failed", statusCode: null, requestId);

            case "response.incomplete":
                var reason = root.TryGetProperty("response", out var incomplete)
                    && incomplete.TryGetProperty("incomplete_details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("reason", out var reasonElement)
                        ? reasonElement.GetString()
                        : null;
                throw new ChatServiceException(
                    $"The answer was cut off{(reason is null ? string.Empty : $" ({reason})")}.", "response_incomplete", statusCode: null, requestId);

            case "error":
                var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
                throw OpenAiErrorParser.FromErrorObject(error, requestId);

            default:
                return default;
        }
    }
}
