namespace LanDoorbell;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\LanDoorbellPro.SingleInstance";
    private const string ActivateEventName = @"Local\LanDoorbellPro.ShowSettings";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask the running copy to open Settings, then quit quietly.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* the other copy is still starting */ }
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved background task exception", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Log.Info($"{AppInfo.Name} {AppInfo.Version} starting");

        using var activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        Application.Run(new DoorbellContext(activateSignal));
        GC.KeepAlive(mutex);
    }

    private static void ReportCrash(Exception ex)
    {
        Log.Error("Unhandled exception", ex);
        MessageBox.Show(
            $"Something went wrong:\n\n{ex.Message}\n\nThe app will keep running. Details were saved to:\n{Log.FilePath}",
            AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
