using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Infrastructure.OpenAI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

internal sealed record ValidatedIdentity(string Subject, string? Email, string? Name);

/// <summary>Validates OpenAI ID tokens against the published JWKS (signature, issuer, audience, lifetime, nonce).</summary>
internal sealed class OpenIdTokenValidator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly JsonWebTokenHandler _handler = new();

    public OpenIdTokenValidator(IHttpClientFactory httpClientFactory)
    {
        var retriever = new HttpDocumentRetriever(httpClientFactory.CreateClient(HttpClientNames.OpenAIAuth)) { RequireHttps = true };
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            ChatGptOAuthConstants.DiscoveryDocument, new OpenIdConnectConfigurationRetriever(), retriever);
    }

    public async Task<Uri?> GetRevocationEndpointAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return Uri.TryCreate(configuration.RevocationEndpoint, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    public async Task<ValidatedIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var configuration = await _configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var result = await _handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = ChatGptOAuthConstants.Issuer,
                ValidAudience = clientId,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = ClockSkew,
            }).ConfigureAwait(false);

            if (!result.IsValid)
            {
                if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException)
                {
                    _configuration.RequestRefresh();
                    continue;
                }

                throw new AuthenticationFailedException("The ChatGPT identity token could not be validated.", result.Exception);
            }

            var nonce = GetClaim(result.Claims, "nonce");
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            {
                throw new AuthenticationFailedException("The ChatGPT identity token was not issued for this sign-in attempt.");
            }

            var subject = GetClaim(result.Claims, "sub");
            if (string.IsNullOrEmpty(subject))
            {
                throw new AuthenticationFailedException("The ChatGPT identity token has no subject.");
            }

            return new ValidatedIdentity(subject, GetClaim(result.Claims, "email"), GetClaim(result.Claims, "name"));
        }
    }

    private static string? GetClaim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value?.ToString() : null;
}
