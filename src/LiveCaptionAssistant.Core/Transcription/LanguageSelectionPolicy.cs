using LiveCaptionAssistant.Core.Audio;

namespace LiveCaptionAssistant.Core.Transcription;

public static class LanguageSelectionPolicy
{
    /// <summary>Whisper's language detection is unreliable on shorter clips.</summary>
    public static readonly TimeSpan MinimumDetectionDuration = TimeSpan.FromSeconds(1.2);

    /// <summary>Detections below this confidence do not change the session language.</summary>
    public const float SessionLanguageConfidenceThreshold = 0.5f;

    /// <summary>
    /// Returns the language a clip must be transcribed in, or <see langword="null"/> when it should be detected.
    /// </summary>
    public static SpokenLanguage? ResolveForcedLanguage(
        LanguageMode mode,
        bool isPartial,
        int sampleCount,
        SpokenLanguage? languageDetectedForSegment,
        SpokenLanguage sessionLanguage)
    {
        if (mode.ToFixedLanguage() is { } fixedLanguage)
        {
            return fixedLanguage;
        }

        if (isPartial && languageDetectedForSegment is { } detected)
        {
            return detected;
        }

        var duration = TimeSpan.FromSeconds((double)sampleCount / AudioConstants.SampleRate);
        return duration < MinimumDetectionDuration ? sessionLanguage : null;
    }
}
