using System.Diagnostics.CodeAnalysis;
using LiveCaptionAssistant.Core.Conversation;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Core.Transcription;

public sealed record TranscriptionJob(Guid SegmentId, Speaker Speaker, float[] Samples, bool IsPartial);

/// <summary>
/// Runs transcription jobs one at a time (the speech engine is a single GPU/CPU resource).
/// Final jobs always run before partial ones, and only the most recent partial job per speaker is kept.
/// </summary>
public sealed class TranscriptionScheduler : IAsyncDisposable
{
    private readonly Func<TranscriptionJob, CancellationToken, Task> _processJob;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Queue<TranscriptionJob> _finalJobs = new();
    private readonly Dictionary<Speaker, TranscriptionJob> _partialJobs = [];
    private readonly HashSet<Guid> _finalizedSegments = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private bool _acceptingJobs = true;

    public TranscriptionScheduler(Func<TranscriptionJob, CancellationToken, Task> processJob, ILogger<TranscriptionScheduler> logger)
    {
        _processJob = processJob;
        _logger = logger;
        _worker = Task.Run(RunAsync);
    }

    public void Enqueue(TranscriptionJob job)
    {
        lock (_gate)
        {
            if (!_acceptingJobs)
            {
                return;
            }

            if (job.IsPartial)
            {
                if (_finalizedSegments.Contains(job.SegmentId))
                {
                    return;
                }

                _partialJobs[job.Speaker] = job;
            }
            else
            {
                _finalizedSegments.Add(job.SegmentId);
                if (_partialJobs.TryGetValue(job.Speaker, out var pending) && pending.SegmentId == job.SegmentId)
                {
                    _partialJobs.Remove(job.Speaker);
                }

                _finalJobs.Enqueue(job);
            }
        }

        _signal.Release();
    }

    /// <summary>
    /// Stops accepting jobs, drops pending partial jobs and waits for queued final jobs to finish.
    /// Remaining work is cancelled once <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            _acceptingJobs = false;
            _partialJobs.Clear();
        }

        _signal.Release();
        try
        {
            await _worker.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Transcription queue did not drain within {Timeout}; cancelling remaining work.", timeout);
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _acceptingJobs = false;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _signal.Release();
        await _worker.ConfigureAwait(false);
        _stopping.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync()
    {
        var cancellationToken = _stopping.Token;
        while (true)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (TryTakeNext(out var job))
            {
                try
                {
                    await _processJob(job, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transcription of segment {SegmentId} failed.", job.SegmentId);
                }
            }

            if (IsDrained())
            {
                return;
            }
        }
    }

    private bool TryTakeNext([NotNullWhen(true)] out TranscriptionJob? job)
    {
        lock (_gate)
        {
            if (_finalJobs.TryDequeue(out job))
            {
                return true;
            }

            foreach (var (speaker, partial) in _partialJobs)
            {
                _partialJobs.Remove(speaker);
                job = partial;
                return true;
            }

            job = null;
            return false;
        }
    }

    private bool IsDrained()
    {
        lock (_gate)
        {
            return !_acceptingJobs && _finalJobs.Count == 0;
        }
    }
}
