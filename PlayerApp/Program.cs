using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PlayerApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length > 0 && args[0] == "--media-worker")
        {
            if (args.Length != 2)
            {
                return 2;
            }
            // The supervisor records failures and restores IPC; Windows must not show
            // a crash dialog for this non-interactive child process.
            _ = SetErrorMode(0x0001 | 0x0002);
            try
            {
                Task.Run(() => MediaWorker.RunAsync(args[1])).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                MediaLog.Write($"Worker stopped: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
