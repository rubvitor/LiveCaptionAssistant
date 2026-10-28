namespace LiveCaptionAssistant.Core.Authentication;

/// <param name="PlanUsageEnabled">Whether the user allowed this app to use their ChatGPT plan for AI requests.</param>
public sealed record ChatGptAccount(string Subject, string? Email, string? Name, bool PlanUsageEnabled)
{
    public string DisplayName => Email ?? Name ?? "ChatGPT account";
}

/// <summary>"Sign in with ChatGPT" session management.</summary>
public interface IChatGptAuthenticator
{
    /// <summary>The signed-in account, or <see langword="null"/> when signed out.</summary>
    ChatGptAccount? CurrentAccount { get; }

    event EventHandler? AccountChanged;

    /// <summary>Loads the credentials saved by a previous run.</summary>
    Task RestoreAsync(CancellationToken cancellationToken);

    /// <summary>Runs the browser sign-in flow (the user may pick "Continue with Google" there).</summary>
    /// <param name="requestPlanUsageConsent">Asks OpenAI to show the consent screen again, e.g. after plan usage was declined.</param>
    Task<ChatGptAccount> SignInAsync(bool requestPlanUsageConsent, CancellationToken cancellationToken);

    /// <summary>Revokes the session and clears local tokens.</summary>
    /// <param name="forgetAccount">Also removes the app registration so the next sign-in can use another account.</param>
    /// <returns><see langword="true"/> when OpenAI confirmed the revocation.</returns>
    Task<bool> SignOutAsync(bool forgetAccount, CancellationToken cancellationToken);
}

public interface IAccessTokenProvider
{
    /// <exception cref="AuthenticationRequiredException">No valid session is available.</exception>
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException()
        : base("Sign in with ChatGPT to get AI answers.")
    {
    }

    public AuthenticationRequiredException(string message)
        : base(message)
    {
    }

    public AuthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException()
    {
    }

    public AuthenticationFailedException(string message)
        : base(message)
    {
    }

    public AuthenticationFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
