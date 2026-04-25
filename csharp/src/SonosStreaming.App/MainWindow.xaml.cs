using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace SonosStreaming.App;

public sealed partial class MainWindow : Window
{
    private bool _exiting;

    public void MarkExiting() => _exiting = true;

    public MainWindow()
    {
        this.Title = "RoomRelay";

        SystemBackdrop = new MicaBackdrop();

        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(900, 680));
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "sonos-streaming.ico");
            if (System.IO.File.Exists(iconPath))
            {
                try { appWindow.SetIcon(iconPath); } catch { }
            }
        }

        this.Content = new Views.MainPage();

        Closed += OnClosed;
    }

    public IntPtr Hwnd { get; private set; }

    public void CacheHwnd()
    {
        Hwnd = WindowNative.GetWindowHandle(this);
    }

    public void HideToTray()
    {
        var appWindow = GetAppWindow();
        appWindow?.Hide();
    }

    public void RestoreFromTray()
    {
        var appWindow = GetAppWindow();
        appWindow?.Show();
        PInvoke.ShowWindow(new Windows.Win32.Foundation.HWND(Hwnd), SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(Hwnd));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_exiting) return;
        args.Handled = true;
        HideToTray();
    }

    private AppWindow? GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        Hwnd = hwnd;
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }
}
