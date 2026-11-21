using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SonosStreaming.App.Services;
using SonosStreaming.App.Tray;
using SonosStreaming.App.ViewModels;
using SonosStreaming.Core.Network;
using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;
using Serilog;

namespace SonosStreaming.App;

public sealed partial class App : Application
{
    public IServiceProvider Services { get; }
    public static new App Current => (App)Application.Current;
    public Window? MainWindow { get; private set; }

    public App()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RoomRelay", "app.log"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("RoomRelay {Version} starting on {OS}",
            DiagnosticsPackageService.VersionLabel,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        var services = new ServiceCollection();
        services.AddSingleton<AppCore>();
        services.AddSingleton<PipelineOptions>();
        services.AddSingleton(_ => AppSettings.Load());
        services.AddSingleton(sp => new PipelineRunner(
            sp.GetRequiredService<AppCore>(),
            sp.GetRequiredService<PipelineOptions>()));
        services.AddSingleton<ISsdpDiscovery, SsdpDiscovery>();
        services.AddSingleton<ITopologyResolver, TopologyResolver>();
        services.AddSingleton<SharedGuiBridge>();
        services.AddSingleton<DiagnosticsPackageService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayIconHost>();
        Services = services.BuildServiceProvider();

        this.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            RecordCrash("UnhandledException", "Unhandled WinUI exception", e.Exception);
        };

        // Application.UnhandledException only sees exceptions on the UI thread.
        // One thrown on a thread-pool thread (for example from an async void
        // handler that touched the UI off-thread) ends the process, and without
        // this the log simply stops (issue #35).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordCrash("Unhandled background exception", "Unhandled background exception; RoomRelay is exiting",
                e.ExceptionObject as Exception);

        this.InitializeComponent();
    }

    private static void RecordCrash(string kind, string logMessage, Exception? exception)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RoomRelay", "crash.txt");
        try { File.WriteAllText(path, $"[{DateTime.Now}] {kind}\n{exception}\n"); } catch { }
        Log.Fatal(exception, logMessage);
        Log.CloseAndFlush();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        var bridge = Services.GetRequiredService<SharedGuiBridge>();
        bridge.Initialize(MainWindow);
        bridge.BindPipeline(Services.GetRequiredService<PipelineRunner>());

        var vm = Services.GetRequiredService<MainViewModel>();
        var tray = Services.GetRequiredService<TrayIconHost>();
        tray.Setup(MainWindow, vm);

        MainWindow.Activate();
        StartSecondInstanceListener(MainWindow);
        vm.StartAudioProcessPolling();
    }

    public void RequestQuit()
    {
        var window = MainWindow;
        if (window == null) return;

        window.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await Services.GetRequiredService<MainViewModel>().StopCommand.ExecuteAsync(null); } catch { }
            if (MainWindow is MainWindow mainWindow)
                mainWindow.MarkExiting();
            try { Services.GetRequiredService<TrayIconHost>().Dispose(); } catch { }
            Exit();
        });
    }

    private static void StartSecondInstanceListener(Window window)
    {
        var showWindow = Program.ShowWindowEvent;
        if (showWindow == null) return;

        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    showWindow.WaitOne();
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (window is MainWindow mainWindow)
                        {
                            mainWindow.RestoreFromTray();
                        }
                        else
                        {
                            window.Activate();
                        }
                    });
                }
                catch
                {
                    break;
                }
            }
        });
    }
}
