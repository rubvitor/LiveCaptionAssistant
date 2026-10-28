using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Platform;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

/// <summary>
/// "Sign in with ChatGPT" for a local app: browser sign-in with PKCE and a loopback redirect,
/// dynamic client registration, ID token validation, rotating refresh tokens and revocation.
/// </summary>
internal sealed class ChatGptAuthService : IChatGptAuthenticator, IAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromHours(1);

    private readonly ChatGptAuthStateStore _store;
    private readonly ChatGptTokenClient _tokenClient;
    private readonly OpenIdTokenValidator _tokenValidator;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatGptAuthService> _logger;

    // Sign-in can wait minutes for the browser, so it must not block token refreshes.
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private ChatGptAuthState? _state;
    private ChatGptAccount? _account;

    public ChatGptAuthService(
        ChatGptAuthStateStore store,
        ChatGptTokenClient tokenClient,
        OpenIdTokenValidator tokenValidator,
        IBrowserLauncher browser,
        TimeProvider timeProvider,
        ILogger<ChatGptAuthService> logger)
    {
        _store = store;
        _tokenClient = tokenClient;
        _tokenValidator = tokenValidator;
        _browser = browser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler? AccountChanged;

    public ChatGptAccount? CurrentAccount => Volatile.Read(ref _account);

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state = _store.Load();
            SetAccount(ToAccount(_state?.Registration));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<ChatGptAccount> SignInAsync(bool requestPlanUsageConsent, CancellationToken cancellationToken)
    {
        await _signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetOrCreateStateAsync(cancellationToken).ConfigureAwait(false);
            var registration = state.Registration;
            var isNewRegistration = registration is null;
            var requestedClientId = registration?.ClientId ?? ChatGptOAuthConstants.DynamicRegistrationClientId;

            var codeVerifier = Pkce.CreateCodeVerifier();
            var oauthState = Pkce.CreateRandomToken();
            var nonce = Pkce.CreateRandomToken();

            using var listener = LoopbackCallbackListener.Start(ChatGptOAuthConstants.PreferredCallbackPort);
            var authorizationUri = AuthorizationRequestBuilder.Build(new AuthorizationRequest(
                requestedClientId,
                isNewRegistration,
                state.HostId,
                listener.RedirectUri,
                oauthState,
                nonce,
                Pkce.CreateCodeChallenge(codeVerifier),
                registration?.IdToken,
                registration?.Email,
                requestPlanUsageConsent));

            _logger.LogInformation("Opening the browser for ChatGPT sign-in (callback port {Port}, new registration {IsNew}).", listener.Port, isNewRegistration);
            _browser.Open(authorizationUri);
            var callback = await listener.WaitForCallbackAsync(cancellationToken).ConfigureAwait(false);

            var issuedClientId = ValidateCallback(callback, oauthState, isNewRegistration, requestedClientId, out var code);
            TokenResponse tokens;
            try
            {
                tokens = await _tokenClient.ExchangeCodeAsync(issuedClientId, code, codeVerifier, listener.RedirectUri, cancellationToken).ConfigureAwait(false);
            }
            catch (TokenEndpointException ex) when (ex.Error == "invalid_grant")
            {
                throw new AuthenticationFailedException("The sign-in code expired before it could be used. Please try again.", ex);
            }

            if (string.IsNullOrEmpty(tokens.IdToken))
            {
                throw new AuthenticationFailedException("ChatGPT did not return an identity token.");
            }

            var identity = await _tokenValidator.ValidateAsync(tokens.IdToken, issuedClientId, nonce, cancellationToken).ConfigureAwait(false);
            if (registration is not null && identity.Subject != registration.Subject)
            {
                throw new AuthenticationFailedException(
                    $"You signed in with a different ChatGPT account than {registration.Email ?? "the saved one"}. Use 'Forget account' in Settings to switch accounts.");
            }

            var callbackScopes = ParseScopes(callback.GetValueOrDefault("scope"));
            var updatedRegistration = new ChatGptRegistration
            {
                ClientId = issuedClientId,
                Subject = identity.Subject,
                Email = identity.Email ?? registration?.Email,
                Name = identity.Name ?? registration?.Name,
                IdToken = tokens.IdToken,
                Tokens = CreateTokenSet(tokens, previous: null, callbackScopes),
            };

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                UpdateState((_state ?? state) with { Registration = updatedRegistration });
            }
            finally
            {
                _stateGate.Release();
            }

            _logger.LogInformation("Signed in with ChatGPT (plan usage enabled: {PlanUsage}).", updatedRegistration.Tokens.AllowsPlanUsage);
            return ToAccount(updatedRegistration)!;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    public async Task<bool> SignOutAsync(bool forgetAccount, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            var registration = state?.Registration;
            if (state is null || registration is null)
            {
                SetAccount(null);
                return true;
            }

            var revoked = true;
            if (registration.Tokens is { } tokens)
            {
                revoked = await TryRevokeAsync(registration.ClientId, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            UpdateState(state with { Registration = forgetAccount ? null : registration with { Tokens = null } });
            _logger.LogInformation("Signed out of ChatGPT (forget account: {Forget}, revocation confirmed: {Revoked}).", forgetAccount, revoked);
            return revoked;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registration = _state?.Registration;
            var tokens = registration?.Tokens ?? throw new AuthenticationRequiredException();
            if (!tokens.AllowsPlanUsage)
            {
                throw new AuthenticationRequiredException("ChatGPT plan usage isn't enabled for this app. Use 'Enable plan usage' to allow it.");
            }

            var now = _timeProvider.GetUtcNow();
            var stillValid = now < tokens.ExpiresAt;
            var refreshDue = now >= tokens.ExpiresAt - RefreshMargin;
            var refreshAllowed = tokens.EarliestRefreshAt is not { } earliest || now >= earliest || !stillValid;
            if (!forceRefresh && (!refreshDue || !refreshAllowed))
            {
                return tokens.AccessToken;
            }

            return await RefreshAsync(registration, tokens, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Dispose()
    {
        _signInGate.Dispose();
        _stateGate.Dispose();
    }

    internal static IReadOnlyList<string> ParseScopes(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? [] : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static DateTimeOffset? ParseTimestamp(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        long? seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

        if (seconds is { } unixTime)
        {
            return unixTime > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime) : DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }

        return value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }

    private static string ValidateCallback(
        IReadOnlyDictionary<string, string> callback,
        string expectedState,
        bool isNewRegistration,
        string requestedClientId,
        out string code)
    {
        if (!callback.TryGetValue("state", out var returnedState)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(expectedState)))
        {
            throw new AuthenticationFailedException("The sign-in response could not be verified. Please try again.");
        }

        if (callback.TryGetValue("error", out var error))
        {
            var description = callback.GetValueOrDefault("error_description");
            throw new AuthenticationFailedException(error == "access_denied"
                ? "Sign-in was cancelled or permission was not granted."
                : $"Sign-in failed: {description ?? error}");
        }

        if (!callback.TryGetValue("code", out var returnedCode) || string.IsNullOrEmpty(returnedCode))
        {
            throw new AuthenticationFailedException("The sign-in response did not include an authorization code.");
        }

        code = returnedCode;
        var returnedClientId = callback.GetValueOrDefault("client_id");
        if (isNewRegistration)
        {
            if (string.IsNullOrWhiteSpace(returnedClientId) || returnedClientId == ChatGptOAuthConstants.DynamicRegistrationClientId)
            {
                throw new AuthenticationFailedException("ChatGPT did not complete the app registration. Please try again.");
            }

            return returnedClientId;
        }

        if (!string.IsNullOrEmpty(returnedClientId) && returnedClientId != requestedClientId)
        {
            throw new AuthenticationFailedException("The sign-in response belongs to a different app registration.");
        }

        return requestedClientId;
    }

    private async Task<string> RefreshAsync(ChatGptRegistration registration, ChatGptTokenSet tokens, CancellationToken cancellationToken)
    {
        TokenResponse response;
        try
        {
            response = await _tokenClient.RefreshAsync(registration.ClientId, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (TokenEndpointException ex) when (ex.IsUnusableRefreshToken || ex.IsInvalidClient)
        {
            _logger.LogWarning("The ChatGPT refresh token was rejected ({Error}); a new sign-in is required.", ex.Error);
            UpdateState(_state! with { Registration = registration with { Tokens = null } });
            throw new AuthenticationRequiredException("Your ChatGPT session expired. Sign in again.", ex);
        }

        var refreshed = CreateTokenSet(response, tokens, tokens.Scopes);
        UpdateState(_state! with
        {
            Registration = registration with { Tokens = refreshed, IdToken = response.IdToken ?? registration.IdToken },
        });
        return refreshed.AccessToken;
    }

    private async Task<bool> TryRevokeAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await _tokenValidator.GetRevocationEndpointAsync(cancellationToken).ConfigureAwait(false);
            return endpoint is not null
                && await _tokenClient.RevokeRefreshTokenAsync(endpoint, clientId, refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Could not revoke the ChatGPT session remotely.");
            return false;
        }
    }

    private async Task<ChatGptAuthState> GetOrCreateStateAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is null)
            {
                var state = _store.Load() ?? new ChatGptAuthState { HostId = $"urn:uuid:{Guid.NewGuid():D}" };
                UpdateState(state);
            }

            return _state!;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private ChatGptTokenSet CreateTokenSet(TokenResponse response, ChatGptTokenSet? previous, IReadOnlyList<string> fallbackScopes)
    {
        var now = _timeProvider.GetUtcNow();
        var accessToken = response.AccessToken;
        var refreshToken = response.RefreshToken ?? previous?.RefreshToken;
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            throw new AuthenticationFailedException("ChatGPT returned an incomplete token response.");
        }

        var scopes = ParseScopes(response.Scope);
        return new ChatGptTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = now + (response.ExpiresIn is > 0 ? TimeSpan.FromSeconds(response.ExpiresIn.Value) : DefaultAccessTokenLifetime),
            EarliestRefreshAt = ParseTimestamp(response.EarliestRefreshAt),
            Scopes = scopes.Count > 0 ? scopes : fallbackScopes,
            SavedAt = now,
        };
    }

    private void UpdateState(ChatGptAuthState state)
    {
        _state = state;
        _store.Save(state);
        SetAccount(ToAccount(state.Registration));
    }

    private void SetAccount(ChatGptAccount? account)
    {
        var previous = Interlocked.Exchange(ref _account, account);
        if (previous != account)
        {
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ChatGptAccount? ToAccount(ChatGptRegistration? registration) =>
        registration?.Tokens is { } tokens
            ? new ChatGptAccount(registration.Subject, registration.Email, registration.Name, tokens.AllowsPlanUsage)
            : null;
}
