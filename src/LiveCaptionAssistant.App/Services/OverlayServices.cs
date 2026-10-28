using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LiveCaptionAssistant.App.Interop;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.App.Services;

/// <summary>
/// Keeps the overlay above other always-on-top windows (e.g. a meeting app that also pins itself)
/// by periodically re-asserting HWND_TOPMOST without stealing focus.
/// </summary>
internal sealed class TopmostEnforcer : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;

    public TopmostEnforcer(Window window, TimeSpan interval)
    {
        _window = window;
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => Enforce(), window.Dispatcher);
    }

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Enforce()
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }
}

/// <summary>System-wide hotkeys, so the overlay can be controlled while the meeting app has focus.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly Dictionary<int, Action> _callbacks = [];
    private HwndSource? _source;
    private int _nextId = 0x4C43;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger)
    {
        _logger = logger;
    }

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
    }

    public bool Register(ModifierKeys modifiers, Key key, Action callback)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("Attach the service to a window before registering hotkeys.");
        }

        var id = _nextId++;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, (uint)modifiers | NativeMethods.ModNoRepeat, virtualKey))
        {
            _logger.LogWarning("Could not register hotkey {Modifiers}+{Key} (Win32 error {Error}); another app may be using it.",
                modifiers, key, Marshal.GetLastPInvokeError());
            return false;
        }

        _callbacks[id] = callback;
        return true;
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        foreach (var id in _callbacks.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _callbacks.Clear();
        _source.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            handled = true;
        }

        return IntPtr.Zero;
    }
}
