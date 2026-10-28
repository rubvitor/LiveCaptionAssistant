using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.App.ViewModels;

public sealed record OptionItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record EffortOption(ReasoningEffort? Effort, string Label)
{
    public string SettingValue => Effort?.Value ?? AppSettings.ModelDefaultEffort;
}

public sealed partial class TranscriptItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageLabel))]
    private SpokenLanguage? _language;

    [ObservableProperty]
    private bool _isFinal;

    [ObservableProperty]
    private bool _isQuestion;

    public TranscriptItemViewModel(TranscriptSegment segment)
    {
        Id = segment.Id;
        Speaker = segment.Speaker;
        StartedAt = segment.StartedAt;
        Update(segment);
    }

    public Guid Id { get; }

    public Speaker Speaker { get; }

    public DateTimeOffset StartedAt { get; }

    public bool IsMe => Speaker == Speaker.Me;

    public string SpeakerLabel => IsMe ? "Me" : "Them";

    public string TimeLabel => StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LanguageLabel => Language?.ToShortLabel() ?? "··";

    public void Update(TranscriptSegment segment)
    {
        Text = segment.Text;
        Language = segment.Language;
        IsFinal = segment.IsFinal;
        IsQuestion = segment.IsQuestion;
    }
}

public sealed partial class AnswerItemViewModel : ObservableObject
{
    private readonly string _baseModelLabel;
    private readonly ResponseSpeed _requestedSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelLabel), nameof(StatusLabel), nameof(SpeedNotice), nameof(HasSpeedNotice))]
    private string? _appliedServiceTier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInProgress), nameof(IsPending), nameof(StatusLabel))]
    private AnswerStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isUsageLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private TimeSpan? _duration;

    public AnswerItemViewModel(AnswerSnapshot snapshot)
    {
        Id = snapshot.Id;
        Question = snapshot.Question;
        OriginLabel = snapshot.IsAutomatic ? "auto" : "asked";
        _baseModelLabel = string.IsNullOrEmpty(snapshot.ReasoningEffort)
            ? snapshot.ModelId
            : $"{snapshot.ModelId} · {snapshot.ReasoningEffort}";
        _requestedSpeed = snapshot.Speed;
        Apply(snapshot);
    }

    public Guid Id { get; }

    public string Question { get; }

    public string OriginLabel { get; }

    public string ModelLabel => $"{_baseModelLabel} · {SpeedLabel}";

    public string? SpeedNotice => _requestedSpeed == ResponseSpeed.Fast && AppliedServiceTier is "default" or "auto"
        ? "Fast was requested, but the service used Standard for this answer."
        : null;

    public bool HasSpeedNotice => SpeedNotice is not null;

    private string SpeedLabel => AppliedServiceTier switch
    {
        "fast" or "priority" => "Fast",
        "default" or "auto" => "Standard",
        _ => _requestedSpeed == ResponseSpeed.Fast ? "Fast requested" : "Standard",
    };

    public bool HasText => Text.Length > 0;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsPending => Status == AnswerStatus.Pending;

    public bool IsInProgress => Status is AnswerStatus.Pending or AnswerStatus.Streaming;

    public string StatusLabel => Status switch
    {
        AnswerStatus.Pending => $"Thinking… · {ModelLabel}",
        AnswerStatus.Streaming => $"Answering… · {ModelLabel}",
        AnswerStatus.Completed => Duration is { } duration
            ? $"{ModelLabel} · {duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
            : ModelLabel,
        AnswerStatus.Cancelled => $"Stopped · {ModelLabel}",
        _ => $"Failed · {ModelLabel}",
    };

    public void Apply(AnswerSnapshot snapshot)
    {
        Text = snapshot.Text;
        Status = snapshot.Status;
        ErrorMessage = snapshot.ErrorMessage;
        IsUsageLimit = snapshot.ErrorCode == ChatServiceException.UsageLimitExceededCode;
        Duration = snapshot.Duration;
        AppliedServiceTier = snapshot.AppliedServiceTier;
    }
}
