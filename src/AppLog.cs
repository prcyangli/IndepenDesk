using System.Diagnostics;

namespace IndepenDesk;

internal static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndepenDesk",
        $"IndepenDesk-session-{GetSessionId()}.log");

    public static void Error(string operation, Exception exception) =>
        Write("ERROR", operation, exception.ToString());

    public static void Warning(string operation, string message) =>
        Write("WARN", operation, message);

    private static void Write(string level, string operation, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length >= MaxLogBytes)
                    File.Move(LogFile, LogFile + ".previous", overwrite: true);
                File.AppendAllText(LogFile,
                    $"{DateTimeOffset.Now:O} [{level}] {operation}: {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never make window recovery or application shutdown fail.
        }
    }

    private static int GetSessionId()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }
}
