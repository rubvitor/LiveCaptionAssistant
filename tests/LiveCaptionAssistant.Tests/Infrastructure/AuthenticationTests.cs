using System.Net;
using System.Text.Json;
using LiveCaptionAssistant.Infrastructure.Authentication;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class PkceTests
{
    [Fact]
    public void Challenge_matches_the_rfc_7636_test_vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Verifiers_are_url_safe_and_unique()
    {
        var first = Pkce.CreateCodeVerifier();
        var second = Pkce.CreateCodeVerifier();

        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, second);
    }
}

public sealed class AuthorizationRequestBuilderTests
{
    [Fact]
    public void First_sign_in_registers_the_app_dynamically()
    {
        var uri = AuthorizationRequestBuilder.Build(Request(clientId: ChatGptOAuthConstants.DynamicRegistrationClientId, isNewRegistration: true));
        var query = LoopbackCallbackListener.ParseQuery(uri.Query.TrimStart('?'));

        Assert.StartsWith(ChatGptOAuthConstants.AuthorizeEndpoint, uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("Live Caption Assistant", query["agent_name_hint"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Contains("chatgpt.tokens.use.direct", query["scope"], StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal("urn:uuid:3f1c2d4e-0000-4000-8000-000000000001", query["ext_agent_host_id"]);
        Assert.False(query.ContainsKey("id_token_hint"));
        Assert.False(query.ContainsKey("prompt"));
    }

    [Fact]
    public void Returning_sign_in_reuses_the_issued_client_and_account_hints()
    {
        var request = Request(clientId: "oaiapp_123", isNewRegistration: false) with
        {
            IdTokenHint = "previous.id.token",
            LoginHint = "user@example.com",
            RequestConsent = true,
        };

        var query = LoopbackCallbackListener.ParseQuery(AuthorizationRequestBuilder.Build(request).Query.TrimStart('?'));

        Assert.Equal("oaiapp_123", query["client_id"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
        Assert.Equal("previous.id.token", query["id_token_hint"]);
        Assert.Equal("user@example.com", query["login_hint"]);
        Assert.Equal("consent", query["prompt"]);
    }

    private static AuthorizationRequest Request(string clientId, bool isNewRegistration) => new(
        clientId,
        isNewRegistration,
        HostId: "urn:uuid:3f1c2d4e-0000-4000-8000-000000000001",
        RedirectUri: "http://127.0.0.1:1455/auth/callback",
        State: "state-value",
        Nonce: "nonce-value",
        CodeChallenge: "challenge",
        IdTokenHint: null,
        LoginHint: null,
        RequestConsent: false);
}

public sealed class LoopbackCallbackListenerTests
{
    [Fact]
    public async Task Returns_the_callback_parameters_and_ignores_other_paths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = LoopbackCallbackListener.Start(preferredPort: 0);
        var callback = listener.WaitForCallbackAsync(cancellationToken);
        using var http = new HttpClient();

        using var notFound = await http.GetAsync(new Uri($"http://127.0.0.1:{listener.Port}/favicon.ico"), cancellationToken);
        using var ok = await http.GetAsync(new Uri($"{listener.RedirectUri}?code=abc&state=xyz%20123&scope=openid+email"), cancellationToken);
        var parameters = await callback.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("abc", parameters["code"]);
        Assert.Equal("xyz 123", parameters["state"]);
        Assert.Equal("openid email", parameters["scope"]);
    }
}

public sealed class ChatGptAuthServiceParsingTests
{
    [Fact]
    public void Scopes_are_split_on_spaces()
    {
        Assert.Equal(["openid", "chatgpt.tokens.use.direct"], ChatGptAuthService.ParseScopes(" openid  chatgpt.tokens.use.direct "));
        Assert.Empty(ChatGptAuthService.ParseScopes(null));
    }

    [Theory]
    [InlineData("1790032532")]
    [InlineData("\"1790032532\"")]
    [InlineData("\"2026-09-21T23:15:32Z\"")]
    public void Earliest_refresh_time_accepts_unix_seconds_or_iso_dates(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ChatGptAuthService.ParseTimestamp(document.RootElement.Clone());

        Assert.Equal(new DateTimeOffset(2026, 9, 21, 23, 15, 32, TimeSpan.Zero), parsed);
    }
}
