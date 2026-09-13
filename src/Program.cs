using System.Security.Principal;

namespace IndepenDesk;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppLog.Info(nameof(Main),
            $"IndepenDesk v{UpdateChecker.CurrentVersion} starting " +
            $"({(Environment.Is64BitProcess ? "x64" : "x86")}; {Environment.OSVersion.VersionString}; " +
            $"packaged={StartupManager.IsPackaged}; \"{Environment.ProcessPath ?? "?"}\").");

        ApplicationConfiguration.Initialize();

        using var mutex = CreateSingleInstanceMutex(out bool isNew);
        if (!isNew)
        {
            AppLog.Warning(nameof(Main), "Another instance is already running; exiting after the notice.");
            MessageBox.Show(L.T("msg.alreadyRunning"), "IndepenDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        StorageMaintenance.Cleanup();

        TrayApp? app = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI thread", e.Exception);
            app?.ExitThread();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                AppLog.Error("Unhandled exception", exception);
            else
                AppLog.Warning("Unhandled exception", e.ExceptionObject?.ToString() ?? "Unknown error");
        };

        try
        {
            app = new TrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(Main), ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            app?.Dispose();
        }
    }

    private static string GetSingleInstanceName()
    {
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
                return $@"Global\IndepenDesk_{sid}";
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(GetSingleInstanceName), ex);
        }
        return $@"Local\IndepenDesk_{Environment.UserName}";
    }

    private static Mutex CreateSingleInstanceMutex(out bool isNew)
    {
        string name = GetSingleInstanceName();
        try
        {
            return new Mutex(true, name, out isNew);
        }
        catch (UnauthorizedAccessException ex) when (name.StartsWith("Global\\", StringComparison.Ordinal))
        {
            AppLog.Warning(nameof(CreateSingleInstanceMutex),
                $"Could not create the global mutex; using a session-local mutex. {ex.Message}");
            return new Mutex(true, name.Replace("Global\\", "Local\\", StringComparison.Ordinal), out isNew);
        }
    }
}
