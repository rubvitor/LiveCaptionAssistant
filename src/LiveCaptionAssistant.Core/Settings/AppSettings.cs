using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Core.Settings;

public sealed record WindowBounds(double Left, double Top, double Width, double Height);

public sealed record AppSettings
{
    /// <summary>Stored in <see cref="ChatReasoningEffort"/> when the user picked the model's default effort.</summary>
    public const string ModelDefaultEffort = "default";

    public LanguageMode LanguageMode { get; init; } = LanguageMode.AutoDetect;

    public string SystemAudioDeviceId { get; init; } = AudioDeviceIds.DefaultDevice;

    public bool CaptureMicrophone { get; init; }

    public string MicrophoneDeviceId { get; init; } = AudioDeviceIds.DefaultCommunicationsDevice;

    /// <summary>Speech model id; <see langword="null"/> uses the engine default.</summary>
    public string? SpeechModelId { get; init; }

    public bool UseGpuAcceleration { get; init; } = true;

    public bool LivePartialCaptions { get; init; } = true;

    public string? ChatModelId { get; init; }

    /// <summary>A reasoning effort value, <see cref="ModelDefaultEffort"/>, or <see langword="null"/> when not chosen yet.</summary>
    public string? ChatReasoningEffort { get; init; } = "medium";

    public ResponseSpeed ChatResponseSpeed { get; init; } = ResponseSpeed.Standard;

    public int ResponsePreferencesVersion { get; init; }

    public bool AutoAnswerQuestions { get; init; } = true;

    public AnswerLanguagePreference AnswerLanguage { get; init; } = AnswerLanguagePreference.SameAsQuestion;

    public AnswerStyle AnswerStyle { get; init; } = AnswerStyle.Concise;

    /// <summary>Résumé, role or job description the assistant may use in answers.</summary>
    public string UserBackground { get; init; } = string.Empty;

    public double OverlayOpacity { get; init; } = 1.0;

    public WindowBounds? WindowBounds { get; init; }

    public bool HasSeenPlanUsageNotice { get; init; }

    /// <summary>The effort to send with requests; <see langword="null"/> lets the model decide.</summary>
    public ReasoningEffort? GetEffectiveReasoningEffort() =>
        ChatReasoningEffort is null ? ReasoningEffort.Medium
        : string.IsNullOrWhiteSpace(ChatReasoningEffort) || ChatReasoningEffort == ModelDefaultEffort
            ? null
            : new ReasoningEffort(ChatReasoningEffort);
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    void Update(Func<AppSettings, AppSettings> update);
}
