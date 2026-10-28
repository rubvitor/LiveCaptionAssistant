using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class TranscriptionSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Final_jobs_run_first_and_only_the_latest_partial_per_speaker_runs()
    {
        var harness = new Harness(expectedJobs: 4);
        await using var scheduler = harness.CreateScheduler();
        var blocker = Job(Speaker.Them, isPartial: false);
        scheduler.Enqueue(blocker);
        await harness.FirstJobStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var segment = Guid.NewGuid();
        var stalePartial = Job(Speaker.Them, isPartial: true, segment);
        var latestPartial = Job(Speaker.Them, isPartial: true, segment);
        var finalJob = Job(Speaker.Me, isPartial: false);
        var otherPartial = Job(Speaker.Me, isPartial: true);
        scheduler.Enqueue(stalePartial);
        scheduler.Enqueue(latestPartial);
        scheduler.Enqueue(finalJob);
        scheduler.Enqueue(otherPartial);
        harness.ReleaseFirstJob();
        await harness.AllJobsProcessed.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(blocker, harness.Processed[0]);
        Assert.Same(finalJob, harness.Processed[1]);
        Assert.Contains(harness.Processed, job => ReferenceEquals(job, latestPartial));
        Assert.Contains(harness.Processed, job => ReferenceEquals(job, otherPartial));
        Assert.DoesNotContain(harness.Processed, job => ReferenceEquals(job, stalePartial));
    }

    [Fact]
    public async Task Partial_jobs_for_a_finalized_segment_are_ignored()
    {
        var harness = new Harness(expectedJobs: 3);
        await using var scheduler = harness.CreateScheduler();
        scheduler.Enqueue(Job(Speaker.Them, isPartial: false));
        await harness.FirstJobStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var segment = Guid.NewGuid();
        scheduler.Enqueue(Job(Speaker.Them, isPartial: false, segment));
        scheduler.Enqueue(Job(Speaker.Them, isPartial: true, segment));
        scheduler.Enqueue(Job(Speaker.Me, isPartial: true));
        harness.ReleaseFirstJob();
        await harness.AllJobsProcessed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Processed.Count);
        Assert.DoesNotContain(harness.Processed, job => job.IsPartial && job.SegmentId == segment);
    }

    [Fact]
    public async Task Drain_finishes_queued_final_jobs_and_drops_partial_ones()
    {
        var harness = new Harness(expectedJobs: 3);
        await using var scheduler = harness.CreateScheduler();
        scheduler.Enqueue(Job(Speaker.Them, isPartial: false));
        await harness.FirstJobStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        scheduler.Enqueue(Job(Speaker.Them, isPartial: false));
        scheduler.Enqueue(Job(Speaker.Me, isPartial: false));
        scheduler.Enqueue(Job(Speaker.Them, isPartial: true));

        var drain = scheduler.DrainAsync(Timeout);
        harness.ReleaseFirstJob();
        await drain.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Processed.Count);
        Assert.All(harness.Processed, job => Assert.False(job.IsPartial));
    }

    private static TranscriptionJob Job(Speaker speaker, bool isPartial, Guid? segment = null) =>
        new(segment ?? Guid.NewGuid(), speaker, new float[1], isPartial);

    private sealed class Harness(int expectedJobs)
    {
        private readonly TaskCompletionSource _firstJobStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstJob = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allJobsProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TranscriptionJob> Processed { get; } = [];

        public Task FirstJobStarted => _firstJobStarted.Task;

        public Task AllJobsProcessed => _allJobsProcessed.Task;

        public void ReleaseFirstJob() => _releaseFirstJob.SetResult();

        public TranscriptionScheduler CreateScheduler() => new(ProcessAsync, NullLogger<TranscriptionScheduler>.Instance);

        private async Task ProcessAsync(TranscriptionJob job, CancellationToken cancellationToken)
        {
            Processed.Add(job);
            if (Processed.Count == 1)
            {
                _firstJobStarted.SetResult();
                await _releaseFirstJob.Task.WaitAsync(cancellationToken);
            }

            if (Processed.Count == expectedJobs)
            {
                _allJobsProcessed.TrySetResult();
            }
        }
    }
}
