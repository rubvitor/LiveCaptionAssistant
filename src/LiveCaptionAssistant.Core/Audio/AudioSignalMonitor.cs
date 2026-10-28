namespace LiveCaptionAssistant.Core.Audio;

/// <summary>Signal measured from the samples actually delivered to the speech pipeline.</summary>
public sealed record AudioSignalStatus(
    AudioEndpointKind Kind,
    float Peak,
    TimeSpan SilenceDuration,
    bool HasReceivedSignal)
{
    public bool IsSilent => SilenceDuration >= TimeSpan.FromSeconds(10);
}

internal sealed class AudioSignalMonitor
{
    private const float SignalThreshold = 0.001f;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly AudioEndpointKind _kind;
    private readonly TimeProvider _timeProvider;
    private long _lastSignal;
    private long _lastReport;
    private float _peak;
    private bool _hasReceivedSignal;
    private bool _hasReported;

    public AudioSignalMonitor(AudioEndpointKind kind, TimeProvider timeProvider)
    {
        _kind = kind;
        _timeProvider = timeProvider;
        _lastSignal = _lastReport = timeProvider.GetTimestamp();
    }

    public AudioSignalStatus? Process(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            if (float.IsFinite(sample))
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }

        var now = _timeProvider.GetTimestamp();
        if (peak >= SignalThreshold)
        {
            _lastSignal = now;
            _hasReceivedSignal = true;
        }

        _peak = Math.Max(_peak, peak);
        if (_hasReported && _timeProvider.GetElapsedTime(_lastReport, now) < ReportInterval)
        {
            return null;
        }

        var status = new AudioSignalStatus(_kind, Math.Clamp(_peak, 0, 1),
            _timeProvider.GetElapsedTime(_lastSignal, now), _hasReceivedSignal);
        _lastReport = now;
        _hasReported = true;
        _peak = 0;
        return status;
    }
}
