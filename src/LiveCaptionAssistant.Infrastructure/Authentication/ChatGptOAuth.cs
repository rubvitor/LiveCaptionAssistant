using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

/// <summary>Endpoints and parameters of the "Sign in with ChatGPT" flow for open-source / local apps.</summary>
internal static class ChatGptOAuthConstants
{
    public const string Issuer = "https://auth.openai.com";
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string DiscoveryDocument = "https://auth.openai.com/.well-known/openid-configuration";
    public const string Resource = "https://api.openai.com/v1";
    public const string DynamicRegistrationClientId = "dynamic_agent_client";
    public const string PlanUsageScope = "chatgpt.tokens.use.direct";
    public const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public const string AgentName = "Live Caption Assistant";
    public const string CallbackPath = "/auth/callback";
    public const int PreferredCallbackPort = 1455;
}

internal static class Pkce
{
    public static string CreateCodeVerifier() => CreateRandomToken();

    /// <summary>S256 challenge: base64url(SHA-256(verifier)) without padding (RFC 7636).</summary>
    public static string CreateCodeChallenge(string codeVerifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string CreateRandomToken(int byteCount = 32) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteCount));
}

internal sealed record AuthorizationRequest(
    string ClientId,
    bool IsNewRegistration,
    string HostId,
    string RedirectUri,
    string State,
    string Nonce,
    string CodeChallenge,
    string? IdTokenHint,
    string? LoginHint,
    bool RequestConsent);

internal static class AuthorizationRequestBuilder
{
    /// <remarks>The resulting URL may contain an ID token hint; never log it.</remarks>
    public static Uri Build(AuthorizationRequest request)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", request.ClientId),
            new("response_type", "code"),
            new("redirect_uri", request.RedirectUri),
            new("scope", ChatGptOAuthConstants.Scopes),
            new("resource", ChatGptOAuthConstants.Resource),
            new("state", request.State),
            new("nonce", request.Nonce),
            new("code_challenge", request.CodeChallenge),
            new("code_challenge_method", "S256"),
            new("ext_agent_host_id", request.HostId),
        };

        if (request.IsNewRegistration)
        {
            parameters.Add(new("agent_name_hint", ChatGptOAuthConstants.AgentName));
        }

        if (!string.IsNullOrEmpty(request.IdTokenHint))
        {
            parameters.Add(new("id_token_hint", request.IdTokenHint));
        }

        if (!string.IsNullOrEmpty(request.LoginHint))
        {
            parameters.Add(new("login_hint", request.LoginHint));
        }

        if (request.RequestConsent)
        {
            parameters.Add(new("prompt", "consent"));
        }

        var query = string.Join('&', parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        return new Uri($"{ChatGptOAuthConstants.AuthorizeEndpoint}?{query}");
    }
}
