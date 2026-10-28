using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Core.Captioning;

public enum CaptionSessionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

public sealed record CaptionSessionOptions(
    LanguageMode LanguageMode,
    string SystemAudioDeviceId,
    bool CaptureMicrophone,
    string MicrophoneDeviceId,
    bool LivePartialCaptions);

/// <param name="IsActive">
/// True from the moment a speaker starts talking until the speech is transcribed.
/// </param>
public sealed record SpeechActivity(Speaker Speaker, bool IsActive);

internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
