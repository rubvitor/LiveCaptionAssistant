using LiveCaptionAssistant.Core.Audio;

namespace LiveCaptionAssistant.Tests;

internal static class TestAudio
{
    public static float[] Silence(double seconds) => new float[Samples(seconds)];

    public static float[] Tone(double seconds, float amplitude = 0.3f, double frequency = 220, int sampleRate = AudioConstants.SampleRate)
    {
        var samples = new float[Samples(seconds, sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        }

        return samples;
    }

    /// <summary>
    /// Syllable-like bursts (240 ms of tone, 60 ms of near silence). Unlike a steady tone, it has the short
    /// energy dips real speech has, which keep the adaptive noise floor low.
    /// </summary>
    public static float[] SpeechLike(double seconds)
    {
        var samples = Tone(seconds);
        var period = Samples(0.3);
        var gap = Samples(0.06);
        for (var i = 0; i < samples.Length; i++)
        {
            if (i % period >= period - gap)
            {
                samples[i] *= 0.02f;
            }
        }

        return samples;
    }

    /// <summary>Deterministic uniform white noise with the given RMS level.</summary>
    public static float[] Noise(double seconds, float rms, int seed = 7)
    {
        var random = new Random(seed);
        var peak = rms * MathF.Sqrt(3);
        var samples = new float[Samples(seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1) * peak;
        }

        return samples;
    }

    public static float[] Concat(params float[][] parts) => [.. parts.SelectMany(part => part)];

    public static int Samples(double seconds, int sampleRate = AudioConstants.SampleRate) => (int)(seconds * sampleRate);

    public static double Seconds(int sampleCount) => (double)sampleCount / AudioConstants.SampleRate;
}
