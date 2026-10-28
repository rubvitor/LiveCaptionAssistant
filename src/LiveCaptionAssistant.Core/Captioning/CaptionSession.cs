using System.Collections.Concurrent;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Core.Captioning;

/// <summary>
/// Orchestrates live captioning: audio capture → speech segmentation → transcription → transcript.
/// </summary>
public sealed class CaptionSession : IDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CaptureRestartDelay = TimeSpan.FromSeconds(2);
    private const int PartialIntervalMilliseconds = 1_000;

    private readonly IAudioCaptureSourceFactory _audioSources;
    private readonly ISpeechToTextEngine _engine;
    private readonly ConversationTranscript _transcript;
    private readonly IQuestionDetector _questionDetector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptionSession> _logger;
    private readonly ILogger<TranscriptionScheduler> _schedulerLogger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SegmentContext> _segments = new();
    private readonly Lock _activityGate = new();
    private readonly Dictionary<Speaker, int> _activeSegmentCounts = [];

    private CancellationTokenSource? _runCancellation;
    private TranscriptionScheduler? _scheduler;
    private Task[] _captureTasks = [];
    private volatile LanguageMode _languageMode = LanguageMode.AutoDetect;
    private volatile SpokenLanguage _currentLanguage = SpokenLanguage.English;
    private volatile bool _livePartialCaptions = true;
    private volatile CaptionSessionState _state = CaptionSessionState.Stopped;

    public CaptionSession(
        IAudioCaptureSourceFactory audioSources,
        ISpeechToTextEngine engine,
        ConversationTranscript transcript,
        IQuestionDetector questionDetector,
        TimeProvider timeProvider,
        ILogger<CaptionSession> logger,
        ILogger<TranscriptionScheduler> schedulerLogger)
    {
        _audioSources = audioSources;
        _engine = engine;
        _transcript = transcript;
        _questionDetector = questionDetector;
        _timeProvider = timeProvider;
        _logger = logger;
        _schedulerLogger = schedulerLogger;
    }

    public event EventHandler<CaptionSessionState>? StateChanged;

    public event EventHandler<SpokenLanguage>? LanguageChanged;

    public event EventHandler<SpeechActivity>? SpeechActivityChanged;

    public event EventHandler<AudioSignalStatus>? AudioSignalChanged;

    /// <summary>Raised after a final transcript segment has been added to the transcript.</summary>
    public event EventHandler<TranscriptSegment>? SegmentFinalized;

    public event EventHandler<ModelLoadProgress>? ModelLoadProgressChanged;

    public event EventHandler<string>? ErrorOccurred;

    public CaptionSessionState State => _state;

    public SpokenLanguage CurrentLanguage => _currentLanguage;

    public LanguageMode LanguageMode => _languageMode;

    public async Task StartAsync(CaptionSessionOptions options, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != CaptionSessionState.Stopped)
            {
                return;
            }

            SetState(CaptionSessionState.Starting);
            SetLanguageMode(options.LanguageMode);
            _livePartialCaptions = options.LivePartialCaptions;

            var progress = new InlineProgress<ModelLoadProgress>(value => ModelLoadProgressChanged?.Invoke(this, value));
            await _engine.EnsureReadyAsync(progress, cancellationToken).ConfigureAwait(false);

            _runCancellation = new CancellationTokenSource();
            _scheduler = new TranscriptionScheduler(ProcessJobAsync, _schedulerLogger);

            var token = _runCancellation.Token;
            var captureTasks = new List<Task>
            {
                Task.Run(() => RunCaptureLoopAsync(AudioEndpointKind.SystemOutput, options.SystemAudioDeviceId, Speaker.Them, token), CancellationToken.None),
            };

            if (options.CaptureMicrophone)
            {
                captureTasks.Add(Task.Run(() => RunCaptureLoopAsync(AudioEndpointKind.Microphone, options.MicrophoneDeviceId, Speaker.Me, token), CancellationToken.None));
            }

            _captureTasks = [.. captureTasks];
            SetState(CaptionSessionState.Running);
            _logger.LogInformation("Caption session started (language mode {LanguageMode}, microphone {Microphone}).", options.LanguageMode, options.CaptureMicrophone);
        }
        catch
        {
            await TearDownAsync().ConfigureAwait(false);
            SetState(CaptionSessionState.Stopped);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != CaptionSessionState.Running)
            {
                return;
            }

            SetState(CaptionSessionState.Stopping);
            await TearDownAsync().ConfigureAwait(false);
            SetState(CaptionSessionState.Stopped);
            _logger.LogInformation("Caption session stopped.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void SetLanguageMode(LanguageMode mode)
    {
        _languageMode = mode;
        if (mode.ToFixedLanguage() is { } fixedLanguage)
        {
            UpdateCurrentLanguage(fixedLanguage);
        }
    }

    public void SetLivePartialCaptions(bool enabled) => _livePartialCaptions = enabled;

    public void Dispose()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _lifecycle.Dispose();
    }

    private async Task TearDownAsync()
    {
        if (_runCancellation is not null)
        {
            await _runCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_captureTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A capture loop ended with an error during shutdown.");
        }

        if (_scheduler is not null)
        {
            await _scheduler.DrainAsync(DrainTimeout).ConfigureAwait(false);
            await _scheduler.DisposeAsync().ConfigureAwait(false);
        }

        _scheduler = null;
        _captureTasks = [];
        _runCancellation?.Dispose();
        _runCancellation = null;
        _segments.Clear();
        _transcript.RemoveNonFinal();
        ResetSpeechActivity();
    }

    private async Task RunCaptureLoopAsync(AudioEndpointKind kind, string deviceId, Speaker speaker, CancellationToken cancellationToken)
    {
        var signalMonitor = new AudioSignalMonitor(kind, _timeProvider);
        var wasSilent = false;
        var receivedSignal = false;
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            PartialIntervalMilliseconds = speaker == Speaker.Them ? PartialIntervalMilliseconds : 0,
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var source = _audioSources.Create(kind, deviceId);
                await foreach (var chunk in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (signalMonitor.Process(chunk) is { } signal)
                    {
                        AudioSignalChanged?.Invoke(this, signal);
                        if (signal.IsSilent && !wasSilent)
                        {
                            _logger.LogWarning("No {Kind} signal for {Seconds:F0} seconds (selection {DeviceId}). Check the meeting output and audio settings if someone is speaking.",
                                kind, signal.SilenceDuration.TotalSeconds, deviceId);
                        }
                        else if (signal.HasReceivedSignal && (!receivedSignal || (wasSilent && !signal.IsSilent)))
                        {
                            _logger.LogInformation("{Kind} audio signal received (selection {DeviceId}).", kind, deviceId);
                        }

                        wasSilent = signal.IsSilent;
                        receivedSignal = signal.HasReceivedSignal;
                    }

                    foreach (var segmentEvent in segmenter.Process(chunk))
                    {
                        HandleSegmentEvent(speaker, segmentEvent);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{Kind} capture ended unexpectedly; restarting.", kind);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Kind} capture failed.", kind);
                ErrorOccurred?.Invoke(this, $"Audio capture error ({Describe(kind)}): {ex.Message}");
            }

            try
            {
                await Task.Delay(CaptureRestartDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var segmentEvent in segmenter.Flush())
        {
            HandleSegmentEvent(speaker, segmentEvent);
        }
    }

    private void HandleSegmentEvent(Speaker speaker, SpeechSegmentEvent segmentEvent)
    {
        var scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        switch (segmentEvent)
        {
            case SpeechSegmentStarted started:
                _segments[started.SegmentId] = new SegmentContext(_timeProvider.GetUtcNow());
                ChangeSpeechActivity(speaker, +1);
                break;

            case SpeechSegmentUpdated updated when _livePartialCaptions:
                scheduler.Enqueue(new TranscriptionJob(updated.SegmentId, speaker, updated.Samples, IsPartial: true));
                break;

            case SpeechSegmentCompleted completed:
                scheduler.Enqueue(new TranscriptionJob(completed.SegmentId, speaker, completed.Samples, IsPartial: false));
                break;

            case SpeechSegmentDiscarded discarded:
                if (_segments.TryRemove(discarded.SegmentId, out _))
                {
                    _transcript.Remove(discarded.SegmentId);
                    ChangeSpeechActivity(speaker, -1);
                }

                break;
        }
    }

    private async Task ProcessJobAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (job.IsPartial)
            {
                await ProcessPartialJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ProcessFinalJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!job.IsPartial)
            {
                _transcript.Remove(job.SegmentId);
            }

            ErrorOccurred?.Invoke(this, $"Speech recognition error: {ex.Message}");
            throw;
        }
        finally
        {
            if (!job.IsPartial && _segments.TryRemove(job.SegmentId, out _))
            {
                ChangeSpeechActivity(job.Speaker, -1);
            }
        }
    }

    private async Task ProcessPartialJobAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(job.SegmentId, out var context))
        {
            return;
        }

        var result = await RecognizeAsync(job, ResolveForcedLanguage(job, context), cancellationToken).ConfigureAwait(false);
        var text = HallucinationFilter.Clean(result.Text, result.NoSpeechProbability);
        if (text.Length == 0 || !_segments.ContainsKey(job.SegmentId))
        {
            return;
        }

        context.Language ??= result.Language;
        _transcript.Upsert(new TranscriptSegment(job.SegmentId, job.Speaker, context.StartedAt, text, result.Language, IsFinal: false, IsQuestion: false));
    }

    private async Task ProcessFinalJobAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        var context = _segments.GetValueOrDefault(job.SegmentId) ?? new SegmentContext(_timeProvider.GetUtcNow());
        var forcedLanguage = ResolveForcedLanguage(job, context);
        var result = await RecognizeAsync(job, forcedLanguage, cancellationToken).ConfigureAwait(false);
        var text = HallucinationFilter.Clean(result.Text, result.NoSpeechProbability);
        if (text.Length == 0)
        {
            _transcript.Remove(job.SegmentId);
            return;
        }

        if (forcedLanguage is null && result.LanguageConfidence >= LanguageSelectionPolicy.SessionLanguageConfidenceThreshold)
        {
            UpdateCurrentLanguage(result.Language);
        }

        var isQuestion = job.Speaker == Speaker.Them && _questionDetector.IsQuestion(text);
        var segment = new TranscriptSegment(job.SegmentId, job.Speaker, context.StartedAt, text, result.Language, IsFinal: true, isQuestion);
        _transcript.Upsert(segment);
        SegmentFinalized?.Invoke(this, segment);
    }

    private Task<SpeechRecognitionResult> RecognizeAsync(TranscriptionJob job, SpokenLanguage? forcedLanguage, CancellationToken cancellationToken)
    {
        var request = new SpeechRecognitionRequest(job.Samples, forcedLanguage, _currentLanguage);
        return _engine.RecognizeAsync(request, cancellationToken);
    }

    private SpokenLanguage? ResolveForcedLanguage(TranscriptionJob job, SegmentContext context) =>
        LanguageSelectionPolicy.ResolveForcedLanguage(_languageMode, job.IsPartial, job.Samples.Length, context.Language, _currentLanguage);

    private void UpdateCurrentLanguage(SpokenLanguage language)
    {
        if (_currentLanguage == language)
        {
            return;
        }

        _currentLanguage = language;
        LanguageChanged?.Invoke(this, language);
    }

    private void ChangeSpeechActivity(Speaker speaker, int delta)
    {
        bool? becameActive = null;
        lock (_activityGate)
        {
            var previous = _activeSegmentCounts.GetValueOrDefault(speaker);
            var current = Math.Max(0, previous + delta);
            _activeSegmentCounts[speaker] = current;
            if (previous == 0 && current > 0)
            {
                becameActive = true;
            }
            else if (previous > 0 && current == 0)
            {
                becameActive = false;
            }
        }

        if (becameActive is { } isActive)
        {
            SpeechActivityChanged?.Invoke(this, new SpeechActivity(speaker, isActive));
        }
    }

    private void ResetSpeechActivity()
    {
        List<Speaker> deactivated;
        lock (_activityGate)
        {
            deactivated = [.. _activeSegmentCounts.Where(entry => entry.Value > 0).Select(entry => entry.Key)];
            _activeSegmentCounts.Clear();
        }

        foreach (var speaker in deactivated)
        {
            SpeechActivityChanged?.Invoke(this, new SpeechActivity(speaker, false));
        }
    }

    private void SetState(CaptionSessionState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static string Describe(AudioEndpointKind kind) => kind == AudioEndpointKind.SystemOutput ? "system audio" : "microphone";

    private sealed class SegmentContext(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public SpokenLanguage? Language { get; set; }
    }
}
