using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Tailshare.App;

public static class Program
{
    private const string InstanceKey = "tailshare-main";

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint milliseconds, ulong handleCount, IntPtr[] handles, out uint index);

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // A second launch, or a share-sheet activation while we run, goes to the running instance.
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = new EventWaitHandle(false, EventResetMode.ManualReset);
            _ = Task.Run(() =>
            {
                instance.RedirectActivationToAsync(args).AsTask().Wait();
                done.Set();
            });
            // Pump COM while waiting: the redirect of a share-target activation needs this thread's apartment.
            CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
            return 0;
        }

        Application.Start(p =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            var host = new AppHost(queue);
            instance.Activated += (_, e) => queue.TryEnqueue(() => host.OnActivated(e));
            _ = new App();
            var first = AppInstance.GetCurrent().GetActivatedEventArgs();
            queue.TryEnqueue(() => host.Start(first));
        });
        return 0;
    }
}
