using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace LiveCaptionAssistant.Infrastructure.Audio;

/// <summary>
/// Converts interleaved capture buffers (float or PCM, any channel count and rate) into mono float
/// samples at the target rate. Keeps resampler state between calls, so feed it from one thread.
/// </summary>
internal sealed class MonoResampler
{
    private readonly SampleFormat _sampleFormat;
    private readonly int _channels;
    private readonly int _blockAlign;
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly WdlResampler? _resampler;
    private float[] _mono = [];
    private float[] _output = [];

    public MonoResampler(WaveFormat sourceFormat, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        var format = sourceFormat is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : sourceFormat;
        _sampleFormat = (format.Encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleFormat.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleFormat.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => SampleFormat.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => SampleFormat.Pcm32,
            _ => throw new NotSupportedException($"Unsupported capture format: {format}."),
        };

        _channels = format.Channels;
        _blockAlign = format.BlockAlign;
        _sourceRate = format.SampleRate;
        _targetRate = targetSampleRate;

        if (_sourceRate != _targetRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false, 64, 32);
            _resampler.SetFilterParms(0.693f, 0.707f);
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_sourceRate, _targetRate);
        }
    }

    private enum SampleFormat
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    public float[] Process(ReadOnlySpan<byte> buffer)
    {
        var frames = buffer.Length / _blockAlign;
        if (frames == 0)
        {
            return [];
        }

        EnsureCapacity(ref _mono, frames);
        var mono = _mono.AsSpan(0, frames);
        DownmixToMono(buffer[..(frames * _blockAlign)], mono);

        if (_resampler is null)
        {
            return mono.ToArray();
        }

        var accepted = _resampler.ResamplePrepare(frames, 1, out var input);
        var count = Math.Min(Math.Min(accepted, frames), input.Length);
        mono[..count].CopyTo(input);

        var capacity = (int)Math.Ceiling(count * (double)_targetRate / _sourceRate) + 16;
        EnsureCapacity(ref _output, capacity);
        var produced = _resampler.ResampleOut(_output.AsSpan(0, capacity), count, capacity, 1);
        return _output.AsSpan(0, produced).ToArray();
    }

    private void DownmixToMono(ReadOnlySpan<byte> input, Span<float> mono)
    {
        var scale = 1f / _channels;
        switch (_sampleFormat)
        {
            case SampleFormat.Float32:
                {
                    var samples = MemoryMarshal.Cast<byte, float>(input);
                    for (var frame = 0; frame < mono.Length; frame++)
                    {
                        var offset = frame * _channels;
                        var sum = 0f;
                        for (var channel = 0; channel < _channels; channel++)
                        {
                            sum += samples[offset + channel];
                        }

                        mono[frame] = sum * scale;
                    }

                    break;
                }

            case SampleFormat.Pcm16:
                {
                    var samples = MemoryMarshal.Cast<byte, short>(input);
                    for (var frame = 0; frame < mono.Length; frame++)
                    {
                        var offset = frame * _channels;
                        var sum = 0f;
                        for (var channel = 0; channel < _channels; channel++)
                        {
                            sum += samples[offset + channel] / 32768f;
                        }

                        mono[frame] = sum * scale;
                    }

                    break;
                }

            case SampleFormat.Pcm32:
                {
                    var samples = MemoryMarshal.Cast<byte, int>(input);
                    for (var frame = 0; frame < mono.Length; frame++)
                    {
                        var offset = frame * _channels;
                        var sum = 0f;
                        for (var channel = 0; channel < _channels; channel++)
                        {
                            sum += samples[offset + channel] / 2147483648f;
                        }

                        mono[frame] = sum * scale;
                    }

                    break;
                }

            case SampleFormat.Pcm24:
                for (var frame = 0; frame < mono.Length; frame++)
                {
                    var offset = frame * _blockAlign;
                    var sum = 0f;
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        var index = offset + (channel * 3);
                        var value = input[index] | (input[index + 1] << 8) | ((sbyte)input[index + 2] << 16);
                        sum += value / 8388608f;
                    }

                    mono[frame] = sum * scale;
                }

                break;
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new float[Math.Max(length, buffer.Length * 2)];
        }
    }
}
