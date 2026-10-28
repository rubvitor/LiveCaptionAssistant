using LiveCaptionAssistant.Core.Audio;

namespace LiveCaptionAssistant.Infrastructure.Audio;

/// <summary>
/// Places independent endpoint packets on one sample timeline. Each endpoint has a fixed-size
/// ring, so a stalled consumer cannot grow memory or replay an old backlog after a route change.
/// </summary>
internal sealed class ClockedAudioMixer
{
    public const int FrameSamples = AudioConstants.SampleRate / 10;
    public const int LatencySamples = FrameSamples * 2;
    public const int CapacitySamples = AudioConstants.SampleRate;
    private const int JitterToleranceSamples = FrameSamples;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Input> _inputs = new(StringComparer.Ordinal);
    private long _readPosition = -LatencySamples;

    public void AddInput(string id)
    {
        lock (_gate)
        {
            _inputs.TryAdd(id, new Input());
        }
    }

    public void RemoveInput(string id)
    {
        lock (_gate)
        {
            _inputs.Remove(id);
        }
    }

    /// <param name="endPosition">Packet arrival time, in samples on the common monotonic clock.</param>
    public void Write(string id, ReadOnlySpan<float> samples, long endPosition)
    {
        lock (_gate)
        {
            if (!_inputs.TryGetValue(id, out var input) || samples.IsEmpty)
            {
                return;
            }

            var start = endPosition - samples.Length;
            // Keep adjacent packets continuous despite callback jitter. A real pause or a slow
            // endpoint starts at its current clock position, leaving silence in the gap.
            if (input.NextPosition is { } next && Math.Abs(start - next) <= JitterToleranceSamples)
            {
                start = next;
            }

            input.NextPosition = start + samples.Length;
            var skip = (int)Math.Min(samples.Length, Math.Max(0, _readPosition - start));
            skip = Math.Max(skip, samples.Length - CapacitySamples);
            for (var i = skip; i < samples.Length; i++)
            {
                var position = start + i;
                if (position >= _readPosition + CapacitySamples)
                {
                    break;
                }

                var index = RingIndex(position);
                input.Samples[index] = float.IsFinite(samples[i]) ? samples[i] : 0;
                input.Positions[index] = position;
            }
        }
    }

    public float[] ReadFrame(long startPosition)
    {
        lock (_gate)
        {
            if (startPosition < _readPosition)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), "The mix clock cannot run backwards.");
            }

            var mixed = new float[FrameSamples];
            foreach (var input in _inputs.Values)
            {
                for (var i = 0; i < mixed.Length; i++)
                {
                    var position = startPosition + i;
                    var index = RingIndex(position);
                    if (input.Positions[index] == position)
                    {
                        mixed[i] += input.Samples[index];
                    }
                }
            }

            for (var i = 0; i < mixed.Length; i++)
            {
                mixed[i] = Math.Clamp(mixed[i], -1f, 1f);
            }

            _readPosition = startPosition + mixed.Length;
            return mixed;
        }
    }

    private static int RingIndex(long position) => (int)((position % CapacitySamples + CapacitySamples) % CapacitySamples);

    private sealed class Input
    {
        public float[] Samples { get; } = new float[CapacitySamples];

        public long[] Positions { get; } = Enumerable.Repeat(long.MinValue, CapacitySamples).ToArray();

        public long? NextPosition { get; set; }
    }
}
