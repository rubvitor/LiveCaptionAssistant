using LiveCaptionAssistant.Core.Transcription;
using Whisper.net.Ggml;

namespace LiveCaptionAssistant.Infrastructure.Speech;

public sealed record WhisperModelDescriptor(
    string Id,
    string DisplayName,
    GgmlType Type,
    QuantizationType Quantization,
    long ApproximateSizeBytes)
{
    public string FileName => $"ggml-{Id}.bin";
}

internal sealed class WhisperSpeechModelCatalog : ISpeechModelCatalog
{
    private static readonly WhisperModelDescriptor[] Descriptors =
    [
        new("large-v3-turbo-q5_0", "Large v3 Turbo (Q5) - recommended", GgmlType.LargeV3Turbo, QuantizationType.Q5_0, 574_000_000),
        new("large-v3-turbo", "Large v3 Turbo (full precision)", GgmlType.LargeV3Turbo, QuantizationType.NoQuantization, 1_620_000_000),
        new("medium-q5_0", "Medium (Q5)", GgmlType.Medium, QuantizationType.Q5_0, 539_000_000),
        new("small", "Small - faster, less accurate", GgmlType.Small, QuantizationType.NoQuantization, 488_000_000),
        new("base", "Base - fastest, least accurate", GgmlType.Base, QuantizationType.NoQuantization, 148_000_000),
    ];

    private readonly WhisperModelStore _store;

    public WhisperSpeechModelCatalog(WhisperModelStore store)
    {
        _store = store;
    }

    public string DefaultModelId => Descriptors[0].Id;

    public static WhisperModelDescriptor Find(string? modelId) =>
        Array.Find(Descriptors, descriptor => descriptor.Id == modelId) ?? Descriptors[0];

    public IReadOnlyList<SpeechModelInfo> GetModels() => [.. Descriptors.Select(ToInfo)];

    public SpeechModelInfo Resolve(string? modelId) => ToInfo(Find(modelId));

    private SpeechModelInfo ToInfo(WhisperModelDescriptor descriptor) =>
        new(descriptor.Id, descriptor.DisplayName, descriptor.ApproximateSizeBytes, _store.IsDownloaded(descriptor));
}
