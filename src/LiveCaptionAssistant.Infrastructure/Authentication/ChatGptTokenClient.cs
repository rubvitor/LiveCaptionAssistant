using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptionAssistant.Infrastructure.OpenAI;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("earliest_refresh_at")]
    public JsonElement? EarliestRefreshAt { get; init; }
}

[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class TokenJsonContext : JsonSerializerContext;

internal sealed class TokenEndpointException : Exception
{
    private static readonly HashSet<string> UnusableRefreshTokenErrors = new(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused",
    };

    public TokenEndpointException(string error, string? description, int statusCode)
        : base(string.IsNullOrWhiteSpace(description) ? $"Token request failed: {error} (HTTP {statusCode})." : $"Token request failed: {error} - {description}")
    {
        Error = error;
        StatusCode = statusCode;
    }

    public string Error { get; }

    public int StatusCode { get; }

    public bool IsUnusableRefreshToken => UnusableRefreshTokenErrors.Contains(Error);

    public bool IsInvalidClient => Error == "invalid_client";

    public static TokenEndpointException FromResponse(int statusCode, string body)
    {
        string? error = null;
        string? description = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    error = errorElement.GetString();
                    description = root.TryGetProperty("error_description", out var descriptionElement) ? descriptionElement.GetString() : null;
                }
                else if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    error = errorElement.TryGetProperty("code", out var code) ? code.GetString() : null;
                    description = errorElement.TryGetProperty("message", out var message) ? message.GetString() : null;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error bodies are reported by status code only.
        }

        return new TokenEndpointException(error ?? "token_request_failed", description, statusCode);
    }
}

/// <summary>Talks to the OpenAI OAuth token and revocation endpoints (public client, PKCE, no secret).</summary>
internal sealed class ChatGptTokenClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatGptTokenClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<TokenResponse> ExchangeCodeAsync(string clientId, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        PostTokenRequestAsync(
            [
                new("grant_type", "authorization_code"),
                new("client_id", clientId),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri),
                new("resource", ChatGptOAuthConstants.Resource),
            ],
            cancellationToken);

    public Task<TokenResponse> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken) =>
        PostTokenRequestAsync(
            [
                new("grant_type", "refresh_token"),
                new("client_id", clientId),
                new("refresh_token", refreshToken),
                new("resource", ChatGptOAuthConstants.Resource),
            ],
            cancellationToken);

    /// <returns><see langword="true"/> when the server confirmed the revocation.</returns>
    public async Task<bool> RevokeRefreshTokenAsync(Uri revocationEndpoint, string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenAIAuth);
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("token", refreshToken),
            new KeyValuePair<string, string>("token_type_hint", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
        ]);
        using var response = await client.PostAsync(revocationEndpoint, content, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<TokenResponse> PostTokenRequestAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenAIAuth);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatGptOAuthConstants.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw TokenEndpointException.FromResponse((int)response.StatusCode, body);
        }

        return JsonSerializer.Deserialize(body, TokenJsonContext.Default.TokenResponse)
            ?? throw new TokenEndpointException("invalid_token_response", "The token endpoint returned an empty response.", (int)response.StatusCode);
    }
}
