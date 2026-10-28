using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionAssistant.App.Views;
using LiveCaptionAssistant.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.App.Services;

public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread (immediately when already on it).</summary>
    void Post(Action action);
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}

public interface IClipboardService
{
    bool TrySetText(string text);
}

internal sealed class WpfClipboardService : IClipboardService
{
    private readonly ILogger<WpfClipboardService> _logger;

    public WpfClipboardService(ILogger<WpfClipboardService> logger)
    {
        _logger = logger;
    }

    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException ex)
        {
            // Another application can hold the clipboard open.
            _logger.LogWarning(ex, "Could not copy to the clipboard.");
            return false;
        }
    }
}

public interface IDialogService
{
    void ShowSettings();

    void ShowNotice(string title, string message, string buttonText, string? linkText = null, Uri? link = null);

    bool Confirm(string title, string message, string confirmText, string cancelText);

    void OpenFolder(string path);
}

internal sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;
    private readonly IBrowserLauncher _browser;

    public DialogService(IServiceProvider services, IBrowserLauncher browser)
    {
        _services = services;
        _browser = browser;
    }

    public void ShowSettings()
    {
        var window = _services.GetRequiredService<SettingsWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    public void ShowNotice(string title, string message, string buttonText, string? linkText = null, Uri? link = null)
    {
        var window = new NoticeWindow(title, message, buttonText, cancelText: null, linkText, link, _browser)
        {
            Owner = Application.Current.MainWindow,
        };
        window.ShowDialog();
    }

    public bool Confirm(string title, string message, string confirmText, string cancelText)
    {
        var window = new NoticeWindow(title, message, confirmText, cancelText, linkText: null, link: null, _browser)
        {
            Owner = Application.Current.MainWindow,
        };
        return window.ShowDialog() == true;
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
    }
}
