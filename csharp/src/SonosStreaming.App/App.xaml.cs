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
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayIconHost>();
        Services = services.BuildServiceProvider();

        this.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RoomRelay", "crash.txt");
            try { File.WriteAllText(path, $"[{DateTime.Now}] UnhandledException\n{e.Exception}\n"); } catch { }
            Log.Fatal(e.Exception, "Unhandled WinUI exception");
            Log.CloseAndFlush();
        };

        this.InitializeComponent();
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
