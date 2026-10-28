using System.Runtime.InteropServices;
using LiveCaptionAssistant.Core.Audio;

namespace LiveCaptionAssistant.Core.Transcription;

public sealed record SpeechSegmenterOptions
{
    public int SampleRate { get; init; } = AudioConstants.SampleRate;

    public int FrameMilliseconds { get; init; } = 30;

    /// <summary>Minimum RMS energy treated as speech, regardless of the noise floor (about -48 dBFS).</summary>
    public float AbsoluteThreshold { get; init; } = 0.004f;

    /// <summary>A frame is speech when its energy exceeds the noise floor by this factor.</summary>
    public float NoiseFloorMultiplier { get; init; } = 3f;

    public int NoiseFloorWindowMilliseconds { get; init; } = 3_000;

    /// <summary>Per-frame smoothing applied when the noise floor rises (it falls immediately).</summary>
    public float NoiseFloorRiseRate { get; init; } = 0.01f;

    public int SpeechStartMilliseconds { get; init; } = 150;

    public int SilenceEndMilliseconds { get; init; } = 650;

    public int PrePaddingMilliseconds { get; init; } = 300;

    public int TrailingPaddingMilliseconds { get; init; } = 240;

    /// <summary>Segments with less voiced audio than this are discarded.</summary>
    public int MinSpeechMilliseconds { get; init; } = 350;

    public int MaxSegmentMilliseconds { get; init; } = 15_000;

    /// <summary>When a segment hits the maximum length it is split at the quietest frame within this window.</summary>
    public int SplitSearchMilliseconds { get; init; } = 3_000;

    /// <summary>Interval between <see cref="SpeechSegmentUpdated"/> snapshots; zero or less disables them.</summary>
    public int PartialIntervalMilliseconds { get; init; } = 1_000;
}

public abstract record SpeechSegmentEvent(Guid SegmentId);

public sealed record SpeechSegmentStarted(Guid SegmentId) : SpeechSegmentEvent(SegmentId);

/// <summary>Snapshot of the audio captured so far for a segment that is still in progress.</summary>
public sealed record SpeechSegmentUpdated(Guid SegmentId, float[] Samples) : SpeechSegmentEvent(SegmentId);

public sealed record SpeechSegmentCompleted(Guid SegmentId, float[] Samples) : SpeechSegmentEvent(SegmentId);

public sealed record SpeechSegmentDiscarded(Guid SegmentId) : SpeechSegmentEvent(SegmentId);

/// <summary>
/// Energy-based voice activity detector that turns a continuous audio stream into speech segments.
/// The noise floor is tracked with minimum statistics so steady background noise is not mistaken for speech.
/// Not thread-safe: feed it from a single capture loop.
/// </summary>
public sealed class SpeechSegmenter
{
    private readonly SpeechSegmenterOptions _options;
    private readonly int _frameSize;
    private readonly float[] _frame;
    private readonly int _prePaddingFrames;
    private readonly Queue<(float[] Samples, FrameInfo Info)> _idleFrames = new();
    private readonly List<float> _segmentSamples = [];
    private readonly List<FrameInfo> _segmentFrames = [];
    private readonly float[] _energyHistory;
    private int _energyHistoryIndex;
    private int _energyHistoryCount;
    private float _noiseFloor;
    private int _frameFill;
    private int _speechRunFrames;
    private int _silenceRunFrames;
    private int _samplesAtLastPartial;
    private Guid _segmentId;
    private bool _inSpeech;

    public SpeechSegmenter(SpeechSegmenterOptions? options = null)
    {
        _options = options ?? new SpeechSegmenterOptions();
        _frameSize = _options.SampleRate * _options.FrameMilliseconds / 1000;
        if (_frameSize <= 0)
        {
            throw new ArgumentException("The frame duration and sample rate must produce at least one sample per frame.", nameof(options));
        }

        _frame = new float[_frameSize];
        _prePaddingFrames = Math.Max(0, _options.PrePaddingMilliseconds / _options.FrameMilliseconds);
        _energyHistory = new float[Math.Max(1, _options.NoiseFloorWindowMilliseconds / _options.FrameMilliseconds)];
    }

    public bool IsInSpeech => _inSpeech;

    public IReadOnlyList<SpeechSegmentEvent> Process(ReadOnlySpan<float> samples)
    {
        List<SpeechSegmentEvent>? events = null;
        while (!samples.IsEmpty)
        {
            var take = Math.Min(_frameSize - _frameFill, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];

            if (_frameFill == _frameSize)
            {
                ProcessFrame(ref events);
                _frameFill = 0;
            }
        }

        return events ?? (IReadOnlyList<SpeechSegmentEvent>)[];
    }

    /// <summary>Completes any in-progress segment, e.g. when capture stops.</summary>
    public IReadOnlyList<SpeechSegmentEvent> Flush()
    {
        List<SpeechSegmentEvent>? events = null;
        if (_inSpeech)
        {
            CompleteSegment(trimTrailingSilence: false, ref events);
        }

        ResetToIdle();
        _frameFill = 0;
        return events ?? (IReadOnlyList<SpeechSegmentEvent>)[];
    }

    private void ProcessFrame(ref List<SpeechSegmentEvent>? events)
    {
        var energy = ComputeRms(_frame);
        var threshold = Math.Max(_options.AbsoluteThreshold, _noiseFloor * _options.NoiseFloorMultiplier);
        var info = new FrameInfo(energy, energy >= threshold);
        UpdateNoiseFloor(energy);

        if (!_inSpeech)
        {
            _speechRunFrames = info.IsSpeech ? _speechRunFrames + 1 : 0;
            _idleFrames.Enqueue(((float[])_frame.Clone(), info));
            while (_idleFrames.Count > _prePaddingFrames + _speechRunFrames)
            {
                _idleFrames.Dequeue();
            }

            if (_speechRunFrames * _options.FrameMilliseconds >= _options.SpeechStartMilliseconds)
            {
                StartSegment(ref events);
            }

            return;
        }

        _segmentSamples.AddRange(_frame);
        _segmentFrames.Add(info);
        _silenceRunFrames = info.IsSpeech ? 0 : _silenceRunFrames + 1;

        if (_silenceRunFrames * _options.FrameMilliseconds >= _options.SilenceEndMilliseconds)
        {
            CompleteSegment(trimTrailingSilence: true, ref events);
            ResetToIdle();
            return;
        }

        if (ToMilliseconds(_segmentSamples.Count) >= _options.MaxSegmentMilliseconds)
        {
            SplitSegment(ref events);
            return;
        }

        if (_options.PartialIntervalMilliseconds > 0
            && ToMilliseconds(_segmentSamples.Count - _samplesAtLastPartial) >= _options.PartialIntervalMilliseconds)
        {
            _samplesAtLastPartial = _segmentSamples.Count;
            (events ??= []).Add(new SpeechSegmentUpdated(_segmentId, [.. _segmentSamples]));
        }
    }

    private void StartSegment(ref List<SpeechSegmentEvent>? events)
    {
        _inSpeech = true;
        _segmentId = Guid.NewGuid();
        _segmentSamples.Clear();
        _segmentFrames.Clear();
        foreach (var (samples, info) in _idleFrames)
        {
            _segmentSamples.AddRange(samples);
            _segmentFrames.Add(info);
        }

        _idleFrames.Clear();
        _speechRunFrames = 0;
        _silenceRunFrames = 0;
        _samplesAtLastPartial = 0;
        (events ??= []).Add(new SpeechSegmentStarted(_segmentId));
    }

    private void CompleteSegment(bool trimTrailingSilence, ref List<SpeechSegmentEvent>? events)
    {
        var frameCount = _segmentFrames.Count;
        if (trimTrailingSilence)
        {
            var keepFrames = _options.TrailingPaddingMilliseconds / _options.FrameMilliseconds;
            frameCount -= Math.Max(0, _silenceRunFrames - keepFrames);
        }

        var voicedMilliseconds = CountSpeechFrames(0, frameCount) * _options.FrameMilliseconds;
        if (frameCount <= 0 || voicedMilliseconds < _options.MinSpeechMilliseconds)
        {
            (events ??= []).Add(new SpeechSegmentDiscarded(_segmentId));
        }
        else
        {
            var samples = CollectionsMarshal.AsSpan(_segmentSamples)[..(frameCount * _frameSize)].ToArray();
            (events ??= []).Add(new SpeechSegmentCompleted(_segmentId, samples));
        }

        _inSpeech = false;
    }

    private void SplitSegment(ref List<SpeechSegmentEvent>? events)
    {
        var totalFrames = _segmentFrames.Count;
        var searchFrames = Math.Min(totalFrames - 1, _options.SplitSearchMilliseconds / _options.FrameMilliseconds);
        var splitFrame = totalFrames;
        var quietest = float.MaxValue;
        for (var i = totalFrames - searchFrames; i < totalFrames; i++)
        {
            if (_segmentFrames[i].Energy < quietest)
            {
                quietest = _segmentFrames[i].Energy;
                splitFrame = i + 1;
            }
        }

        var allSamples = CollectionsMarshal.AsSpan(_segmentSamples);
        var completed = allSamples[..(splitFrame * _frameSize)].ToArray();
        var remainder = allSamples[(splitFrame * _frameSize)..].ToArray();
        var remainderFrames = _segmentFrames.GetRange(splitFrame, totalFrames - splitFrame);

        (events ??= []).Add(new SpeechSegmentCompleted(_segmentId, completed));

        _segmentId = Guid.NewGuid();
        _segmentSamples.Clear();
        _segmentSamples.AddRange(remainder);
        _segmentFrames.Clear();
        _segmentFrames.AddRange(remainderFrames);
        _samplesAtLastPartial = 0;
        _silenceRunFrames = CountTrailingSilence();
        events.Add(new SpeechSegmentStarted(_segmentId));
    }

    private void ResetToIdle()
    {
        _inSpeech = false;
        _segmentSamples.Clear();
        _segmentFrames.Clear();
        _idleFrames.Clear();
        _speechRunFrames = 0;
        _silenceRunFrames = 0;
        _samplesAtLastPartial = 0;
    }

    private void UpdateNoiseFloor(float energy)
    {
        _energyHistory[_energyHistoryIndex] = energy;
        _energyHistoryIndex = (_energyHistoryIndex + 1) % _energyHistory.Length;
        _energyHistoryCount = Math.Min(_energyHistoryCount + 1, _energyHistory.Length);

        var minimum = float.MaxValue;
        for (var i = 0; i < _energyHistoryCount; i++)
        {
            minimum = Math.Min(minimum, _energyHistory[i]);
        }

        _noiseFloor = minimum < _noiseFloor
            ? minimum
            : _noiseFloor + ((minimum - _noiseFloor) * _options.NoiseFloorRiseRate);
    }

    private int CountSpeechFrames(int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++)
        {
            if (_segmentFrames[i].IsSpeech)
            {
                count++;
            }
        }

        return count;
    }

    private int CountTrailingSilence()
    {
        var count = 0;
        for (var i = _segmentFrames.Count - 1; i >= 0 && !_segmentFrames[i].IsSpeech; i--)
        {
            count++;
        }

        return count;
    }

    private double ToMilliseconds(int sampleCount) => sampleCount * 1000.0 / _options.SampleRate;

    private static float ComputeRms(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var sample in frame)
        {
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / frame.Length);
    }

    private readonly record struct FrameInfo(float Energy, bool IsSpeech);
}
