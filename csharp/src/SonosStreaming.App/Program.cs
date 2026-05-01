using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
#if !WINDOWS_APP_SDK_SELF_CONTAINED
using Microsoft.Windows.ApplicationModel.DynamicDependency;
#endif

namespace SonosStreaming.App;

public static class Program
{
    private const string InstanceMutexName = @"Local\RoomRelay.SingleInstance";
    private const string ShowWindowEventName = @"Local\RoomRelay.ShowWindow";
    private static Mutex? _instanceMutex;

    internal static EventWaitHandle? ShowWindowEvent { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var showWindow = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showWindow.Set();
            }
            catch
            {
                // If the first instance is still starting up, just avoid
                // launching a duplicate process.
            }

            return;
        }

        ShowWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

#if !WINDOWS_APP_SDK_SELF_CONTAINED
        Bootstrap.Initialize(0x00020000);
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
