using System.Diagnostics;

namespace IndepenDesk;

/// <summary>
/// Diagnostic log with an in-memory queue drained by a background writer thread.
/// Callers (the UI thread, hotkey handlers, switch transactions) never pay the
/// synchronous disk cost; lines are formatted at enqueue time so their timestamps
/// stay accurate even if the writer is briefly busy.
/// </summary>
internal static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int QueueCapacity = 4096;

    // Guards Pending/_shutdown/_droppedLines only (fast operations); never held
    // during file I/O so Enqueue can never block on a slow disk.
    private static readonly object Gate = new();
    // Guards the log file (writer thread + post-shutdown synchronous writes).
    private static readonly object FileGate = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndepenDesk",
        $"IndepenDesk-session-{GetSessionId()}.log");
    private static readonly Queue<string> Pending = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly Thread Writer = new(WriterLoop)
    {
        IsBackground = true,
        Name = "IndepenDesk-Log",
        Priority = ThreadPriority.BelowNormal
    };
    private static bool _shutdown;
    private static long _droppedLines;

    static AppLog() => Writer.Start();

    public static void Error(string operation, Exception exception) =>
        Enqueue("ERROR", operation, exception.ToString());

    public static void Warning(string operation, string message) =>
        Enqueue("WARN", operation, message);

    public static void Info(string operation, string message) =>
        Enqueue("INFO", operation, message);

    /// <summary>Drain the queue synchronously; idempotent. Call before process exit.</summary>
    public static void Shutdown()
    {
        List<string> batch;
        lock (Gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            batch = Drain();
        }
        WriteBatch(batch);
        Wake.Set();
    }

    private static void Enqueue(string level, string operation, string message)
    {
        string line = $"{DateTimeOffset.Now:O} [{level}] {operation}: {message}";
        lock (Gate)
        {
            if (_shutdown)
            {
                WriteSynchronously(line);
                return;
            }
            if (Pending.Count >= QueueCapacity)
            {
                _droppedLines++;
                return;
            }
            Pending.Enqueue(line);
        }
        Wake.Set();
    }

    private static void WriterLoop()
    {
        while (true)
        {
            Wake.WaitOne();
            List<string> batch;
            bool shutdown;
            lock (Gate)
            {
                batch = Drain();
                shutdown = _shutdown;
            }
            if (batch.Count > 0)
                WriteBatch(batch);
            if (shutdown)
            {
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                }
            }
        }
    }

    private static List<string> Drain()
    {
        var batch = new List<string>(Pending.Count);
        while (Pending.Count > 0)
            batch.Add(Pending.Dequeue());
        return batch;
    }

    private static void WriteSynchronously(string line)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never make window recovery or application shutdown fail.
        }
    }

    private static void WriteBatch(List<string> batch)
    {
        try
        {
            lock (FileGate)
            {
                long dropped = Interlocked.Exchange(ref _droppedLines, 0);
                if (dropped > 0)
                    batch.Insert(0, $"{DateTimeOffset.Now:O} [WARN] AppLog: dropped {dropped} log line(s) while the queue was full.");
                if (batch.Count == 0) return;
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length >= MaxLogBytes)
                    File.Move(LogFile, LogFile + ".previous", overwrite: true);
                File.AppendAllText(LogFile, string.Join(Environment.NewLine, batch) + Environment.NewLine);
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
