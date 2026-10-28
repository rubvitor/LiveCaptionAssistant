using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCaptionAssistant.App.Services;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Captioning;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Platform;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly Uri ChatGptUsageUri = new("https://chatgpt.com/settings/usage");

    private const int CaptionMaxCharacters = 220;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly CaptionSession _session;
    private readonly AssistantService _assistant;
    private readonly ConversationTranscript _transcript;
    private readonly ISettingsService _settings;
    private readonly IAudioDeviceCatalog _audioDevices;
    private readonly IChatGptAuthenticator _authenticator;
    private readonly IModelCatalogClient _modelCatalog;
    private readonly ISpeechToTextEngine _speechEngine;
    private readonly ISpeechModelCatalog _speechModels;
    private readonly IBrowserLauncher _browser;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<Guid, TranscriptItemViewModel> _transcriptIndex = [];
    private readonly Dictionary<Guid, AnswerItemViewModel> _answerIndex = [];
    private AppSettings _appliedSettings;
    private SpokenLanguage? _detectedLanguage;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _signInCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText), nameof(CaptureButtonIcon), nameof(CaptionDisplayText), nameof(CanChangeCaptureDevices))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText), nameof(CaptureButtonIcon), nameof(CanChangeCaptureDevices))]
    private bool _isStarting;

    [ObservableProperty]
    private OptionItem<LanguageMode>? _selectedLanguageMode;

    [ObservableProperty]
    private string _languageStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionDisplayText), nameof(IsCaptionPlaceholder))]
    private string _captionText = string.Empty;

    [ObservableProperty]
    private bool _captionIsPartial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaptionLanguage))]
    private string? _captionLanguage;

    [ObservableProperty]
    private bool _isThemSpeaking;

    [ObservableProperty]
    private double _systemAudioLevel;

    [ObservableProperty]
    private string _audioStatusText = "Play speech after Start to check the audio signal.";

    [ObservableProperty]
    private string _audioSourceText = string.Empty;

    [ObservableProperty]
    private bool _isAudioSilent;

    [ObservableProperty]
    private bool _captureMicrophone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranscriptPlaceholder))]
    private bool _hasTranscript;

    [ObservableProperty]
    private ChatModelInfo? _selectedModel;

    [ObservableProperty]
    private EffortOption? _selectedEffort;

    [ObservableProperty]
    private OptionItem<ResponseSpeed>? _selectedResponseSpeed;

    [ObservableProperty]
    private bool _isRefreshingModels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInCard), nameof(SignInButtonText), nameof(SignInDescription))]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _accountDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInCard), nameof(SignInButtonText), nameof(SignInDescription))]
    private bool _isPlanUsageEnabled;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string _questionInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnswersPlaceholder))]
    private bool _hasAnswers;

    [ObservableProperty]
    private string _statusText = "Ready. Press Start to caption the meeting audio.";

    [ObservableProperty]
    private double _modelLoadProgress;

    [ObservableProperty]
    private bool _isModelLoadIndeterminate;

    [ObservableProperty]
    private bool _showModelLoadProgress;

    [ObservableProperty]
    private bool _autoAnswer;

    [ObservableProperty]
    private double _overlayOpacity;

    public MainViewModel(
        CaptionSession session,
        AssistantService assistant,
        ConversationTranscript transcript,
        ISettingsService settings,
        IAudioDeviceCatalog audioDevices,
        IChatGptAuthenticator authenticator,
        IModelCatalogClient modelCatalog,
        ISpeechToTextEngine speechEngine,
        ISpeechModelCatalog speechModels,
        IBrowserLauncher browser,
        IClipboardService clipboard,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<MainViewModel> logger)
    {
        _session = session;
        _assistant = assistant;
        _transcript = transcript;
        _settings = settings;
        _audioDevices = audioDevices;
        _authenticator = authenticator;
        _modelCatalog = modelCatalog;
        _speechEngine = speechEngine;
        _speechModels = speechModels;
        _browser = browser;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = logger;
        _appliedSettings = settings.Current;

        LanguageModes =
        [
            new(LanguageMode.AutoDetect, "Auto-detect (EN / PT)"),
            new(LanguageMode.English, "English"),
            new(LanguageMode.Portuguese, "Portuguese"),
        ];

        SelectedLanguageMode = LanguageModes.First(option => option.Value == _appliedSettings.LanguageMode);
        ResponseSpeeds = [new(ResponseSpeed.Standard, "Standard"), new(ResponseSpeed.Fast, "Fast")];
        SelectedResponseSpeed = ResponseSpeeds.First(option => option.Value == _appliedSettings.ChatResponseSpeed);
        AutoAnswer = _appliedSettings.AutoAnswerQuestions;
        CaptureMicrophone = _appliedSettings.CaptureMicrophone;
        OverlayOpacity = _appliedSettings.OverlayOpacity;
        LoadModels(KnownModelCatalog.All);
        UpdateAccountState();
        UpdateLanguageStatus();
        UpdateAudioSource();

        _session.StateChanged += OnSessionStateChanged;
        _session.LanguageChanged += OnSessionLanguageChanged;
        _session.SpeechActivityChanged += OnSpeechActivityChanged;
        _session.AudioSignalChanged += OnAudioSignalChanged;
        _session.ModelLoadProgressChanged += OnModelLoadProgressChanged;
        _session.ErrorOccurred += OnSessionError;
        _transcript.Changed += OnTranscriptChanged;
        _assistant.AnswerChanged += OnAnswerChanged;
        _authenticator.AccountChanged += OnAccountChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<OptionItem<LanguageMode>> LanguageModes { get; }

    public ObservableCollection<TranscriptItemViewModel> Transcript { get; } = [];

    public ObservableCollection<AnswerItemViewModel> Answers { get; } = [];

    public ObservableCollection<ChatModelInfo> Models { get; } = [];

    public ObservableCollection<EffortOption> Efforts { get; } = [];

    public IReadOnlyList<OptionItem<ResponseSpeed>> ResponseSpeeds { get; }

    public string CaptureButtonText => IsStarting ? "Cancel" : IsRunning ? "Stop" : "Start";

    public string CaptureButtonIcon => IsStarting ? "\uE711" : IsRunning ? "\uE71A" : "\uE768";

    public bool CanChangeCaptureDevices => !IsRunning && !IsStarting;

    public bool IsCaptionPlaceholder => CaptionText.Length == 0;

    public string CaptionDisplayText => CaptionText.Length == 0
        ? IsRunning ? "Listening… captions appear here when someone speaks." : "Press Start to caption the meeting audio."
        : TakeEnd(CaptionText, CaptionMaxCharacters);

    public bool HasCaptionLanguage => !string.IsNullOrEmpty(CaptionLanguage);

    public bool ShowTranscriptPlaceholder => !HasTranscript;

    public bool ShowAnswersPlaceholder => !HasAnswers;

    public bool ShowSignInCard => !IsSignedIn || !IsPlanUsageEnabled;

    public string SignInButtonText => IsSignedIn ? "Enable ChatGPT plan usage" : "Continue with ChatGPT";

    public string SignInDescription => IsSignedIn
        ? "You're signed in, but this app isn't allowed to use your ChatGPT plan yet. Allow it to get AI answers."
        : "Sign in with your ChatGPT account (Go, Plus or Pro). On the OpenAI page you can choose \"Continue with Google\". Answers use your ChatGPT plan - no API key needed.";

    public async Task InitializeAsync()
    {
        if (_authenticator.CurrentAccount is not null)
        {
            await RefreshModelsAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        // Cancel only: the operations that own these sources dispose them when they finish.
        _startCancellation?.Cancel();
        _signInCancellation?.Cancel();
        _assistant.CancelAll();
        try
        {
            await _session.StopAsync().WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Caption session did not stop within {Timeout}.", ShutdownTimeout);
        }
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionStateChanged;
        _session.LanguageChanged -= OnSessionLanguageChanged;
        _session.SpeechActivityChanged -= OnSpeechActivityChanged;
        _session.AudioSignalChanged -= OnAudioSignalChanged;
        _session.ModelLoadProgressChanged -= OnModelLoadProgressChanged;
        _session.ErrorOccurred -= OnSessionError;
        _transcript.Changed -= OnTranscriptChanged;
        _assistant.AnswerChanged -= OnAnswerChanged;
        _authenticator.AccountChanged -= OnAccountChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleCaptureAsync()
    {
        if (IsStarting)
        {
            _startCancellation?.Cancel();
            return;
        }

        if (IsRunning)
        {
            StatusText = "Stopping…";
            await _session.StopAsync();
            return;
        }

        await StartCaptureAsync();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn)
        {
            return;
        }

        IsSigningIn = true;
        StatusText = "Complete the sign-in in your browser…";
        _signInCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var requestConsent = _authenticator.CurrentAccount is { PlanUsageEnabled: false };
            var account = await _authenticator.SignInAsync(requestConsent, _signInCancellation.Token);
            if (account.PlanUsageEnabled)
            {
                StatusText = $"Signed in as {account.DisplayName}.";
                if (!_settings.Current.HasSeenPlanUsageNotice)
                {
                    _settings.Update(settings => settings with { HasSeenPlanUsageNotice = true });
                    _dialogs.ShowNotice(
                        "You're using your ChatGPT plan",
                        "AI answers in Live Caption Assistant count toward the usage included in your ChatGPT plan. You can review usage and set a limit for this app in ChatGPT settings.",
                        "Got it",
                        "Manage usage",
                        ChatGptUsageUri);
                }
            }
            else
            {
                StatusText = "Signed in, but ChatGPT plan usage was not allowed. Use 'Enable ChatGPT plan usage' to allow it.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sign-in was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT sign-in failed.");
            StatusText = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            CancelAndDispose(ref _signInCancellation);
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCancellation?.Cancel();

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_authenticator.CurrentAccount is not { PlanUsageEnabled: true })
        {
            LoadModels(KnownModelCatalog.All);
            return;
        }

        IsRefreshingModels = true;
        try
        {
            var models = await _modelCatalog.GetAvailableModelsAsync(CancellationToken.None);
            LoadModels(models.Count > 0 ? models : KnownModelCatalog.All);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the model list.");
            StatusText = $"Could not load your ChatGPT models: {ex.Message}";
            if (Models.Count == 0)
            {
                LoadModels(KnownModelCatalog.All);
            }
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void Ask()
    {
        var question = QuestionInput.Trim();
        if (question.Length == 0)
        {
            return;
        }

        QuestionInput = string.Empty;
        _ = _assistant.AskAsync(question, questionLanguage: null, isAutomatic: false);
    }

    [RelayCommand]
    private void AskAboutSegment(TranscriptItemViewModel? item)
    {
        if (item is not null && item.Text.Length > 0)
        {
            _ = _assistant.AskAsync(item.Text, item.Language, isAutomatic: false);
        }
    }

    [RelayCommand]
    private void AskAboutLatest()
    {
        if (!_assistant.AskAboutLatestSpeech())
        {
            StatusText = "There is no recent speech from the other participants to answer.";
        }
    }

    [RelayCommand]
    private void StopAnswer(AnswerItemViewModel? item)
    {
        if (item is not null)
        {
            _assistant.Cancel(item.Id);
        }
    }

    [RelayCommand]
    private void CopyAnswer(AnswerItemViewModel? item)
    {
        if (item is { HasText: true } && _clipboard.TrySetText(item.Text))
        {
            StatusText = "Answer copied to the clipboard.";
        }
    }

    [RelayCommand]
    private void CopyTranscript()
    {
        var builder = new StringBuilder();
        foreach (var item in Transcript.Where(item => item.IsFinal))
        {
            builder.Append('[').Append(item.TimeLabel).Append("] ")
                .Append(item.SpeakerLabel).Append(" (").Append(item.LanguageLabel).Append("): ")
                .AppendLine(item.Text);
        }

        if (builder.Length > 0 && _clipboard.TrySetText(builder.ToString()))
        {
            StatusText = "Transcript copied to the clipboard.";
        }
    }

    [RelayCommand]
    private void ClearTranscript() => _transcript.Clear();

    [RelayCommand]
    private void ClearAnswers()
    {
        _assistant.CancelAll();
        Answers.Clear();
        _answerIndex.Clear();
        HasAnswers = false;
    }

    [RelayCommand]
    private void OpenUsage() => _browser.Open(ChatGptUsageUri);

    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    private bool CanAsk() => !string.IsNullOrWhiteSpace(QuestionInput);

    private async Task StartCaptureAsync()
    {
        var settings = _settings.Current;
        var speechModel = _speechModels.Resolve(settings.SpeechModelId);
        if (!speechModel.IsDownloaded && !_dialogs.Confirm(
                "Download the speech model",
                $"Captions are transcribed locally on this PC with Whisper (free and offline). The \"{speechModel.DisplayName}\" model (about {FormatBytes(speechModel.ApproximateSizeBytes)}) is downloaded once and kept in your local app data.",
                "Download",
                "Cancel"))
        {
            return;
        }

        _startCancellation = new CancellationTokenSource();
        IsStarting = true;
        StatusText = "Preparing the speech engine…";
        try
        {
            var options = new CaptionSessionOptions(
                settings.LanguageMode,
                settings.SystemAudioDeviceId,
                settings.CaptureMicrophone,
                settings.MicrophoneDeviceId,
                settings.LivePartialCaptions);
            await _session.StartAsync(options, _startCancellation.Token);
            _appliedSettings = settings;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Start cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start captions.");
            StatusText = $"Could not start captions: {ex.Message}";
        }
        finally
        {
            CancelAndDispose(ref _startCancellation);
            IsStarting = false;
            ShowModelLoadProgress = false;
        }
    }

    partial void OnSelectedLanguageModeChanged(OptionItem<LanguageMode>? value)
    {
        if (value is null)
        {
            return;
        }

        _session.SetLanguageMode(value.Value);
        _settings.Update(settings => settings with { LanguageMode = value.Value });
        UpdateLanguageStatus();
    }

    partial void OnSelectedModelChanged(ChatModelInfo? value)
    {
        Efforts.Clear();
        if (value is null)
        {
            return;
        }

        _settings.Update(settings => settings with { ChatModelId = value.Id });
        var defaultLabel = value.DefaultEffort is { } defaultEffort ? $"Default ({defaultEffort.DisplayName})" : "Default";
        Efforts.Add(new EffortOption(null, defaultLabel));
        foreach (var effort in value.SupportedEfforts)
        {
            Efforts.Add(new EffortOption(effort, effort.DisplayName));
        }

        SelectedEffort = ChooseEffort(value, _settings.Current.ChatReasoningEffort);
    }

    partial void OnSelectedEffortChanged(EffortOption? value)
    {
        if (value is not null)
        {
            _settings.Update(settings => settings with { ChatReasoningEffort = value.SettingValue });
        }
    }

    partial void OnSelectedResponseSpeedChanged(OptionItem<ResponseSpeed>? value)
    {
        if (value is not null)
        {
            _settings.Update(settings => settings with { ChatResponseSpeed = value.Value });
        }
    }

    partial void OnAutoAnswerChanged(bool value) =>
        _settings.Update(settings => settings with { AutoAnswerQuestions = value });

    partial void OnCaptureMicrophoneChanged(bool value) =>
        _settings.Update(settings => settings with { CaptureMicrophone = value });

    partial void OnOverlayOpacityChanged(double value) =>
        _settings.Update(settings => settings with { OverlayOpacity = Math.Clamp(value, 0.35, 1) });

    private EffortOption ChooseEffort(ChatModelInfo model, string? savedEffort)
    {
        // Medium is the default balance requested for live answers; saved choices remain selectable.
        var preferred = savedEffort switch
        {
            AppSettings.ModelDefaultEffort => null,
            null => ReasoningEffort.Medium,
            _ => new ReasoningEffort(savedEffort),
        };

        if (preferred is not null && !model.Supports(preferred))
        {
            preferred = model.Supports(ReasoningEffort.Low) ? ReasoningEffort.Low : null;
        }

        return Efforts.First(option => option.Effort == preferred);
    }

    private void LoadModels(IReadOnlyList<ChatModelInfo> models)
    {
        var savedModelId = _settings.Current.ChatModelId;
        Models.Clear();
        foreach (var model in models)
        {
            Models.Add(model);
        }

        SelectedModel = Models.FirstOrDefault(model => model.Id == savedModelId)
            ?? Models.FirstOrDefault(model => model.Id == KnownModelCatalog.PreferredModelId)
            ?? Models.FirstOrDefault();
    }

    private void OnSessionStateChanged(object? sender, CaptionSessionState state) => _dispatcher.Post(() =>
    {
        IsRunning = state == CaptionSessionState.Running;
        IsStarting = state == CaptionSessionState.Starting;
        switch (state)
        {
            case CaptionSessionState.Running:
                StatusText = $"Listening to the meeting audio · {_speechEngine.ActiveModelDescription}";
                AudioStatusText = "Waiting for system audio… play speech to check the signal.";
                UpdateAudioSource();
                break;
            case CaptionSessionState.Stopped:
                IsThemSpeaking = false;
                SystemAudioLevel = 0;
                IsAudioSilent = false;
                AudioStatusText = "System audio stopped.";
                if (!StatusText.StartsWith("Could not", StringComparison.Ordinal))
                {
                    StatusText = "Captions stopped.";
                }

                break;
        }

        UpdateLanguageStatus();
    });

    private void OnSessionLanguageChanged(object? sender, SpokenLanguage language) => _dispatcher.Post(() =>
    {
        _detectedLanguage = language;
        UpdateLanguageStatus();
    });

    private void OnSpeechActivityChanged(object? sender, SpeechActivity activity) => _dispatcher.Post(() =>
    {
        if (activity.Speaker == Speaker.Them)
        {
            IsThemSpeaking = activity.IsActive;
        }
    });

    private void OnModelLoadProgressChanged(object? sender, ModelLoadProgress progress) => _dispatcher.Post(() =>
    {
        switch (progress.Stage)
        {
            case ModelLoadStage.Downloading:
                ShowModelLoadProgress = true;
                IsModelLoadIndeterminate = progress.Fraction is null;
                ModelLoadProgress = progress.Fraction ?? 0;
                StatusText = progress.TotalBytes is { } total
                    ? $"Downloading the speech model… {FormatBytes(progress.BytesDownloaded)} of {FormatBytes(total)}"
                    : $"Downloading the speech model… {FormatBytes(progress.BytesDownloaded)}";
                break;
            case ModelLoadStage.Loading:
                ShowModelLoadProgress = true;
                IsModelLoadIndeterminate = true;
                StatusText = "Loading the speech model…";
                break;
            case ModelLoadStage.WarmingUp:
                ShowModelLoadProgress = true;
                IsModelLoadIndeterminate = true;
                StatusText = "Preparing the first transcription… this can take a few seconds. Audio starts when ready.";
                break;
            case ModelLoadStage.Ready:
                ShowModelLoadProgress = false;
                break;
        }
    });

    private void OnAudioSignalChanged(object? sender, AudioSignalStatus signal)
    {
        if (signal.Kind != AudioEndpointKind.SystemOutput)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!IsRunning)
            {
                return;
            }

            // A logarithmic meter makes quiet meeting audio visible as well as loud playback.
            SystemAudioLevel = signal.Peak > 0 ? Math.Clamp((20 * Math.Log10(signal.Peak) + 60) / 60, 0, 1) : 0;
            IsAudioSilent = signal.IsSilent;
            AudioStatusText = signal.IsSilent
                ? "No system sound detected. If someone is speaking, check the meeting output and Audio settings."
                : signal.HasReceivedSignal
                    ? "System audio received · speech recognition active."
                    : "Waiting for system audio… play speech to check the signal.";
        });
    }

    private void UpdateAudioSource()
    {
        var deviceId = _settings.Current.SystemAudioDeviceId;
        AudioSourceText = _audioDevices.GetDevices(AudioEndpointKind.SystemOutput)
            .FirstOrDefault(device => device.Id == deviceId)?.Name ?? "Selected output unavailable — check Audio settings";
    }

    private void OnSessionError(object? sender, string message) => _dispatcher.Post(() => StatusText = message);

    private void OnTranscriptChanged(object? sender, TranscriptChange change) => _dispatcher.Post(() => ApplyTranscriptChange(change));

    private void ApplyTranscriptChange(TranscriptChange change)
    {
        switch (change.Kind)
        {
            case TranscriptChangeKind.Cleared:
                Transcript.Clear();
                _transcriptIndex.Clear();
                CaptionText = string.Empty;
                CaptionLanguage = null;
                break;

            case TranscriptChangeKind.Removed when change.Segment is { } removed:
                if (_transcriptIndex.Remove(removed.Id, out var removedItem))
                {
                    Transcript.Remove(removedItem);
                }

                break;

            case TranscriptChangeKind.Added or TranscriptChangeKind.Updated when change.Segment is { } segment:
                if (_transcriptIndex.TryGetValue(segment.Id, out var existing))
                {
                    existing.Update(segment);
                }
                else
                {
                    var item = new TranscriptItemViewModel(segment);
                    _transcriptIndex[segment.Id] = item;
                    InsertChronologically(item);
                }

                if (segment.Speaker == Speaker.Them)
                {
                    CaptionText = segment.Text;
                    CaptionIsPartial = !segment.IsFinal;
                    CaptionLanguage = segment.Language?.ToShortLabel();
                }

                if (segment.IsFinal && segment.Language is { } language)
                {
                    _detectedLanguage = language;
                    UpdateLanguageStatus();
                }

                break;
        }

        HasTranscript = Transcript.Count > 0;
    }

    private void InsertChronologically(TranscriptItemViewModel item)
    {
        var index = Transcript.Count;
        while (index > 0 && Transcript[index - 1].StartedAt > item.StartedAt)
        {
            index--;
        }

        Transcript.Insert(index, item);
    }

    private void OnAnswerChanged(object? sender, AnswerSnapshot snapshot) => _dispatcher.Post(() =>
    {
        if (_answerIndex.TryGetValue(snapshot.Id, out var existing))
        {
            existing.Apply(snapshot);
        }
        else
        {
            var item = new AnswerItemViewModel(snapshot);
            _answerIndex[snapshot.Id] = item;
            Answers.Insert(0, item);
            HasAnswers = true;
        }

        if (snapshot.ErrorCode == AssistantService.AuthenticationRequiredCode)
        {
            UpdateAccountState();
        }
    });

    private void OnAccountChanged(object? sender, EventArgs args) => _dispatcher.Post(() =>
    {
        UpdateAccountState();
        _ = RefreshModelsAsync();
    });

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() =>
    {
        AutoAnswer = settings.AutoAnswerQuestions;
        CaptureMicrophone = settings.CaptureMicrophone;
        _session.SetLivePartialCaptions(settings.LivePartialCaptions);
        UpdateAudioSource();

        if (IsRunning && RequiresRestart(_appliedSettings, settings))
        {
            StatusText = "Audio or speech model settings changed. Stop and start captions to apply them.";
        }
    });

    private static bool RequiresRestart(AppSettings applied, AppSettings current) =>
        applied.SystemAudioDeviceId != current.SystemAudioDeviceId
        || applied.CaptureMicrophone != current.CaptureMicrophone
        || applied.MicrophoneDeviceId != current.MicrophoneDeviceId
        || applied.SpeechModelId != current.SpeechModelId;

    private void UpdateAccountState()
    {
        var account = _authenticator.CurrentAccount;
        IsSignedIn = account is not null;
        IsPlanUsageEnabled = account?.PlanUsageEnabled == true;
        AccountDisplay = account?.DisplayName ?? string.Empty;
    }

    private void UpdateLanguageStatus()
    {
        var mode = SelectedLanguageMode?.Value ?? LanguageMode.AutoDetect;
        LanguageStatusText = mode.ToFixedLanguage() is { } fixedLanguage
            ? $"Fixed: {fixedLanguage.ToDisplayName()}"
            : _detectedLanguage is { } detected
                ? $"Detected: {detected.ToDisplayName()}"
                : "Detecting EN / PT";
    }

    private static string TakeEnd(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var start = text.Length - maxCharacters;
        var wordBoundary = text.IndexOf(' ', start);
        return "… " + (wordBoundary > 0 && wordBoundary < text.Length - 1 ? text[(wordBoundary + 1)..] : text[start..]);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_000_000_000
            ? (bytes / 1_000_000_000d).ToString("0.0 GB", CultureInfo.InvariantCulture)
            : (bytes / 1_000_000d).ToString("0 MB", CultureInfo.InvariantCulture);

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        var current = Interlocked.Exchange(ref cancellation, null);
        if (current is null)
        {
            return;
        }

        current.Cancel();
        current.Dispose();
    }
}
