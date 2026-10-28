using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Core.Assistant;

public sealed record PendingQuestion(string Text, SpokenLanguage? Language, IReadOnlyList<Guid> SegmentIds)
{
    public static PendingQuestion From(TranscriptSegment segment) => new(segment.Text, segment.Language, [segment.Id]);

    public PendingQuestion Append(TranscriptSegment segment) => this with
    {
        Text = $"{Text} {segment.Text}",
        Language = Language ?? segment.Language,
        SegmentIds = [.. SegmentIds, segment.Id],
    };
}

/// <summary>
/// Waits until the other participants stop talking before releasing a detected question,
/// so follow-up sentences ("...especially in production?") become part of the same question.
/// </summary>
public sealed class QuestionDebouncer : IDisposable
{
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    // Timers must never fire synchronously while the gate is held.
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxWait;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private PendingQuestion? _pending;
    private DateTimeOffset _detectedAt;
    private bool _othersSpeaking;
    private long _generation;

    public QuestionDebouncer(TimeProvider timeProvider, TimeSpan? quietPeriod = null, TimeSpan? maxWait = null)
    {
        _timeProvider = timeProvider;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        _maxWait = maxWait ?? DefaultMaxWait;
    }

    public event EventHandler<PendingQuestion>? QuestionReady;

    public void OnSegmentFinalized(TranscriptSegment segment)
    {
        if (segment.Speaker != Speaker.Them)
        {
            return;
        }

        lock (_gate)
        {
            if (_pending is not null)
            {
                _pending = _pending.Append(segment);
            }
            else if (segment.IsQuestion)
            {
                _pending = PendingQuestion.From(segment);
                _detectedAt = _timeProvider.GetUtcNow();
            }
            else
            {
                return;
            }

            Reschedule();
        }
    }

    public void OnSpeechActivity(Speaker speaker, bool isActive)
    {
        if (speaker != Speaker.Them)
        {
            return;
        }

        lock (_gate)
        {
            _othersSpeaking = isActive;
            if (_pending is not null)
            {
                Reschedule();
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending = null;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Reset();

    private void Reschedule()
    {
        var remaining = _maxWait - (_timeProvider.GetUtcNow() - _detectedAt);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var delay = _othersSpeaking || remaining < _quietPeriod ? remaining : _quietPeriod;
        if (delay < MinimumDelay)
        {
            delay = MinimumDelay;
        }

        var generation = ++_generation;
        _timer?.Dispose();
        _timer = _timeProvider.CreateTimer(_ => Release(generation), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Release(long generation)
    {
        PendingQuestion? ready;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            ready = _pending;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }

        if (ready is not null)
        {
            QuestionReady?.Invoke(this, ready);
        }
    }
}
