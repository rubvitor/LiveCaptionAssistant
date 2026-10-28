using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Captioning;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class AssistantServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_nontechnical_question_automatically_streams_an_answer_after_the_quiet_period()
    {
        await using var harness = new Harness("What do you enjoy outside work?");
        await harness.CaptureUtteranceAsync();
        Assert.Empty(harness.Client.Requests);

        harness.Clock.Advance(QuestionDebouncer.DefaultQuietPeriod);
        var answer = await harness.TerminalAnswer.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(answer.IsAutomatic);
        Assert.Equal("What do you enjoy outside work?", answer.Question);
        Assert.Equal(AnswerStatus.Completed, answer.Status);
        Assert.Equal("I enjoy hiking.", answer.Text);
        Assert.Single(harness.Client.Requests);
        Assert.Contains(harness.Answers, snapshot => snapshot.Status == AnswerStatus.Pending);
        Assert.Contains(harness.Answers, snapshot => snapshot.Status == AnswerStatus.Streaming && snapshot.Text == "I enjoy ");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manual_answer_uses_the_latest_unrecognized_prompt_even_when_auto_answer_is_disabled(bool autoAnswer)
    {
        await using var harness = new Harness("Your approach to planning a project.", autoAnswer);
        await harness.CaptureUtteranceAsync();
        Assert.False(Assert.Single(harness.Transcript.GetSnapshot()).IsQuestion);
        harness.Clock.Advance(QuestionDebouncer.DefaultQuietPeriod);
        Assert.Empty(harness.Client.Requests);

        Assert.True(harness.Assistant.AskAboutLatestSpeech());
        var answer = await harness.TerminalAnswer.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.False(answer.IsAutomatic);
        Assert.Equal("Your approach to planning a project.", answer.Question);
        Assert.Equal(AnswerStatus.Completed, answer.Status);
        Assert.Equal("I enjoy hiking.", answer.Text);
        Assert.Single(harness.Client.Requests);
    }

    [Fact]
    public async Task Manual_answer_returns_false_when_there_is_no_recent_speech_from_other_participants()
    {
        await using var harness = new Harness("Unused");
        var now = harness.Clock.GetUtcNow();
        harness.Transcript.Upsert(new TranscriptSegment(Guid.NewGuid(), Speaker.Me, now, "My own speech", SpokenLanguage.English, true, false));
        harness.Transcript.Upsert(new TranscriptSegment(Guid.NewGuid(), Speaker.Them, now.AddMinutes(-3), "An old question?", SpokenLanguage.English, true, true));

        Assert.False(harness.Assistant.AskAboutLatestSpeech());
        Assert.Empty(harness.Client.Requests);
        Assert.Empty(harness.Answers);
    }

    [Fact]
    public async Task Streaming_failure_publishes_the_error_and_preserves_text_already_received_for_the_answer_panel()
    {
        await using var harness = new Harness("How do you approach a deadline?", streamFailure: new ChatServiceException(
            "Plan usage limit reached.", ChatServiceException.UsageLimitExceededCode, 429));

        await harness.Assistant.AskAsync("How do you approach a deadline?", SpokenLanguage.English, isAutomatic: false);
        var answer = await harness.TerminalAnswer.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(AnswerStatus.Failed, answer.Status);
        Assert.Equal("I enjoy ", answer.Text);
        Assert.Equal("Plan usage limit reached.", answer.ErrorMessage);
        Assert.Equal(ChatServiceException.UsageLimitExceededCode, answer.ErrorCode);
        Assert.Contains(harness.Answers, snapshot => snapshot.Status == AnswerStatus.Streaming);
    }

    [Theory]
    [InlineData(ResponseSpeed.Standard)]
    [InlineData(ResponseSpeed.Fast)]
    public async Task Selected_response_speed_reaches_the_client_and_answer_card(ResponseSpeed speed)
    {
        await using var harness = new Harness("Unused", speed: speed);

        await harness.Assistant.AskAsync("What is dependency injection?", SpokenLanguage.English, isAutomatic: false);
        var answer = await harness.TerminalAnswer.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(speed, Assert.Single(harness.Client.Requests).Speed);
        Assert.Equal(speed, answer.Speed);
        Assert.Equal("default", answer.AppliedServiceTier);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _speechIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(string recognizedText, bool autoAnswer = true, Exception? streamFailure = null, ResponseSpeed speed = ResponseSpeed.Standard)
        {
            Client = new RecordingChatClient(streamFailure);
            Session = new CaptionSession(Source, new SpeechEngine(recognizedText), Transcript, new HeuristicQuestionDetector(), Clock,
                NullLogger<CaptionSession>.Instance, NullLogger<TranscriptionScheduler>.Instance);
            Assistant = new AssistantService(Client, Transcript, new TestSettings(autoAnswer, speed), Session, new TestAuthenticator(), Clock,
                NullLogger<AssistantService>.Instance);
            Assistant.AnswerChanged += (_, answer) =>
            {
                Answers.Enqueue(answer);
                if (answer.Status is AnswerStatus.Completed or AnswerStatus.Failed or AnswerStatus.Cancelled)
                {
                    TerminalAnswer.TrySetResult(answer);
                }
            };
            Session.SpeechActivityChanged += (_, activity) =>
            {
                if (!activity.IsActive)
                {
                    _speechIdle.TrySetResult();
                }
            };
        }

        public FakeTimeProvider Clock { get; } = new();

        public CaptureSource Source { get; } = new();

        public ConversationTranscript Transcript { get; } = new();

        public CaptionSession Session { get; }

        public AssistantService Assistant { get; }

        public RecordingChatClient Client { get; }

        public ConcurrentQueue<AnswerSnapshot> Answers { get; } = new();

        public TaskCompletionSource<AnswerSnapshot> TerminalAnswer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CaptureUtteranceAsync()
        {
            await Session.StartAsync(new CaptionSessionOptions(LanguageMode.AutoDetect, AudioDeviceIds.DefaultDevice, false,
                AudioDeviceIds.DefaultCommunicationsDevice, false), TestContext.Current.CancellationToken);
            Source.Feed(TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.SpeechLike(2), TestAudio.Silence(1)));
            await _speechIdle.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Assistant.Dispose();
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

    private sealed class SpeechEngine(string text) : ISpeechToTextEngine
    {
        public bool IsReady => true;

        public string ActiveModelDescription => "Test speech engine";

        public Task EnsureReadyAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SpeechRecognitionResult> RecognizeAsync(SpeechRecognitionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SpeechRecognitionResult(text, SpokenLanguage.English, 1f, 0f));
    }

    private sealed class RecordingChatClient(Exception? streamFailure) : IChatResponseClient
    {
        public ConcurrentQueue<ChatRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate("I enjoy ");
            if (streamFailure is not null)
            {
                throw streamFailure;
            }

            yield return new ChatResponseUpdate("hiking.");
            yield return new ChatResponseUpdate(null, "default");
        }
    }

    private sealed class TestSettings(bool autoAnswer, ResponseSpeed speed) : ISettingsService
    {
        public AppSettings Current { get; } = new() { AutoAnswerQuestions = autoAnswer, ChatModelId = "test-model", ChatResponseSpeed = speed };

        public event EventHandler<AppSettings>? Changed { add { } remove { } }

        public void Update(Func<AppSettings, AppSettings> update) => throw new NotSupportedException();
    }

    private sealed class TestAuthenticator : IChatGptAuthenticator
    {
        public ChatGptAccount CurrentAccount { get; } = new("test-account", null, "Test account", PlanUsageEnabled: true);

        public event EventHandler? AccountChanged { add { } remove { } }

        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChatGptAccount> SignInAsync(bool requestPlanUsageConsent, CancellationToken cancellationToken) => Task.FromResult(CurrentAccount);

        public Task<bool> SignOutAsync(bool forgetAccount, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
