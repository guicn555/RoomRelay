using System.Runtime.InteropServices;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using SonosStreaming.App.ViewModels;
using Serilog;

namespace SonosStreaming.App.Tray;

public sealed class TrayIconHost : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private TaskbarIcon? _icon;
    private Window? _window;
    private PopupMenu? _popupMenu;

    public void Setup(Window window, MainViewModel vm)
    {
        _window = window;
        _icon = new TaskbarIcon();

        _icon.ToolTipText = "RoomRelay";
        _icon.NoLeftClickDelay = true;
        _icon.LeftClickCommand = new RelayTrayCommand(() => RestoreWindow());
        _icon.RightClickCommand = new RelayTrayCommand(ShowPopupMenu);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "sonos-streaming.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load tray icon from {Path}", iconPath);
            }
        }
        else
        {
            Log.Warning("Tray icon file not found at {Path}", iconPath);
        }

        _popupMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Show app", (_, _) => RestoreWindow()),
                new PopupMenuSeparator(),
                new PopupMenuItem("Quit", (_, _) => QuitApp(vm)),
            },
        };

        _icon.ForceCreate();
    }

    private void QuitApp(MainViewModel vm)
    {
        if (_window is not MainWindow mw) return;
        mw.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await vm.StopCommand.ExecuteAsync(null); } catch { }
            mw.MarkExiting();
            _icon?.Dispose();
            _icon = null;
            Application.Current.Exit();
        });
    }

    private void ShowPopupMenu()
    {
        if (_popupMenu == null || _window is not MainWindow mw) return;
        if (!GetCursorPos(out var pt)) return;
        try
        {
            _popupMenu.Show(mw.Hwnd, pt.X, pt.Y);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show tray popup menu");
        }
    }

    private void RestoreWindow()
    {
        if (_window is MainWindow mw)
        {
            mw.RestoreFromTray();
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
    }

    private sealed class RelayTrayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayTrayCommand(Action action) => _action = action;
#pragma warning disable CS0067 // Required by ICommand but never subscribed
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
