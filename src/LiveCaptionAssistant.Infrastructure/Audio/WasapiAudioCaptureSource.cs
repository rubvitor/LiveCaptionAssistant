using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LiveCaptionAssistant.Core.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveCaptionAssistant.Infrastructure.Audio;

internal sealed class WasapiAudioCaptureSourceFactory : IAudioCaptureSourceFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public WasapiAudioCaptureSourceFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IAudioCaptureSource Create(AudioEndpointKind kind, string? deviceId)
    {
        if (kind == AudioEndpointKind.SystemOutput && (string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDeviceIds.DefaultDevice))
        {
            return new AllOutputsAudioCaptureSource(
                WasapiAudioDeviceCatalog.GetActivePlaybackDevices,
                id => new WasapiAudioCaptureSource(kind, id, _loggerFactory.CreateLogger<WasapiAudioCaptureSource>(), synthesizeSilence: false),
                _loggerFactory.CreateLogger<AllOutputsAudioCaptureSource>());
        }

        return new WasapiAudioCaptureSource(kind, deviceId, _loggerFactory.CreateLogger<WasapiAudioCaptureSource>());
    }
}

/// <summary>
/// Captures the system audio mix (WASAPI loopback) or a microphone and converts it to 16 kHz mono.
/// </summary>
internal sealed class WasapiAudioCaptureSource : IAudioCaptureSource
{
    private const int BufferMilliseconds = 100;
    private static readonly TimeSpan SilenceCheckInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SilenceGap = TimeSpan.FromMilliseconds(250);

    private readonly AudioEndpointKind _kind;
    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private readonly bool _synthesizeSilence;

    public WasapiAudioCaptureSource(AudioEndpointKind kind, string? deviceId, ILogger<WasapiAudioCaptureSource> logger, bool synthesizeSilence = true)
    {
        _kind = kind;
        _deviceId = deviceId;
        _logger = logger;
        _synthesizeSilence = synthesizeSilence;
    }

    public async IAsyncEnumerable<float[]> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        using var enumerator = new MMDeviceEnumerator();
        using var device = ResolveDevice(enumerator);
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(BufferMilliseconds)
            .WithMmcssThreadPriority("Audio");
        if (_kind == AudioEndpointKind.SystemOutput)
        {
            builder = builder.WithLoopbackCapture();
        }

        await using var recorder = builder.Build();
        var resampler = new MonoResampler(recorder.WaveFormat, AudioConstants.SampleRate);
        var activity = new CaptureActivity();
        _logger.LogInformation("Capturing {Kind} from '{Device}' ({Format}).", _kind, recorder.DeviceFriendlyName, recorder.WaveFormat);

        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpAsync(recorder, resampler, activity, channel.Writer, pumpCancellation.Token);

        // Loopback capture delivers no packets while nothing is playing, so silence is synthesized
        // to let the speech segmenter notice that a speaker has stopped.
        using var silenceTimer = _kind == AudioEndpointKind.SystemOutput && _synthesizeSilence
            ? new Timer(_ => activity.FillSilenceIfIdle(channel.Writer), null, SilenceCheckInterval, SilenceCheckInterval)
            : null;

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            await pumpCancellation.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        WasapiRecorder recorder,
        MonoResampler resampler,
        CaptureActivity activity,
        ChannelWriter<float[]> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var buffer in recorder.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                var samples = resampler.Process(buffer.Data.Span);
                activity.MarkData();
                if (samples.Length > 0)
                {
                    writer.TryWrite(samples);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator)
    {
        var flow = _kind == AudioEndpointKind.SystemOutput ? DataFlow.Render : DataFlow.Capture;
        if (_deviceId == AudioDeviceIds.DefaultCommunicationsDevice)
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }

        if (!string.IsNullOrWhiteSpace(_deviceId) && _deviceId != AudioDeviceIds.DefaultDevice)
        {
            try
            {
                var device = enumerator.GetDevice(_deviceId);
                if (device.State == DeviceState.Active && device.DataFlow == flow)
                {
                    return device;
                }

                device.Dispose();
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("The selected audio device is unavailable. Reconnect it or choose Automatic — all Windows outputs in Settings.", ex);
            }

            throw new InvalidOperationException("The selected audio device is disconnected or has the wrong direction. Reconnect it or select another device in Settings.");
        }

        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    private sealed class CaptureActivity
    {
        private static readonly int SilenceChunkSamples = (int)(AudioConstants.SampleRate * SilenceCheckInterval.TotalSeconds);
        private long _lastDataTimestamp = Stopwatch.GetTimestamp();

        public void MarkData() => Interlocked.Exchange(ref _lastDataTimestamp, Stopwatch.GetTimestamp());

        public void FillSilenceIfIdle(ChannelWriter<float[]> writer)
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastDataTimestamp)) >= SilenceGap)
            {
                writer.TryWrite(new float[SilenceChunkSamples]);
            }
        }
    }
}
