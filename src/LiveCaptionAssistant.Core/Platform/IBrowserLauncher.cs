namespace LiveCaptionAssistant.Core.Platform;

public interface IBrowserLauncher
{
    /// <summary>Opens an http(s) address in the user's default browser.</summary>
    void Open(Uri address);
}
