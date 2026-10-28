using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Infrastructure.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class ClockedAudioMixerTests
{
    [Fact]
    public void Simultaneous_outputs_are_one_frame_instead_of_concatenated_audio()
    {
        var mixer = new ClockedAudioMixer();
        mixer.AddInput("speakers");
        mixer.AddInput("headset");
        mixer.Write("speakers", Samples(0.2f), ClockedAudioMixer.FrameSamples);
        mixer.Write("headset", Samples(0.3f), ClockedAudioMixer.FrameSamples);

        var frame = mixer.ReadFrame(0);

        Assert.Equal(ClockedAudioMixer.FrameSamples, frame.Length);
        Assert.All(frame, sample => Assert.Equal(0.5f, sample, precision: 5));
        Assert.All(mixer.ReadFrame(ClockedAudioMixer.FrameSamples), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Silent_endpoints_do_not_attenuate_audible_speech_and_removed_endpoints_leave_no_backlog()
    {
        var mixer = new ClockedAudioMixer();
        mixer.AddInput("silent");
        mixer.AddInput("headset");
        mixer.Write("headset", Samples(0.4f), ClockedAudioMixer.FrameSamples);

        Assert.All(mixer.ReadFrame(0), sample => Assert.Equal(0.4f, sample));

        mixer.Write("headset", Samples(0.4f), ClockedAudioMixer.FrameSamples * 2L);
        mixer.RemoveInput("headset");
        mixer.AddInput("headset");
        Assert.All(mixer.ReadFrame(ClockedAudioMixer.FrameSamples), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Callback_jitter_preserves_continuity_but_a_real_pause_stays_silent()
    {
        var mixer = new ClockedAudioMixer();
        mixer.AddInput("headset");
        mixer.Write("headset", Samples(0.2f), 1_600);
        mixer.Write("headset", Samples(0.3f), 3_300);
        mixer.Write("headset", Samples(0.4f), 11_200);

        Assert.All(mixer.ReadFrame(0), sample => Assert.Equal(0.2f, sample));
        Assert.All(mixer.ReadFrame(1_600), sample => Assert.Equal(0.3f, sample));
        Assert.All(mixer.ReadFrame(3_200), sample => Assert.Equal(0f, sample));
        Assert.All(mixer.ReadFrame(9_600), sample => Assert.Equal(0.4f, sample));
    }

    [Fact]
    public void Huge_late_packet_retains_only_the_recent_audio_that_fits_the_bounded_ring()
    {
        var mixer = new ClockedAudioMixer();
        mixer.AddInput("headset");
        var backlog = Enumerable.Repeat(0.5f, ClockedAudioMixer.CapacitySamples * 20).ToArray();
        mixer.Write("headset", backlog, ClockedAudioMixer.FrameSamples);

        Assert.All(mixer.ReadFrame(0), sample => Assert.Equal(0.5f, sample));
        Assert.All(mixer.ReadFrame(ClockedAudioMixer.FrameSamples), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Samples_are_finite_and_clipped_to_the_valid_pcm_range()
    {
        var mixer = new ClockedAudioMixer();
        mixer.AddInput("first");
        mixer.AddInput("second");
        mixer.Write("first", [0.8f, -0.8f, float.NaN], 3);
        mixer.Write("second", [0.8f, -0.8f, float.PositiveInfinity], 3);

        var frame = mixer.ReadFrame(0);

        Assert.Equal(1f, frame[0]);
        Assert.Equal(-1f, frame[1]);
        Assert.Equal(0f, frame[2]);
    }

    private static float[] Samples(float value) => Enumerable.Repeat(value, ClockedAudioMixer.FrameSamples).ToArray();
}

public sealed class AudioRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Existing_default_settings_enable_automatic_outputs_but_specific_device_choices_remain_specific()
    {
        var factory = new WasapiAudioCaptureSourceFactory(NullLoggerFactory.Instance);

        Assert.IsType<AllOutputsAudioCaptureSource>(factory.Create(AudioEndpointKind.SystemOutput, AudioDeviceIds.DefaultDevice));
        Assert.IsType<AllOutputsAudioCaptureSource>(factory.Create(AudioEndpointKind.SystemOutput, null));
        Assert.IsType<WasapiAudioCaptureSource>(factory.Create(AudioEndpointKind.SystemOutput, "headset-id"));
        Assert.IsType<WasapiAudioCaptureSource>(factory.Create(AudioEndpointKind.SystemOutput, AudioDeviceIds.DefaultCommunicationsDevice));
        Assert.IsType<WasapiAudioCaptureSource>(factory.Create(AudioEndpointKind.Microphone, AudioDeviceIds.DefaultDevice));
    }

    [Fact]
    public async Task Headset_connected_after_start_is_captured_without_restarting_and_removal_cancels_only_that_endpoint()
    {
        var speakers = new Source();
        var headset = new Source();
        await using var harness = new Harness(id => id == "speakers" ? speakers : headset);
        harness.Devices = [Device("speakers")];
        harness.Start();
        await speakers.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.All(await harness.TickAsync(TimeSpan.FromMilliseconds(100)), sample => Assert.Equal(0f, sample));
        harness.Devices = [Device("speakers"), Device("headset")];
        await harness.TickAsync(TimeSpan.FromSeconds(2));
        await headset.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await headset.EmitAsync(0.4f);

        await harness.TickAsync(TimeSpan.FromMilliseconds(100));
        Assert.True((await harness.TickAsync(TimeSpan.FromMilliseconds(100))).All(sample => sample == 0.4f));

        harness.Devices = [Device("headset")];
        await harness.TickAsync(TimeSpan.FromSeconds(2));
        await speakers.Stopped.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(headset.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task Failed_endpoint_is_retried_while_other_outputs_keep_delivering_audio()
    {
        var speakers = new Source();
        var recoveredHeadset = new Source();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var harness = new Harness(id =>
        {
            if (id == "speakers")
            {
                return speakers;
            }

            if (Interlocked.Increment(ref attempts) == 1)
            {
                failed.TrySetResult();
                throw new IOException("Headset is reconnecting.");
            }

            return recoveredHeadset;
        }, useRealClock: true);
        harness.Devices = [Device("speakers"), Device("headset")];
        harness.Start();
        await speakers.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await failed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await speakers.EmitAsync(0.3f);
        var heardWorkingEndpoint = false;
        for (var frame = 0; frame < 5; frame++)
        {
            heardWorkingEndpoint |= (await harness.ReadNextFrameAsync()).Any(sample => sample > 0.1f);
        }

        Assert.True(heardWorkingEndpoint);
        // The real refresh clock lets the failing worker unwind before the next discovery.
        // Advancing fake seconds before its Task.Run completes creates a scheduler race.
        await recoveredHeadset.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
        Assert.False(speakers.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task No_initial_outputs_produces_silence_and_later_endpoint_is_stopped_when_capture_is_disposed()
    {
        var headset = new Source();
        await using (var harness = new Harness(_ => headset))
        {
            harness.Start();
            await harness.Initialized.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True((await harness.TickAsync(TimeSpan.FromMilliseconds(100))).All(sample => sample == 0f));

            harness.Devices = [Device("headset")];
            await harness.TickAsync(TimeSpan.FromSeconds(2));
            await headset.Started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        await headset.Stopped.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static AudioDeviceInfo Device(string id) => new(id, id);

    private sealed class Harness(Func<string, IAudioCaptureSource> createSource, bool useRealClock = false) : IAsyncDisposable
    {
        private readonly TimeProvider _time = useRealClock ? TimeProvider.System : new FakeTimeProvider();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Channel<float[]> _frames = Channel.CreateUnbounded<float[]>();
        private Task? _capture;

        public AudioDeviceInfo[] Devices { get; set; } = [];

        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            var source = new AllOutputsAudioCaptureSource(() =>
            {
                Initialized.TrySetResult();
                return Devices;
            }, createSource, NullLogger<AllOutputsAudioCaptureSource>.Instance, _time);
            _capture = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in source.CaptureAsync(_stopping.Token))
                    {
                        _frames.Writer.TryWrite(frame);
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
            });
        }

        public async Task<float[]> TickAsync(TimeSpan duration)
        {
            ((FakeTimeProvider)_time).Advance(duration);
            return await ReadNextFrameAsync();
        }

        public async Task<float[]> ReadNextFrameAsync() =>
            await _frames.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            if (_capture is not null)
            {
                await _capture.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            _stopping.Dispose();
        }
    }

    private sealed class Source : IAudioCaptureSource
    {
        private readonly Channel<(float[] Samples, TaskCompletionSource Accepted)> _packets = Channel.CreateUnbounded<(float[], TaskCompletionSource)>();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EmitAsync(float value)
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _packets.Writer.TryWrite((Enumerable.Repeat(value, ClockedAudioMixer.FrameSamples).ToArray(), accepted));
            await accepted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        public async IAsyncEnumerable<float[]> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await foreach (var packet in _packets.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return packet.Samples;
                    packet.Accepted.TrySetResult();
                }
            }
            finally
            {
                Stopped.TrySetResult();
            }
        }
    }
}
