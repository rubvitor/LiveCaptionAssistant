using System.Collections.Concurrent;
using System.Text;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Captioning;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Core.Assistant;

public enum AnswerStatus
{
    Pending,
    Streaming,
    Completed,
    Failed,
    Cancelled,
}

public sealed record AnswerSnapshot(
    Guid Id,
    string Question,
    string ModelId,
    string? ReasoningEffort,
    bool IsAutomatic,
    DateTimeOffset CreatedAt,
    AnswerStatus Status,
    string Text,
    string? ErrorMessage = null,
    string? ErrorCode = null,
    TimeSpan? Duration = null,
    ResponseSpeed Speed = ResponseSpeed.Standard,
    string? AppliedServiceTier = null);

/// <summary>
/// Answers questions with the selected ChatGPT model, using the live transcript as context.
/// Questions asked by the other participants are answered automatically when enabled.
/// </summary>
public sealed class AssistantService : IDisposable
{
    public const string AuthenticationRequiredCode = "authentication_required";

    private const int MaxContextSegments = 40;
    private const int MaxContextCharacters = 6_000;
    private static readonly TimeSpan LatestSpeechWindow = TimeSpan.FromMinutes(2);

    private readonly IChatResponseClient _client;
    private readonly ConversationTranscript _transcript;
    private readonly ISettingsService _settings;
    private readonly CaptionSession _session;
    private readonly IChatGptAuthenticator _authenticator;
    private readonly QuestionDebouncer _debouncer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AssistantService> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public AssistantService(
        IChatResponseClient client,
        ConversationTranscript transcript,
        ISettingsService settings,
        CaptionSession session,
        IChatGptAuthenticator authenticator,
        TimeProvider timeProvider,
        ILogger<AssistantService> logger)
    {
        _client = client;
        _transcript = transcript;
        _settings = settings;
        _session = session;
        _authenticator = authenticator;
        _timeProvider = timeProvider;
        _logger = logger;
        _debouncer = new QuestionDebouncer(timeProvider);

        _session.SegmentFinalized += OnSegmentFinalized;
        _session.SpeechActivityChanged += OnSpeechActivityChanged;
        _debouncer.QuestionReady += OnQuestionReady;
    }

    public event EventHandler<AnswerSnapshot>? AnswerChanged;

    /// <summary>Answers <paramref name="question"/>. Never throws; failures are reported through <see cref="AnswerChanged"/>.</summary>
    public async Task AskAsync(string question, SpokenLanguage? questionLanguage, bool isAutomatic)
    {
        question = question.Trim();
        if (question.Length == 0)
        {
            return;
        }

        var settings = _settings.Current;
        var effort = settings.GetEffectiveReasoningEffort();
        var snapshot = new AnswerSnapshot(
            Guid.NewGuid(), question, settings.ChatModelId ?? string.Empty, effort?.Value, isAutomatic,
            _timeProvider.GetUtcNow(), AnswerStatus.Pending, string.Empty, Speed: settings.ChatResponseSpeed);
        Publish(snapshot);

        if (string.IsNullOrWhiteSpace(settings.ChatModelId))
        {
            Publish(snapshot with { Status = AnswerStatus.Failed, ErrorMessage = "Select a ChatGPT model first." });
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _running[snapshot.Id] = cancellation;
        var startedAt = _timeProvider.GetTimestamp();
        var text = new StringBuilder();
        try
        {
            var context = _transcript.GetRecentFinal(MaxContextSegments, MaxContextCharacters);
            var preferences = new AnswerPreferences(settings.AnswerLanguage, settings.AnswerStyle, settings.UserBackground);
            var prompt = AnswerPromptBuilder.Build(question, questionLanguage, context, preferences);
            var request = new ChatRequest(settings.ChatModelId, effort, prompt.Instructions, prompt.Input, settings.ChatResponseSpeed);

            await foreach (var update in _client.StreamResponseAsync(request, cancellation.Token).ConfigureAwait(false))
            {
                if (update.ServiceTier is not null)
                {
                    snapshot = snapshot with { AppliedServiceTier = update.ServiceTier };
                }

                if (!string.IsNullOrEmpty(update.TextDelta))
                {
                    text.Append(update.TextDelta);
                    Publish(snapshot with { Status = AnswerStatus.Streaming, Text = text.ToString() });
                }
            }

            Publish(snapshot with { Status = AnswerStatus.Completed, Text = text.ToString(), Duration = _timeProvider.GetElapsedTime(startedAt) });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Publish(snapshot with { Status = AnswerStatus.Cancelled, Text = text.ToString() });
        }
        catch (ChatServiceException ex)
        {
            _logger.LogWarning(ex, "Answer failed with code {ErrorCode} (status {StatusCode}, request {RequestId}).", ex.ErrorCode, ex.StatusCode, ex.RequestId);
            Publish(snapshot with { Status = AnswerStatus.Failed, Text = text.ToString(), ErrorMessage = ex.Message, ErrorCode = ex.ErrorCode });
        }
        catch (AuthenticationRequiredException ex)
        {
            Publish(snapshot with { Status = AnswerStatus.Failed, ErrorMessage = ex.Message, ErrorCode = AuthenticationRequiredCode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Answer failed unexpectedly.");
            Publish(snapshot with { Status = AnswerStatus.Failed, Text = text.ToString(), ErrorMessage = $"Unexpected error: {ex.Message}" });
        }
        finally
        {
            _running.TryRemove(snapshot.Id, out _);
        }
    }

    /// <summary>Answers the most recent speech from the other participants.</summary>
    /// <returns><see langword="false"/> when there is no recent speech to answer.</returns>
    public bool AskAboutLatestSpeech()
    {
        var since = _timeProvider.GetUtcNow() - LatestSpeechWindow;
        var recent = _transcript.GetLatestFinal(Speaker.Them, since, maxSegments: 2);
        if (recent.Count == 0)
        {
            return false;
        }

        _debouncer.Reset();
        var question = string.Join(' ', recent.Select(segment => segment.Text));
        _ = AskAsync(question, recent[^1].Language, isAutomatic: false);
        return true;
    }

    public void Cancel(Guid answerId)
    {
        if (_running.TryGetValue(answerId, out var cancellation))
        {
            TryCancel(cancellation);
        }
    }

    public void CancelAll()
    {
        _debouncer.Reset();
        foreach (var cancellation in _running.Values)
        {
            TryCancel(cancellation);
        }
    }

    public void Dispose()
    {
        _session.SegmentFinalized -= OnSegmentFinalized;
        _session.SpeechActivityChanged -= OnSpeechActivityChanged;
        _debouncer.QuestionReady -= OnQuestionReady;
        _debouncer.Dispose();
        CancelAll();
    }

    private void OnSegmentFinalized(object? sender, TranscriptSegment segment)
    {
        if (CanAnswerAutomatically())
        {
            _debouncer.OnSegmentFinalized(segment);
        }
    }

    private void OnSpeechActivityChanged(object? sender, SpeechActivity activity) =>
        _debouncer.OnSpeechActivity(activity.Speaker, activity.IsActive);

    private void OnQuestionReady(object? sender, PendingQuestion question)
    {
        if (CanAnswerAutomatically())
        {
            _ = AskAsync(question.Text, question.Language, isAutomatic: true);
        }
    }

    private bool CanAnswerAutomatically() =>
        _settings.Current.AutoAnswerQuestions && _authenticator.CurrentAccount is { PlanUsageEnabled: true };

    private void Publish(AnswerSnapshot snapshot) => AnswerChanged?.Invoke(this, snapshot);

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The answer finished while it was being cancelled.
        }
    }
}
