using LiveCaptionAssistant.Core.Audio;
using Microsoft.Extensions.Time.Testing;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class AudioSignalMonitorTests
{
    [Fact]
    public void Silent_capture_warns_after_ten_seconds_and_recovers_when_sound_arrives()
    {
        var clock = new FakeTimeProvider();
        var monitor = new AudioSignalMonitor(AudioEndpointKind.SystemOutput, clock);
        var initial = Assert.IsType<AudioSignalStatus>(monitor.Process([0f, 0f]));
        Assert.False(initial.IsSilent);
        Assert.False(initial.HasReceivedSignal);

        clock.Advance(TimeSpan.FromSeconds(10));
        var silent = Assert.IsType<AudioSignalStatus>(monitor.Process([0f]));
        Assert.True(silent.IsSilent);
        Assert.Equal(TimeSpan.FromSeconds(10), silent.SilenceDuration);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        var recovered = Assert.IsType<AudioSignalStatus>(monitor.Process([-0.05f, 0.03f]));
        Assert.True(recovered.HasReceivedSignal);
        Assert.False(recovered.IsSilent);
        Assert.Equal(0.05f, recovered.Peak);
        Assert.Equal(TimeSpan.Zero, recovered.SilenceDuration);
    }

    [Fact]
    public void Reports_are_throttled_without_losing_the_peak_between_reports()
    {
        var clock = new FakeTimeProvider();
        var monitor = new AudioSignalMonitor(AudioEndpointKind.SystemOutput, clock);
        monitor.Process([0f]);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(monitor.Process([-0.4f]));
        clock.Advance(TimeSpan.FromMilliseconds(150));

        var status = Assert.IsType<AudioSignalStatus>(monitor.Process([0.1f]));

        Assert.Equal(0.4f, status.Peak);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0f, Assert.IsType<AudioSignalStatus>(monitor.Process([0f])).Peak);
    }

    [Fact]
    public void Tiny_numerical_noise_does_not_hide_a_missing_audio_signal()
    {
        var clock = new FakeTimeProvider();
        var monitor = new AudioSignalMonitor(AudioEndpointKind.SystemOutput, clock);
        clock.Advance(TimeSpan.FromSeconds(12));

        var status = Assert.IsType<AudioSignalStatus>(monitor.Process([float.NaN, float.PositiveInfinity, 0.000001f]));

        Assert.True(status.IsSilent);
        Assert.False(status.HasReceivedSignal);
        Assert.Equal(AudioEndpointKind.SystemOutput, status.Kind);
        Assert.True(float.IsFinite(status.Peak));
    }
}
