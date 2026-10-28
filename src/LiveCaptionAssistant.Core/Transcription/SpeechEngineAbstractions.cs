namespace LiveCaptionAssistant.Core.Transcription;

public interface ISpeechToTextEngine
{
    bool IsReady { get; }

    /// <summary>Human-readable description of the loaded model and compute backend.</summary>
    string? ActiveModelDescription { get; }

    /// <summary>Downloads (if needed) and loads the configured model.</summary>
    Task EnsureReadyAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken);

    Task<SpeechRecognitionResult> RecognizeAsync(SpeechRecognitionRequest request, CancellationToken cancellationToken);
}

/// <param name="Samples">Mono float samples at <see cref="Audio.AudioConstants.SampleRate"/>.</param>
/// <param name="ForcedLanguage">Language to transcribe in; <see langword="null"/> detects between English and Portuguese.</param>
/// <param name="FallbackLanguage">Used when detection is not possible.</param>
public sealed record SpeechRecognitionRequest(
    float[] Samples,
    SpokenLanguage? ForcedLanguage,
    SpokenLanguage FallbackLanguage);

public sealed record SpeechRecognitionResult(
    string Text,
    SpokenLanguage Language,
    float LanguageConfidence,
    float NoSpeechProbability);

public enum ModelLoadStage
{
    Downloading,
    Loading,
    WarmingUp,
    Ready,
}

public sealed record ModelLoadProgress(ModelLoadStage Stage, long BytesDownloaded = 0, long? TotalBytes = null)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0, 1)
        : null;
}

public sealed record SpeechModelInfo(string Id, string DisplayName, long ApproximateSizeBytes, bool IsDownloaded);

public interface ISpeechModelCatalog
{
    string DefaultModelId { get; }

    IReadOnlyList<SpeechModelInfo> GetModels();

    SpeechModelInfo Resolve(string? modelId);
}
