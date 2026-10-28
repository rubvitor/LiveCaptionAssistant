using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LiveCaptionAssistant.App.Services;
using LiveCaptionAssistant.App.ViewModels;
using LiveCaptionAssistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settings;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly ILogger<MainWindow> _logger;
    private TopmostEnforcer? _topmostEnforcer;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, GlobalHotkeyService hotkeys, ILogger<MainWindow> logger)
    {
        _viewModel = viewModel;
        _settings = settings;
        _hotkeys = hotkeys;
        _logger = logger;

        InitializeComponent();
        DataContext = viewModel;
        ApplySavedBounds(settings.Current.WindowBounds);
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _topmostEnforcer = new TopmostEnforcer(this, TopmostInterval);
        _topmostEnforcer.Start();

        _hotkeys.Attach(this);
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.Space, () => _viewModel.AskAboutLatestCommand.Execute(null));
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.H, ToggleVisibility);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            base.OnClosing(e);
            return;
        }

        // Stop capture and pending answers before the window goes away, then close for real.
        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        SaveBounds();
        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while shutting down.");
        }

        _topmostEnforcer?.Dispose();
        _hotkeys.Dispose();
        _shutdownCompleted = true;

        // The shutdown may have completed synchronously, still inside this Closing event,
        // where WPF forbids calling Close(); queue it instead.
        _ = Dispatcher.InvokeAsync(Close);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialization failed.");
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            return;
        }

        // Show without taking focus away from the meeting app.
        ShowActivated = false;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void ApplySavedBounds(WindowBounds? bounds)
    {
        if (bounds is null || bounds.Width < MinWidth || bounds.Height < MinHeight)
        {
            return;
        }

        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var window = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (!visible.IntersectsWith(window))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void SaveBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        _settings.Update(settings => settings with
        {
            WindowBounds = new WindowBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
        });
    }
}
