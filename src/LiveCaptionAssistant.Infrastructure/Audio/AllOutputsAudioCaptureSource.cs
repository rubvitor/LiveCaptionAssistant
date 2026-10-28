using System.Runtime.CompilerServices;
using LiveCaptionAssistant.Core.Audio;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Infrastructure.Audio;

/// <summary>
/// Captures every active Windows playback endpoint, including endpoints connected after Run.
/// A clocked mix preserves real time when one endpoint is silent, fails, or changes its route.
/// </summary>
internal sealed class AllOutputsAudioCaptureSource : IAudioCaptureSource
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _getDevices;
    private readonly Func<string, IAudioCaptureSource> _createSource;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public AllOutputsAudioCaptureSource(
        Func<IReadOnlyList<AudioDeviceInfo>> getDevices,
        Func<string, IAudioCaptureSource> createSource,
        ILogger<AllOutputsAudioCaptureSource> logger,
        TimeProvider? timeProvider = null)
    {
        _getDevices = getDevices;
        _createSource = createSource;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<float[]> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = new PeriodicTimer(FrameDuration, _timeProvider);
        var mixer = new ClockedAudioMixer();
        var workers = new Dictionary<string, CaptureWorker>(StringComparer.Ordinal);
        var origin = _timeProvider.GetTimestamp();
        var nextRefresh = TimeSpan.Zero;
        long lastFrame = -1;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var elapsed = _timeProvider.GetElapsedTime(origin);
                if (elapsed >= nextRefresh)
                {
                    await RefreshAsync(workers, mixer, origin, stopping.Token).ConfigureAwait(false);
                    nextRefresh = elapsed + RefreshInterval;
                }

                if (!await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                {
                    break;
                }

                // If the consumer stalls, skip old frames instead of accumulating minutes of lag.
                var frame = (_timeProvider.GetElapsedTime(origin).Ticks / FrameDuration.Ticks) - 1;
                if (frame <= lastFrame)
                {
                    continue;
                }

                lastFrame = frame;
                yield return mixer.ReadFrame((frame * ClockedAudioMixer.FrameSamples) - ClockedAudioMixer.LatencySamples);
            }
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            foreach (var worker in workers.Values)
            {
                await worker.Task.ConfigureAwait(false);
                worker.Cancellation.Dispose();
            }
        }
    }

    private async Task RefreshAsync(Dictionary<string, CaptureWorker> workers, ClockedAudioMixer mixer, long origin, CancellationToken cancellationToken)
    {
        IReadOnlyList<AudioDeviceInfo> devices;
        try
        {
            devices = _getDevices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh playback endpoints; keeping existing captures.");
            return;
        }

        var activeIds = devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, worker) in workers.ToArray())
        {
            if (!activeIds.Contains(id) || worker.Task.IsCompleted)
            {
                await worker.Cancellation.CancelAsync().ConfigureAwait(false);
                await worker.Task.ConfigureAwait(false);
                worker.Cancellation.Dispose();
                workers.Remove(id);
                mixer.RemoveInput(id);
            }
        }

        foreach (var device in devices)
        {
            if (workers.ContainsKey(device.Id))
            {
                continue;
            }

            mixer.AddInput(device.Id);
            var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = Task.Run(() => CaptureEndpointAsync(device, mixer, origin, workerCancellation.Token), CancellationToken.None);
            workers.Add(device.Id, new CaptureWorker(task, workerCancellation));
        }
    }

    private async Task CaptureEndpointAsync(AudioDeviceInfo device, ClockedAudioMixer mixer, long origin, CancellationToken cancellationToken)
    {
        try
        {
            var source = _createSource(device.Id);
            await foreach (var samples in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                var endPosition = _timeProvider.GetElapsedTime(origin).Ticks * AudioConstants.SampleRate / TimeSpan.TicksPerSecond;
                mixer.Write(device.Id, samples, endPosition);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Playback capture from '{Device}' ended; it will be retried.", device.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown or disconnected endpoint.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playback capture from '{Device}' failed; other outputs continue and this endpoint will be retried.", device.Name);
        }
    }

    private sealed record CaptureWorker(Task Task, CancellationTokenSource Cancellation);
}
