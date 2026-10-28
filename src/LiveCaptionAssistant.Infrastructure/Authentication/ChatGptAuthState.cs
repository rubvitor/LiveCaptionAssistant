using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptionAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

internal sealed record ChatGptAuthState
{
    /// <summary>Stable, opaque <c>ext_agent_host_id</c> of this installation (<c>urn:uuid:...</c>).</summary>
    public required string HostId { get; init; }

    public ChatGptRegistration? Registration { get; init; }
}

/// <summary>The app registration issued for one ChatGPT account; kept across sign-outs.</summary>
internal sealed record ChatGptRegistration
{
    public required string ClientId { get; init; }

    public required string Subject { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    /// <summary>Retained for <c>id_token_hint</c> on later sign-ins.</summary>
    public string? IdToken { get; init; }

    /// <summary><see langword="null"/> while signed out.</summary>
    public ChatGptTokenSet? Tokens { get; init; }
}

internal sealed record ChatGptTokenSet
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? EarliestRefreshAt { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public required DateTimeOffset SavedAt { get; init; }

    [JsonIgnore]
    public bool AllowsPlanUsage => Scopes.Contains(ChatGptOAuthConstants.PlanUsageScope);
}

/// <summary>Persists the ChatGPT credentials encrypted with DPAPI for the current Windows user.</summary>
internal sealed class ChatGptAuthStateStore
{
    private static readonly byte[] Entropy = "LiveCaptionAssistant.ChatGptCredentials.v1"u8.ToArray();

    private readonly string _path = Path.Combine(AppPaths.RoamingDataDirectory, "chatgpt-credentials.bin");
    private readonly ILogger<ChatGptAuthStateStore> _logger;

    public ChatGptAuthStateStore(ILogger<ChatGptAuthStateStore> logger)
    {
        _logger = logger;
    }

    public ChatGptAuthState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(json, StorageJsonContext.Default.ChatGptAuthState);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            _logger.LogWarning(ex, "The saved ChatGPT credentials are unreadable and will be ignored.");
            return null;
        }
    }

    public void Save(ChatGptAuthState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, StorageJsonContext.Default.ChatGptAuthState);
        AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }
}
