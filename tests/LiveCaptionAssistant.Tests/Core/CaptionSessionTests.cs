using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Captioning;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class CaptionSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Audio_diagnostics_report_silence_and_signal_recovery_without_transcribing_silent_samples()
    {
        var clock = new FakeTimeProvider();
        var recognitions = 0;
        await using var harness = new Harness((_, _) =>
        {
            Interlocked.Increment(ref recognitions);
            return Task.FromResult(new SpeechRecognitionResult("Unexpected speech", SpokenLanguage.English, 1f, 0f));
        }, clock);
        var initial = Completion<AudioSignalStatus>();
        var silent = Completion<AudioSignalStatus>();
        var recovered = Completion<AudioSignalStatus>();
        harness.Session.AudioSignalChanged += (_, status) =>
        {
            initial.TrySetResult(status);
            if (status.IsSilent)
            {
                silent.TrySetResult(status);
            }

            if (status.HasReceivedSignal)
            {
                recovered.TrySetResult(status);
            }
        };
        await harness.StartAsync();
        harness.Source.Feed(TestAudio.Silence(1));
        await initial.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(10));
        harness.Source.Feed(TestAudio.Silence(1));
        var missing = await silent.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(AudioEndpointKind.SystemOutput, missing.Kind);
        Assert.False(missing.HasReceivedSignal);
        Assert.Equal(0f, missing.Peak);
        Assert.Empty(harness.Transcript.GetSnapshot());
        Assert.Equal(0, Volatile.Read(ref recognitions));

        clock.Advance(TimeSpan.FromMilliseconds(250));
        harness.Source.Feed(TestAudio.Tone(0.1));
        var signal = await recovered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(signal.IsSilent);
        Assert.InRange(signal.Peak, 0.29f, 0.31f);
        Assert.Equal(TimeSpan.Zero, signal.SilenceDuration);
    }

    [Theory]
    [InlineData(SpokenLanguage.English, "Can you explain dependency injection in software development?")]
    [InlineData(SpokenLanguage.Portuguese, "Você pode explicar injeção de dependências no desenvolvimento de software?")]
    [InlineData(SpokenLanguage.Portuguese, "Você pode explicar injeção de dependências no desenvolvimento de software.")]
    public async Task System_speech_is_finalized_with_detected_language_and_a_technical_question(SpokenLanguage language, string text)
    {
        SpeechRecognitionRequest? recognizedRequest = null;
        await using var harness = new Harness((request, _) =>
        {
            recognizedRequest = request;
            return Task.FromResult(new SpeechRecognitionResult(text, language, 0.99f, 0f));
        });
        var finalized = Completion<TranscriptSegment>();
        harness.Session.SegmentFinalized += (_, segment) => finalized.TrySetResult(segment);
        await harness.StartAsync();

        harness.FeedUtterance();
        var segment = await finalized.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.NotNull(recognizedRequest);
        Assert.Null(recognizedRequest.ForcedLanguage);
        Assert.Equal(Speaker.Them, segment.Speaker);
        Assert.Equal(language, segment.Language);
        Assert.Equal(language, harness.Session.CurrentLanguage);
        Assert.Equal(text, segment.Text);
        Assert.True(segment.IsFinal);
        Assert.True(segment.IsQuestion);
        Assert.Equal(segment, Assert.Single(harness.Transcript.GetSnapshot()));
    }

    [Fact]
    public async Task Recognition_failure_is_reported_and_the_next_utterance_can_still_be_transcribed()
    {
        var attempts = 0;
        await using var harness = new Harness((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("Speech backend unavailable.");
            }

            return Task.FromResult(new SpeechRecognitionResult("How would you test this service?", SpokenLanguage.English, 0.99f, 0f));
        });
        var failure = Completion<string>();
        var finalized = Completion<TranscriptSegment>();
        harness.Session.ErrorOccurred += (_, error) => failure.TrySetResult(error);
        harness.Session.SegmentFinalized += (_, segment) => finalized.TrySetResult(segment);
        await harness.StartAsync();

        harness.FeedUtterance();
        var error = await failure.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Contains("Speech backend unavailable.", error, StringComparison.Ordinal);
        Assert.Empty(harness.Transcript.GetSnapshot());

        harness.FeedUtterance();
        var recovered = await finalized.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(CaptionSessionState.Running, harness.Session.State);
        Assert.Equal("How would you test this service?", recovered.Text);
        Assert.Equal(recovered, Assert.Single(harness.Transcript.GetSnapshot()));
    }

    [Fact]
    public async Task Failed_final_recognition_removes_the_stale_partial_caption_and_resets_speech_activity()
    {
        var attempts = 0;
        await using var harness = new Harness((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                throw new InvalidOperationException("Final recognition failed.");
            }

            return Task.FromResult(new SpeechRecognitionResult("Can you explain", SpokenLanguage.English, 0.99f, 0f));
        });
        var partial = Completion<TranscriptSegment>();
        var failure = Completion<string>();
        var inactive = Completion<SpeechActivity>();
        harness.Transcript.Changed += (_, change) =>
        {
            if (change.Segment is { IsFinal: false } segment && change.Kind == TranscriptChangeKind.Added)
            {
                partial.TrySetResult(segment);
            }
        };
        harness.Session.ErrorOccurred += (_, error) => failure.TrySetResult(error);
        harness.Session.SpeechActivityChanged += (_, activity) =>
        {
            if (!activity.IsActive)
            {
                inactive.TrySetResult(activity);
            }
        };
        await harness.StartAsync(livePartials: true);

        harness.Source.Feed(TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.SpeechLike(1.2)));
        await partial.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(harness.Transcript.GetSnapshot()).IsFinal);

        harness.Source.Feed(TestAudio.Silence(1));
        await failure.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await inactive.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(harness.Transcript.GetSnapshot());
    }

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Func<SpeechRecognitionRequest, CancellationToken, Task<SpeechRecognitionResult>> recognize, TimeProvider? clock = null)
        {
            Session = new CaptionSession(
                Source, new SpeechEngine(recognize), Transcript, new HeuristicQuestionDetector(), clock ?? TimeProvider.System,
                NullLogger<CaptionSession>.Instance, NullLogger<TranscriptionScheduler>.Instance);
        }

        public CaptureSource Source { get; } = new();

        public ConversationTranscript Transcript { get; } = new();

        public CaptionSession Session { get; }

        public Task StartAsync(bool livePartials = false) => Session.StartAsync(
            new CaptionSessionOptions(LanguageMode.AutoDetect, AudioDeviceIds.DefaultDevice, false, AudioDeviceIds.DefaultCommunicationsDevice, livePartials),
            TestContext.Current.CancellationToken);

        public void FeedUtterance() => Source.Feed(TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.SpeechLike(2), TestAudio.Silence(1)));

        public async ValueTask DisposeAsync()
        {
            await Session.StopAsync();
            Session.Dispose();
        }
    }

    private sealed class CaptureSource : IAudioCaptureSource, IAudioCaptureSourceFactory
    {
        private readonly Channel<float[]> _chunks = Channel.CreateUnbounded<float[]>();

        public void Feed(float[] samples) => _chunks.Writer.TryWrite(samples);

        public IAudioCaptureSource Create(AudioEndpointKind kind, string? deviceId) => this;

        public async IAsyncEnumerable<float[]> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private sealed class SpeechEngine(Func<SpeechRecognitionRequest, CancellationToken, Task<SpeechRecognitionResult>> recognize) : ISpeechToTextEngine
    {
        public bool IsReady => true;

        public string ActiveModelDescription => "Test speech engine";

        public Task EnsureReadyAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SpeechRecognitionResult> RecognizeAsync(SpeechRecognitionRequest request, CancellationToken cancellationToken) => recognize(request, cancellationToken);
    }
}
