using System.Diagnostics;

namespace IndepenDesk;

/// <summary>Best-effort cleanup for abandoned, session-scoped recovery artifacts.</summary>
internal static class StorageMaintenance
{
    private static readonly TimeSpan SessionFileRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan TemporaryFileRetention = TimeSpan.FromDays(2);

    public static void Cleanup()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IndepenDesk");
            if (!Directory.Exists(directory)) return;

            HashSet<int> activeSessions = GetActiveSessionIds();
            DateTime now = DateTime.UtcNow;

            DeleteAbandonedSessionFiles(directory, "hidden-*.json*", "hidden-", activeSessions, now);
            DeleteAbandonedSessionFiles(directory, "IndepenDesk-session-*.log*",
                "IndepenDesk-session-", activeSessions, now);
            DeleteOldTemporaryFiles(directory, "hidden-*.tmp", now);
            DeleteOldTemporaryFiles(directory, "settings.json.*.tmp", now);
        }
        catch (Exception ex)
        {
            AppLog.Warning(nameof(StorageMaintenance), ex.Message);
        }
    }

    private static HashSet<int> GetActiveSessionIds()
    {
        var sessions = new HashSet<int>();
        using (Process current = Process.GetCurrentProcess())
            sessions.Add(current.SessionId);
        foreach (string processName in new[] { "explorer", "IndepenDesk" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try { sessions.Add(process.SessionId); }
                    catch { }
                }
            }
        }
        return sessions;
    }

    private static void DeleteAbandonedSessionFiles(string directory, string pattern,
        string prefix, HashSet<int> activeSessions, DateTime now)
    {
        foreach (string path in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                if (!TryReadSessionId(Path.GetFileName(path), prefix, out int sessionId) ||
                    activeSessions.Contains(sessionId) ||
                    now - File.GetLastWriteTimeUtc(path) < SessionFileRetention)
                    continue;
                File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Warning(nameof(DeleteAbandonedSessionFiles),
                    $"Could not delete '{path}': {ex.Message}");
            }
        }
    }

    private static bool TryReadSessionId(string fileName, string prefix, out int sessionId)
    {
        sessionId = -1;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> remainder = fileName.AsSpan(prefix.Length);
        int digitCount = 0;
        while (digitCount < remainder.Length && char.IsDigit(remainder[digitCount]))
            digitCount++;
        return digitCount > 0 && int.TryParse(remainder[..digitCount], out sessionId);
    }

    private static void DeleteOldTemporaryFiles(string directory, string pattern, DateTime now)
    {
        foreach (string path in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(path) >= TemporaryFileRetention)
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Warning(nameof(DeleteOldTemporaryFiles),
                    $"Could not delete '{path}': {ex.Message}");
            }
        }
    }
}
