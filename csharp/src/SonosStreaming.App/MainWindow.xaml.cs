using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using SonosStreaming.App.ViewModels;
using SonosStreaming.Core.State;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace SonosStreaming.App;

public sealed partial class MainWindow : Window
{
    private const int MinWindowWidth = 816;
    private const int MinWindowHeight = 700;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private static readonly UIntPtr MinSizeSubclassId = new(1);

    private bool _exiting;
    private Grid? _root;
    private Grid? _titleBar;
    private SubclassProc? _subclassProc;

    public void MarkExiting() => _exiting = true;

    public MainWindow()
    {
        this.Title = "RoomRelay";
        ExtendsContentIntoTitleBar = true;

        SystemBackdrop = new MicaBackdrop();
        var vm = App.Current.Services.GetRequiredService<MainViewModel>();

        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(855, 720));
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "sonos-streaming.ico");
            if (System.IO.File.Exists(iconPath))
            {
                try { appWindow.SetIcon(iconPath); } catch { }
            }
        }
        InstallMinSizeHook();

        _root = new Grid { RequestedTheme = ToElementTheme(vm.ThemePreference) };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar = BuildTitleBar(vm);
        Grid.SetRow(_titleBar, 0);
        _root.Children.Add(_titleBar);

        var page = new Views.MainPage();
        Grid.SetRow(page, 1);
        _root.Children.Add(page);
        this.Content = _root;
        SetTitleBar(_titleBar);
        ApplyTheme(vm.ThemePreference);
        vm.ThemePreferenceChanged += (_, _) => ApplyTheme(vm.ThemePreference);

        Closed += OnClosed;
    }

    private Grid BuildTitleBar(MainViewModel vm)
    {
        var bar = new Grid
        {
            Height = 40,
            Padding = new Thickness(16, 0, 138, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            ColumnSpacing = 10,
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildTitleIcon();
        Grid.SetColumn(icon, 0);
        bar.Children.Add(icon);

        var title = new TextBlock
        {
            Text = "RoomRelay",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Grid.SetColumn(title, 1);
        bar.Children.Add(title);

        return bar;
    }

    private static FrameworkElement BuildTitleIcon()
    {
        var icon = new Grid
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };

        icon.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 40, 78)),
        });

        icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 3,
            Height = 4,
            Margin = new Thickness(4, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 252, 255)),
        });

        icon.Children.Add(new Polygon
        {
            Points =
            {
                new Windows.Foundation.Point(7, 8),
                new Windows.Foundation.Point(10, 5),
                new Windows.Foundation.Point(10, 15),
                new Windows.Foundation.Point(7, 12),
            },
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 252, 255)),
        });

        icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = ArcGeometry(new Windows.Foundation.Point(11.5, 7.2), new Windows.Foundation.Point(11.5, 12.8), new Windows.Foundation.Size(4, 4)),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 235, 205)),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = ArcGeometry(new Windows.Foundation.Point(13.2, 5.3), new Windows.Foundation.Point(13.2, 14.7), new Windows.Foundation.Size(6.4, 6.4)),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 140, 226, 255)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        icon.Children.Add(new Ellipse
        {
            Width = 3.5,
            Height = 3.5,
            Margin = new Thickness(14.5, 8.25, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 235, 205)),
        });

        return icon;
    }

    private static PathGeometry ArcGeometry(Windows.Foundation.Point start, Windows.Foundation.Point end, Windows.Foundation.Size size)
    {
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = size,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void ApplyTheme(ThemePreference preference)
    {
        if (_root != null)
            _root.RequestedTheme = ToElementTheme(preference);
        ApplyTitleBarColors(preference);
    }

    private void ApplyTitleBarColors(ThemePreference preference)
    {
        var appWindow = GetAppWindow();
        if (appWindow == null) return;

        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        if (preference == ThemePreference.System)
        {
            appWindow.TitleBar.ButtonForegroundColor = null;
            appWindow.TitleBar.ButtonInactiveForegroundColor = null;
        }
        else
        {
            var dark = preference == ThemePreference.Dark;
            appWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            appWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.DimGray;
        }
    }

    private static ElementTheme ToElementTheme(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

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

    private void InstallMinSizeHook()
    {
        if (Hwnd == IntPtr.Zero || _subclassProc != null) return;
        _subclassProc = MinSizeSubclassProc;
        SetWindowSubclass(Hwnd, _subclassProc, MinSizeSubclassId, UIntPtr.Zero);
    }

    private IntPtr MinSizeSubclassProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = MinWindowWidth;
            info.ptMinTrackSize.Y = MinWindowHeight;
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
