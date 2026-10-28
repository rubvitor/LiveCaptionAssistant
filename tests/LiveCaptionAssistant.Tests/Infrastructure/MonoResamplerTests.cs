using System.Runtime.InteropServices;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Infrastructure.Audio;
using NAudio.Wave;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class MonoResamplerTests
{
    [Fact]
    public void Stereo_48k_float_is_converted_to_16k_mono_without_losing_level()
    {
        const int sourceRate = 48_000;
        var tone = TestAudio.Tone(1, amplitude: 0.5f, frequency: 440, sampleRate: sourceRate);
        var interleaved = new float[tone.Length * 2];
        for (var i = 0; i < tone.Length; i++)
        {
            interleaved[2 * i] = tone[i];
            interleaved[(2 * i) + 1] = tone[i];
        }

        var resampler = new MonoResampler(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2), AudioConstants.SampleRate);
        var bytes = MemoryMarshal.AsBytes(interleaved.AsSpan());
        var output = new List<float>();
        const int chunkBytes = 480 * 2 * sizeof(float);
        for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
        {
            output.AddRange(resampler.Process(bytes.Slice(offset, Math.Min(chunkBytes, bytes.Length - offset))));
        }

        Assert.InRange(output.Count, AudioConstants.SampleRate - 64, AudioConstants.SampleRate + 64);
        var steadyState = output.Skip(800).Take(14_000).ToArray();
        var rms = Math.Sqrt(steadyState.Average(sample => (double)sample * sample));
        Assert.InRange(rms, 0.30, 0.40);
    }

    [Fact]
    public void Pcm16_mono_at_the_target_rate_passes_through()
    {
        var samples = new short[1_600];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(i % 2 == 0 ? 16_384 : -16_384);
        }

        var resampler = new MonoResampler(new WaveFormat(AudioConstants.SampleRate, 16, 1), AudioConstants.SampleRate);
        var output = resampler.Process(MemoryMarshal.AsBytes(samples.AsSpan()));

        Assert.Equal(samples.Length, output.Length);
        Assert.Equal(0.5f, output[0], precision: 3);
        Assert.Equal(-0.5f, output[1], precision: 3);
    }
}
