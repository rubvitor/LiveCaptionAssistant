using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCaptionAssistant.App.Services;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Platform;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using LiveCaptionAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IChatGptAuthenticator _authenticator;
    private readonly IBrowserLauncher _browser;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private OptionItem<string>? _selectedOutputDevice;

    [ObservableProperty]
    private bool _captureMicrophone;

    [ObservableProperty]
    private OptionItem<string>? _selectedMicrophone;

    [ObservableProperty]
    private OptionItem<string>? _selectedSpeechModel;

    [ObservableProperty]
    private bool _useGpuAcceleration;

    [ObservableProperty]
    private bool _livePartialCaptions;

    [ObservableProperty]
    private bool _autoAnswer;

    [ObservableProperty]
    private OptionItem<AnswerLanguagePreference>? _selectedAnswerLanguage;

    [ObservableProperty]
    private OptionItem<AnswerStyle>? _selectedAnswerStyle;

    [ObservableProperty]
    private string _userBackground = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private string _accountSummary = string.Empty;

    [ObservableProperty]
    private string _accountMessage = string.Empty;

    public SettingsViewModel(
        ISettingsService settings,
        IAudioDeviceCatalog devices,
        ISpeechModelCatalog speechModels,
        IChatGptAuthenticator authenticator,
        IBrowserLauncher browser,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _authenticator = authenticator;
        _browser = browser;
        _dialogs = dialogs;
        _logger = logger;

        OutputDevices = ToOptions(devices.GetDevices(AudioEndpointKind.SystemOutput));
        Microphones = ToOptions(devices.GetDevices(AudioEndpointKind.Microphone));
        SpeechModels = [.. speechModels.GetModels().Select(model => new OptionItem<string>(
            model.Id,
            $"{model.DisplayName} · {FormatSize(model.ApproximateSizeBytes)}{(model.IsDownloaded ? " · downloaded" : string.Empty)}"))];
        AnswerLanguages =
        [
            new(AnswerLanguagePreference.SameAsQuestion, "Same language as the question"),
            new(AnswerLanguagePreference.English, "Always English"),
            new(AnswerLanguagePreference.Portuguese, "Always Portuguese"),
        ];
        AnswerStyles =
        [
            new(AnswerStyle.Concise, "Concise - quick to say out loud"),
            new(AnswerStyle.Detailed, "Detailed"),
        ];

        var current = settings.Current;
        SelectedOutputDevice = FindOrFirst(OutputDevices, current.SystemAudioDeviceId);
        CaptureMicrophone = current.CaptureMicrophone;
        SelectedMicrophone = FindOrFirst(Microphones, current.MicrophoneDeviceId);
        SelectedSpeechModel = FindOrFirst(SpeechModels, speechModels.Resolve(current.SpeechModelId).Id);
        UseGpuAcceleration = current.UseGpuAcceleration;
        LivePartialCaptions = current.LivePartialCaptions;
        AutoAnswer = current.AutoAnswerQuestions;
        SelectedAnswerLanguage = AnswerLanguages.First(option => option.Value == current.AnswerLanguage);
        SelectedAnswerStyle = AnswerStyles.First(option => option.Value == current.AnswerStyle);
        UserBackground = current.UserBackground;
        UpdateAccountSummary();
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<OptionItem<string>> OutputDevices { get; }

    public IReadOnlyList<OptionItem<string>> Microphones { get; }

    public IReadOnlyList<OptionItem<string>> SpeechModels { get; }

    public IReadOnlyList<OptionItem<AnswerLanguagePreference>> AnswerLanguages { get; }

    public IReadOnlyList<OptionItem<AnswerStyle>> AnswerStyles { get; }

    public string ShortcutsDescription =>
        "Ctrl+Shift+Space - answer the latest thing the other participants said\nCtrl+Shift+H - show or hide the overlay";

    [RelayCommand]
    private void Save()
    {
        var gpuChanged = _settings.Current.UseGpuAcceleration != UseGpuAcceleration;
        _settings.Update(settings => settings with
        {
            SystemAudioDeviceId = SelectedOutputDevice?.Value ?? AudioDeviceIds.DefaultDevice,
            CaptureMicrophone = CaptureMicrophone,
            MicrophoneDeviceId = SelectedMicrophone?.Value ?? AudioDeviceIds.DefaultCommunicationsDevice,
            SpeechModelId = SelectedSpeechModel?.Value,
            UseGpuAcceleration = UseGpuAcceleration,
            LivePartialCaptions = LivePartialCaptions,
            AutoAnswerQuestions = AutoAnswer,
            AnswerLanguage = SelectedAnswerLanguage?.Value ?? AnswerLanguagePreference.SameAsQuestion,
            AnswerStyle = SelectedAnswerStyle?.Value ?? AnswerStyle.Concise,
            UserBackground = UserBackground.Trim(),
        });

        if (gpuChanged)
        {
            _dialogs.ShowNotice("Restart required", "The GPU acceleration setting takes effect the next time you open Live Caption Assistant.", "OK");
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private async Task SignOutAsync() => await SignOutCoreAsync(forgetAccount: false);

    [RelayCommand]
    private async Task ForgetAccountAsync()
    {
        if (_dialogs.Confirm(
                "Forget ChatGPT account",
                "This signs out and removes this app's registration for your ChatGPT account, so you can sign in with a different account next time.",
                "Forget account",
                "Cancel"))
        {
            await SignOutCoreAsync(forgetAccount: true);
        }
    }

    [RelayCommand]
    private void OpenUsage() => _browser.Open(MainViewModel.ChatGptUsageUri);

    [RelayCommand]
    private void OpenLogsFolder() => _dialogs.OpenFolder(AppPaths.LogsDirectory);

    private bool IsSignedIn() => _authenticator.CurrentAccount is not null;

    private async Task SignOutCoreAsync(bool forgetAccount)
    {
        try
        {
            var revoked = await _authenticator.SignOutAsync(forgetAccount, CancellationToken.None);
            AccountMessage = revoked
                ? "Signed out."
                : "Signed out on this PC, but OpenAI did not confirm the revocation. You can disconnect the app in ChatGPT Settings > Security.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-out failed.");
            AccountMessage = $"Sign-out failed: {ex.Message}";
        }

        UpdateAccountSummary();
    }

    private void UpdateAccountSummary()
    {
        var account = _authenticator.CurrentAccount;
        AccountSummary = account is null
            ? "Not signed in. Use \"Continue with ChatGPT\" in the AI panel."
            : $"Signed in as {account.DisplayName} · ChatGPT plan usage {(account.PlanUsageEnabled ? "allowed" : "not allowed")}";
    }

    private static List<OptionItem<string>> ToOptions(IReadOnlyList<AudioDeviceInfo> devices) =>
        [.. devices.Select(device => new OptionItem<string>(device.Id, device.Name))];

    private static OptionItem<string> FindOrFirst(IReadOnlyList<OptionItem<string>> options, string? value) =>
        options.FirstOrDefault(option => option.Value == value) ?? options[0];

    private static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000
            ? (bytes / 1_000_000_000d).ToString("0.0 GB", CultureInfo.InvariantCulture)
            : (bytes / 1_000_000d).ToString("0 MB", CultureInfo.InvariantCulture);
}
