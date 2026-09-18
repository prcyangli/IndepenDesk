using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IndepenDesk;

/// <summary>Geçiş bilgisi: OSD için.</summary>
internal sealed record SwitchInfo(string Device, int LocalIndex);

internal sealed record WindowEntry(IntPtr Handle, string Title);
internal sealed record DesktopEntry(int LocalIndex, bool IsCurrent, IReadOnlyList<WindowEntry> Windows);
internal sealed record MonitorEntry(string Device, int Ordinal, IReadOnlyList<DesktopEntry> Desktops);

/// <summary>A skipped unmanageable window (handle, process name, title, reason), for display by the notification layer.</summary>
internal sealed record UnmanageableWindowInfo(IntPtr Handle, string ProcessName, string Title, string Reason);

/// <summary>
/// Monitör başına bağımsız sanal masaüstü yöneticisi.
/// Windows'un global sanal masaüstü sistemini kullanmaz; bunun yerine her monitör için
/// pencere setleri tutar ve geçişlerde yalnızca o monitördeki pencereleri gizler/gösterir.
/// Masaüstleri dinamiktir ve her monitörde bağımsız sayıdadır. Arayüzde yerel numaralar,
/// Ctrl+Alt+1..9 kısayollarında ise ekran sırasına göre global numaralar kullanılır.
/// </summary>
internal sealed class DesktopManager
{
    public const int MaxDesktopsPerMonitor = 9;

    private sealed class MonitorState
    {
        public required string Device;
        public List<HashSet<IntPtr>> Desktops = new() { new HashSet<IntPtr>() };
        public List<IntPtr> LastActive = new() { IntPtr.Zero };
        public int Current;
    }

    private sealed record HiddenWindowRecord(
        long Handle,
        int PointerSize,
        uint ProcessId,
        long ProcessStartTimeUtcTicks,
        int SessionId,
        string ClassName,
        bool Parked,
        int NormalLeft,
        int NormalTop,
        int NormalRight,
        int NormalBottom,
        int SavedShowCmd,
        string? ParkMonitor);

    /// <summary>A window that cannot be managed safely (hidden/parked); once skipped it stays visible and follows the current desktop.</summary>
    private sealed record UnmanageableWindow(uint ProcessId, string ProcessName, string ClassName,
        string Reason, string Title);

    private sealed record HiddenStateFile(
        int Version,
        int SessionId,
        int PointerSize,
        IReadOnlyList<HiddenWindowRecord> Windows);

    private readonly record struct ObservedProcessIdentity(
        long ProcessStartTimeUtcTicks,
        int SessionId);

    private const int HiddenStateVersion = 3;
    private const string EmptyPersistedState = "<empty>";

    // A park verification failure may just be a transient refusal by the window (it is being
    // dragged, the app clamps its position, etc.): on failure the whole batch backs off and
    // retries, at most 6 attempts with 100 ms between rounds, adding ≤500 ms to a switch.
    private const int ParkAttemptCount = 6;
    private const int ParkRetryDelayMs = 100;
    private const int ParkAnchorThickness = 2;
    private const int ParkRectTolerance = 2;
    private const int MinimizeForegroundSuppressMs = 750;

    private readonly Dictionary<string, MonitorState> _monitors = new();
    private readonly Dictionary<IntPtr, HiddenWindowRecord> _hidden = new();
    private readonly HashSet<HashSet<IntPtr>> _retainedEmptyDesktops = new(ReferenceEqualityComparer.Instance);
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly int _sessionId = GetCurrentSessionId();
    private readonly string _stateFile;
    private string? _lastPersisted;
    private long _hiddenVersion;
    private long _persistedHiddenVersion = -1;
    private readonly Dictionary<uint, ObservedProcessIdentity?> _processIdentityCache = new();
    private int _identityCacheDepth;
    private readonly Dictionary<IntPtr, UnmanageableWindow> _unmanageable = new();
    private bool _syncInProgress;
    private bool _stateFileBlocked;
    private bool _switchInProgress;
    private IntPtr _minimizeSource;
    private long _suppressParkedForegroundUntil;
    private IntPtr _explicitMinimizeRestore;
    private long _explicitMinimizeRestoreUntil;
    private readonly Dictionary<IntPtr, long> _internalMinimizeUntil = new();

    /// <summary>Whether we run with administrator rights; once elevated we can control all windows of the same user, so no pre-check is needed.</summary>
    private static readonly bool OwnProcessElevated = Native.IsOwnProcessElevated();

    /// <summary>
    /// Shared taskbar mode: windows on non-current desktops are not hidden but parked off-screen.
    /// They stay in the taskbar/Alt-Tab; activating one jumps to its desktop automatically.
    /// </summary>
    public bool SharedTaskbar { get; private set; }

    /// <summary>
    /// Default (hidden) mode: when an app activates a window that belongs to another desktop
    /// (taskbar/pinned or tray icon click of a single-instance app), jump to that desktop
    /// instead of adopting the re-shown window into the current one.
    /// </summary>
    public bool TaskbarJump { get; private set; }

    /// <summary>Geçiş kesinleşti, pencereler henüz gizlenmedi: (cihaz, eski index, yeni index).
    /// Animasyon katmanının ekran görüntüsünü bu anda alması gerekir.</summary>
    public event Action<string, int, int>? SwitchStarting;

    /// <summary>Geçiş tamamlandı (veya uçta OSD tazelemesi).</summary>
    public event Action<SwitchInfo>? DesktopSwitched;

    /// <summary>A window was deemed unmanageable (skipped, stays visible), or a desktop operation
    /// was cancelled (a null payload indicates a cancellation-type error).</summary>
    public event Action<UnmanageableWindowInfo?>? WindowControlFailed;

    private static readonly string[] ClassBlacklist =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "Xaml_WindowedPopupClass", "COMTASKSWINDOWCLASS"
    };

    public DesktopManager()
    {
        SharedTaskbar = SettingsStore.GetBool("sharedTaskbar", false);
        TaskbarJump = SettingsStore.GetBool("taskbarJump", true);
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndepenDesk");
        Directory.CreateDirectory(dir);
        // A user can have multiple interactive Windows sessions. Keep journals
        // session-scoped so a fallback local mutex or post-crash launch in another
        // session cannot overwrite the original session's recovery state.
        _stateFile = Path.Combine(dir, $"hidden-{_sessionId}.json");
        string legacyStateFile = Path.Combine(dir, "hidden.json");
        if (File.Exists(legacyStateFile))
            AppLog.Warning(nameof(DesktopManager),
                $"Preserved unverified legacy recovery data at '{legacyStateFile}'.");
        RecoverPreviousSession();
    }

    // ---------- kalıcılık ve kurtarma ----------

    private void RecoverPreviousSession()
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                _lastPersisted = EmptyPersistedState;
                _persistedHiddenVersion = _hiddenVersion;
                return;
            }

            var state = JsonSerializer.Deserialize<HiddenStateFile>(File.ReadAllText(_stateFile));
            if (state == null || state.Version is < 1 or > HiddenStateVersion ||
                state.SessionId != _sessionId || state.PointerSize != IntPtr.Size)
            {
                AppLog.Warning(nameof(RecoverPreviousSession),
                    "Ignored an incompatible or stale hidden-window journal.");
                QuarantineStateFile("incompatible");
                return;
            }

            foreach (var record in state.Windows)
            {
                if (!TryGetHandle(record, out IntPtr hwnd) || !MatchesWindowIdentity(hwnd, record))
                    continue;
                SetHiddenRecord(hwnd, record);
                if (!ShowOrUnparkManagedWindow(hwnd))
                {
                    AppLog.Warning(nameof(RecoverPreviousSession),
                        $"Could not restore HWND={hwnd}; keeping it in the recovery journal.");
                }
            }
            _lastPersisted = null;
            PersistHidden();
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(RecoverPreviousSession), ex);
            QuarantineStateFile("unreadable");
        }
    }

    private void QuarantineStateFile(string reason)
    {
        try
        {
            if (File.Exists(_stateFile))
                File.Move(_stateFile, _stateFile + $".{reason}", overwrite: true);
            _lastPersisted = EmptyPersistedState;
            _persistedHiddenVersion = _hiddenVersion;
        }
        catch (Exception ex)
        {
            // Do not overwrite or delete recovery evidence that could not be
            // understood and could not be moved out of the active path.
            _stateFileBlocked = true;
            _lastPersisted = null;
            AppLog.Error(nameof(QuarantineStateFile), ex);
        }
    }

    private bool PersistHidden()
    {
        if (_hiddenVersion == _persistedHiddenVersion) return true;
        if (!PersistHiddenSnapshot(_hidden.Values)) return false;
        _persistedHiddenVersion = _hiddenVersion;
        return true;
    }

    private void SetHiddenRecord(IntPtr h, HiddenWindowRecord record)
    {
        if (_hidden.TryGetValue(h, out var existing) && existing == record) return;
        _hidden[h] = record;
        _hiddenVersion++;
    }

    private bool RemoveHiddenRecord(IntPtr h)
    {
        if (!_hidden.Remove(h)) return false;
        _hiddenVersion++;
        return true;
    }

    private bool PersistHiddenSnapshot(IEnumerable<HiddenWindowRecord> records)
    {
        if (_stateFileBlocked) return false;
        string tmp = _stateFile + $".{Environment.ProcessId}.tmp";
        try
        {
            var ordered = records
                .OrderBy(r => r.ProcessId)
                .ThenBy(r => r.Handle)
                .ToList();

            if (ordered.Count == 0)
            {
                if (_lastPersisted == EmptyPersistedState && !File.Exists(_stateFile))
                    return true;
                File.Delete(_stateFile);
                try { File.Delete(tmp); } catch { }
                _lastPersisted = EmptyPersistedState;
                return true;
            }

            string json = JsonSerializer.Serialize(new HiddenStateFile(
                HiddenStateVersion, _sessionId, IntPtr.Size, ordered));
            if (json == _lastPersisted)
                return true;

            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_stateFile))
                File.Replace(tmp, _stateFile, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, _stateFile);

            _lastPersisted = json;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(PersistHidden), ex);
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    private static int GetCurrentSessionId()
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

    private bool TryCaptureWindowIdentity(IntPtr h, out HiddenWindowRecord record)
    {
        record = null!;
        try
        {
            if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 ||
                pid == 0 || !Native.IsWindow(h)) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            string className = Native.GetWindowClass(h);
            if (className.Length == 0) return false;

            if (Native.GetWindowThreadProcessId(h, out uint verifiedPid) == 0 ||
                verifiedPid != pid || !Native.IsWindow(h)) return false;

            var placement = new Native.WINDOWPLACEMENT
            {
                length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
            };
            bool hasPlacement = Native.GetWindowPlacement(h, ref placement);
            Rectangle normal = hasPlacement
                ? FromRECT(placement.rcNormalPosition)
                : Native.GetWindowRect(h, out var rect)
                    ? FromRECT(rect)
                    : Rectangle.Empty;
            string? monitor = Native.GetMonitorDeviceOfWindow(h);

            record = new HiddenWindowRecord(
                h.ToInt64(),
                IntPtr.Size,
                pid,
                process.StartTime.ToUniversalTime().Ticks,
                process.SessionId,
                className,
                Parked: false,
                NormalLeft: normal.Left,
                NormalTop: normal.Top,
                NormalRight: normal.Right,
                NormalBottom: normal.Bottom,
                SavedShowCmd: hasPlacement ? (int)placement.showCmd : 0,
                ParkMonitor: monitor);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning(nameof(TryCaptureWindowIdentity), $"HWND={h}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetHandle(HiddenWindowRecord record, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (record.PointerSize != IntPtr.Size) return false;
        try
        {
            handle = new IntPtr(record.Handle);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private bool MatchesWindowIdentity(IntPtr h, HiddenWindowRecord record)
    {
        try
        {
            if (!Native.IsWindow(h) || Native.GetWindowClass(h) != record.ClassName)
                return false;
            if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 ||
                pid != record.ProcessId) return false;

            ObservedProcessIdentity? identity;
            if (_identityCacheDepth > 0 && _processIdentityCache.TryGetValue(pid, out identity))
            {
                return identity is { } cached && cached.SessionId == record.SessionId &&
                       cached.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks;
            }

            using var process = Process.GetProcessById(checked((int)pid));
            identity = new ObservedProcessIdentity(
                process.StartTime.ToUniversalTime().Ticks,
                process.SessionId);
            if (_identityCacheDepth > 0)
                _processIdentityCache[pid] = identity;
            return identity.Value.SessionId == record.SessionId &&
                   identity.Value.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks;
        }
        catch (Exception ex)
        {
            if (Native.IsWindow(h))
                AppLog.Warning(nameof(MatchesWindowIdentity),
                    $"Identity check failed for a live HWND={h}: {ex.GetType().Name}: {ex.Message}.");
            if (_identityCacheDepth > 0 &&
                Native.GetWindowThreadProcessId(h, out uint failedPid) != 0 && failedPid != 0)
                _processIdentityCache[failedPid] = null;
            return false;
        }
    }

    /// <summary>
    /// Hides the manageable windows of a desktop. Windows that are known to be
    /// outside our integrity boundary are skipped and stay visible. Operational
    /// failures are transient: roll this attempt back and let a later switch retry.
    /// </summary>
    private bool HideManagedWindows(IEnumerable<IntPtr> handles)
    {
        var candidates = new List<(IntPtr Handle, HiddenWindowRecord Record)>();
        foreach (IntPtr h in handles.Distinct())
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) continue;
            if (IsKnownUnmanageable(h))
            {
                NotifyKnownUnmanageable(h);
                continue;
            }
            // A failed mode rollback can leave an off-screen parking record that
            // still contains the only safe restore geometry. Preserve that record
            // when converting the window to ordinary hidden-mode visibility.
            if (_hidden.TryGetValue(h, out var parked) && parked.Parked &&
                MatchesWindowIdentity(h, parked))
            {
                candidates.Add((h, parked));
                continue;
            }
            if (ShouldSkipForIntegrity(h, out string reason))
            {
                RegisterUnmanageable(h, reason);
                continue;
            }
            if (!TryCaptureWindowIdentity(h, out var record))
            {
                if (!Native.IsWindow(h)) continue; // destroyed while being captured
                RaiseWindowControlWarning(nameof(HideManagedWindows),
                    "Cannot hide the current desktop safely: window state capture failed; switch cancelled and will be retried",
                    h);
                return false;
            }
            candidates.Add((h, record));
        }

        if (candidates.Count == 0) return true;

        var planned = _hidden.Values.ToDictionary(r => r.Handle);
        foreach (var candidate in candidates)
            planned[candidate.Record.Handle] = candidate.Record;
        if (!PersistHiddenSnapshot(planned.Values))
        {
            RaiseWindowControlWarning(nameof(HideManagedWindows),
                "Cannot hide the current desktop safely: the recovery snapshot could not be persisted");
            return false;
        }

        var previousRecords = candidates
            .Where(c => _hidden.ContainsKey(c.Handle))
            .ToDictionary(c => c.Handle, c => _hidden[c.Handle]);
        var touched = new List<IntPtr>();
        var pending = new List<(IntPtr Handle, HiddenWindowRecord Record)>();
        foreach (var candidate in candidates)
        {
            // The durable snapshot already contains this record. Add it to the
            // in-memory journal before touching the HWND so rollback can recover
            // even when verification itself fails.
            SetHiddenRecord(candidate.Handle, candidate.Record);
            touched.Add(candidate.Handle);
            if (TryHideOnce(candidate) || RouteHideOutcome(candidate, touched))
                continue;
            pending.Add(candidate);
        }

        // Batched backoff retry (mirrors ParkManagedWindows): a transient refusal
        // (the window is being dragged, the app clamps its position) must not cancel
        // the switch on the first try; the whole group waits only once per round.
        for (int attempt = 2; pending.Count > 0 && attempt <= ParkAttemptCount; attempt++)
        {
            AppLog.Info(nameof(HideManagedWindows),
                $"Hide verification failed for {pending.Count} window(s) " +
                $"({string.Join(", ", pending.Select(c => c.Handle))}); " +
                $"retry {attempt}/{ParkAttemptCount} scheduled in {ParkRetryDelayMs} ms.");
            Thread.Sleep(ParkRetryDelayMs);
            var remaining = new List<(IntPtr Handle, HiddenWindowRecord Record)>();
            foreach (var candidate in pending)
            {
                if (TryHideOnce(candidate) || RouteHideOutcome(candidate, touched))
                    continue;
                remaining.Add(candidate);
            }
            pending = remaining;
        }

        if (pending.Count > 0)
        {
            bool rolledBack = RollBackHiddenAttempt(touched, previousRecords);
            PersistHidden();
            RaiseWindowControlWarning(nameof(HideManagedWindows),
                rolledBack
                    ? "A normal window could not be hidden; switch cancelled and all changed windows were restored"
                    : "A normal window could not be hidden; switch cancelled but rollback was incomplete (recovery records retained)",
                pending[0].Handle);
            return false;
        }

        PersistHidden();
        return true;
    }

    /// <summary>One hide attempt: SW_HIDE followed by the visibility + identity verification.</summary>
    private bool TryHideOnce((IntPtr Handle, HiddenWindowRecord Record) candidate)
    {
        Native.ShowWindow(candidate.Handle, Native.SW_HIDE);
        return !Native.IsWindowVisible(candidate.Handle) &&
               MatchesWindowIdentity(candidate.Handle, candidate.Record);
    }

    /// <summary>Handle a terminal hide outcome (window destroyed or handle reused);
    /// returns true when the candidate needs no further retries.</summary>
    private bool RouteHideOutcome((IntPtr Handle, HiddenWindowRecord Record) candidate, List<IntPtr> touched)
    {
        if (!Native.IsWindow(candidate.Handle))
        {
            RemoveHiddenRecord(candidate.Handle);
            touched.Remove(candidate.Handle);
            return true;
        }
        if (!MatchesWindowIdentity(candidate.Handle, candidate.Record))
        {
            RemoveHiddenRecord(candidate.Handle);
            touched.Remove(candidate.Handle);
            // Hidden but the handle was reused: bring the replacement back on screen.
            if (!Native.IsWindowVisible(candidate.Handle))
                Native.ShowWindow(candidate.Handle, Native.SW_SHOWNA);
            return true;
        }
        return false; // transient refusal — retriable
    }

    private bool RollBackHiddenAttempt(IEnumerable<IntPtr> handles,
        IReadOnlyDictionary<IntPtr, HiddenWindowRecord> previousRecords)
    {
        bool success = true;
        foreach (IntPtr h in handles.Reverse())
        {
            if (!Native.IsWindow(h))
            {
                RemoveHiddenRecord(h);
                continue;
            }

            if (previousRecords.TryGetValue(h, out var previous))
            {
                // This was already a journaled off-screen window before the mode
                // conversion attempt. Restore only its visibility and retain the
                // original recovery geometry.
                if (!Native.IsWindowVisible(h))
                    Native.ShowWindow(h, Native.SW_SHOWNA);
                SetHiddenRecord(h, previous);
                success &= Native.IsWindowVisible(h);
                continue;
            }

            success &= ShowManagedWindow(h);
        }
        return success;
    }

    private void RaiseWindowControlWarning(string operation, string detail, IntPtr handle = default)
    {
        AppLog.Warning(operation,
            handle != IntPtr.Zero ? $"{detail}; window: {DescribeWindow(handle)}" : detail);
        // Cancellation-type failures carry no per-window balloon payload; the
        // skip path (RegisterUnmanageable) reports windows individually.
        WindowControlFailed?.Invoke(null);
    }

    /// <summary>Whether the handle is already known to be unmanageable; if the handle was reused (PID/class name mismatch), the entry is invalidated and the window re-probed.</summary>
    private bool IsKnownUnmanageable(IntPtr h)
    {
        if (!_unmanageable.TryGetValue(h, out var info)) return false;
        if (!Native.IsWindow(h) ||
            Native.GetWindowThreadProcessId(h, out uint pid) == 0 ||
            pid != info.ProcessId || Native.GetWindowClass(h) != info.ClassName)
        {
            _unmanageable.Remove(h);
            return false;
        }
        return true;
    }

    /// <summary>First-time registration of a window across the integrity boundary that is definitely unmanageable.</summary>
    private void RegisterUnmanageable(IntPtr h, string reason)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        string processName = "unknown";
        try
        {
            if (pid != 0)
                using (var process = Process.GetProcessById(checked((int)pid)))
                    processName = process.ProcessName;
        }
        catch { }
        string title = Native.GetWindowTitle(h);
        if (title.Length > 60) title = title[..60] + "…";
        string className = Native.GetWindowClass(h);
        _unmanageable[h] = new UnmanageableWindow(pid, processName, className, reason, title);

        AppLog.Warning(nameof(RegisterUnmanageable),
            $"Skipped unmanageable window (reason={reason}): HWND={h}, title='{title}', " +
            $"class='{className}', process='{processName}' (PID {pid}); it stays visible.");
        WindowControlFailed?.Invoke(new UnmanageableWindowInfo(h, processName, title, reason));
    }

    /// <summary>A known unmanageable window is skipped/operated on again: do not log again, only re-raise
    /// the notification event (the tray layer de-duplicates per session; when notifications were disabled,
    /// re-enabling lets the next skip show the balloon again).</summary>
    private void NotifyKnownUnmanageable(IntPtr h)
    {
        if (_unmanageable.TryGetValue(h, out var info))
            WindowControlFailed?.Invoke(new UnmanageableWindowInfo(h, info.ProcessName, info.Title, info.Reason));
    }

    /// <summary>
    /// Detect "target window belongs to an elevated process while we are not elevated": UIPI makes
    /// such windows immune to ShowWindow/SetWindowPos, so skip them up front; if the probe is
    /// inconclusive, skip conservatively.
    /// </summary>
    private bool ShouldSkipForIntegrity(IntPtr h, out string reason)
    {
        reason = "";
        if (OwnProcessElevated) return false;
        if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 || pid == 0 || pid == _ownPid)
            return false;
        switch (Native.IsProcessElevated(pid))
        {
            case true:
                reason = "elevated";
                return true;
            case null:
                reason = "elevated-or-protected";
                return true;
            default:
                return false;
        }
    }

    private static string DescribeWindow(IntPtr h)
    {
        try
        {
            if (!Native.IsWindow(h)) return $"HWND={h} (window no longer exists)";
            string title = Native.GetWindowTitle(h);
            if (title.Length > 60) title = title[..60] + "…";
            string className = Native.GetWindowClass(h);
            if (Native.GetWindowThreadProcessId(h, out uint pid) != 0 && pid != 0)
            {
                string processName = "unknown";
                try
                {
                    using var process = Process.GetProcessById(checked((int)pid));
                    processName = process.ProcessName;
                }
                catch { }
                return $"HWND={h}, title='{title}', class='{className}', process='{processName}' (PID {pid})";
            }
            return $"HWND={h}, title='{title}', class='{className}'";
        }
        catch (Exception ex)
        {
            return $"HWND={h} (details unavailable: {ex.Message})";
        }
    }

    private bool ShowManagedWindow(IntPtr h)
    {
        if (!_hidden.TryGetValue(h, out var record)) return false;
        if (!MatchesWindowIdentity(h, record))
        {
            RemoveHiddenRecord(h);
            return false;
        }
        // A hidden maximized window can keep its live maximized rectangle on the
        // source display even after rcNormalPosition was mapped to another one.
        // Always materialize the recorded restore placement before showing it;
        // checking only IsWindowOffScreen would expose it on the old display.
        if (HasSavedPlacement(record) && !RestoreParkedWindow(h, record))
        {
            AppLog.Warning(nameof(ShowManagedWindow),
                $"Could not restore HWND={h} to its recorded display.");
            return false;
        }
        if (!Native.IsWindowVisible(h))
        {
            Native.ShowWindow(h, Native.SW_SHOWNA);
            if (!Native.IsWindowVisible(h))
            {
                AppLog.Warning(nameof(ShowManagedWindow), $"Could not show HWND={h}.");
                return false;
            }
        }
        RemoveHiddenRecord(h);
        return true;
    }

    // ---------- Shared taskbar mode: off-screen parking ----------

    private static Rectangle FromRECT(Native.RECT r) =>
        Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private static Native.RECT ToRECT(Rectangle r) => new()
    {
        Left = r.Left,
        Top = r.Top,
        Right = r.Right,
        Bottom = r.Bottom
    };

    private static bool HasSavedPlacement(HiddenWindowRecord record) =>
        record.NormalRight > record.NormalLeft && record.NormalBottom > record.NormalTop;

    private static bool IntersectsAnyScreen(Rectangle r)
    {
        foreach (var s in Screen.AllScreens)
            if (s.Bounds.IntersectsWith(r))
                return true;
        return false;
    }

    /// <summary>Whether a parked window (or its minimized restore position) lies completely outside all displays.</summary>
    private static bool IsWindowOffScreen(IntPtr h)
    {
        if (Native.IsIconic(h))
        {
            var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(h, ref pl)) return false;
            return !IntersectsAnyScreen(FromRECT(pl.rcNormalPosition));
        }
        if (!Native.GetWindowRect(h, out var wr)) return false;
        return !IntersectsAnyScreen(FromRECT(wr));
    }

    private static Screen? NearestScreen(Rectangle r)
    {
        Screen? best = null;
        int bestDist = int.MaxValue;
        foreach (var s in Screen.AllScreens)
        {
            int dx = Math.Max(0, Math.Max(s.Bounds.Left - r.Right, r.Left - s.Bounds.Right));
            int dy = Math.Max(0, Math.Max(s.Bounds.Top - r.Bottom, r.Top - s.Bounds.Bottom));
            int d = dx + dy;
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }
        return best;
    }

    /// <summary>
    /// Compute a parking rectangle for the given display that intersects no display. Prefer the left
    /// side of the target display (so the taskbar button stays attributed to that display), then the
    /// right side, and finally a far-away fallback region.
    /// </summary>
    private static Rectangle GetParkRect(string? device, Size size)
    {
        Screen? target = device != null
            ? Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device)
            : null;

        int[] offsets = { 2000, 4000, 8000, 16000, 32000 };
        if (target != null)
        {
            foreach (int off in offsets)
            {
                var r = new Rectangle(target.Bounds.Left - off - size.Width, target.Bounds.Top,
                    size.Width, size.Height);
                if (!IntersectsAnyScreen(r)) return r;
            }
            foreach (int off in offsets)
            {
                var r = new Rectangle(target.Bounds.Right + off, target.Bounds.Top,
                    size.Width, size.Height);
                if (!IntersectsAnyScreen(r)) return r;
            }
        }

        var screens = Screen.AllScreens;
        int left = screens.Length > 0 ? screens.Min(s => s.Bounds.Left) : 0;
        int top = screens.Length > 0 ? screens.Min(s => s.Bounds.Top) : 0;
        return new Rectangle(left - 30000 - size.Width, top, Math.Max(1, size.Width), Math.Max(1, size.Height));
    }

    /// <summary>
    /// SetWindowPlacement keeps a minimized window's restore rectangle reachable and
    /// moves a completely off-screen rectangle back onto a monitor. Park minimized
    /// windows against an exposed monitor edge instead: only a one-pixel strip/corner
    /// remains in the virtual screen while the window itself stays minimized.
    /// </summary>
    private static bool TryGetMinimizedParkRect(string? device, Size size, out Rectangle park)
    {
        park = Rectangle.Empty;
        Screen? target = device != null
            ? Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device)
            : null;
        if (target == null || size.Width <= 0 || size.Height <= 0) return false;

        Rectangle b = target.Bounds;
        int w = Math.Max(1, size.Width);
        int h = Math.Max(1, size.Height);
        var candidates = new[]
        {
            // Four straight edges work well for row/column monitor layouts.
            new Rectangle(b.Left - w + 1, b.Top, w, h),
            new Rectangle(b.Right - 1, b.Top, w, h),
            new Rectangle(b.Left, b.Top - h + 1, w, h),
            new Rectangle(b.Left, b.Bottom - 1, w, h),
            // Corners minimize the visible intersection on isolated displays.
            new Rectangle(b.Left - w + 1, b.Top - h + 1, w, h),
            new Rectangle(b.Right - 1, b.Top - h + 1, w, h),
            new Rectangle(b.Left - w + 1, b.Bottom - 1, w, h),
            new Rectangle(b.Right - 1, b.Bottom - 1, w, h)
        };

        var viable = candidates
            .Where(IsEffectivelyParkedRect)
            .Where(r =>
            {
                Rectangle anchor = Rectangle.Intersect(r, b);
                return anchor.Width > 0 && anchor.Height > 0;
            })
            .OrderBy(VisibleIntersectionPixels)
            .ToList();
        if (viable.Count == 0) return false;
        park = viable[0];
        return true;
    }

    private static long VisibleIntersectionPixels(Rectangle r)
    {
        long total = 0;
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle visible = Rectangle.Intersect(r, screen.Bounds);
            if (visible.Width > 0 && visible.Height > 0)
                total += (long)visible.Width * visible.Height;
        }
        return total;
    }

    /// <summary>A parked rectangle may retain only a thin anchor on each intersected display.</summary>
    private static bool IsEffectivelyParkedRect(Rectangle r)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle visible = Rectangle.Intersect(r, screen.Bounds);
            if (visible.Width <= 0 || visible.Height <= 0) continue;
            if (visible.Width > ParkAnchorThickness && visible.Height > ParkAnchorThickness)
                return false;
        }
        return true; // A strictly off-screen rectangle is parked too.
    }

    private static bool RectApproximatelyEquals(Rectangle actual, Rectangle expected) =>
        Math.Abs(actual.Left - expected.Left) <= ParkRectTolerance &&
        Math.Abs(actual.Top - expected.Top) <= ParkRectTolerance &&
        Math.Abs(actual.Right - expected.Right) <= ParkRectTolerance &&
        Math.Abs(actual.Bottom - expected.Bottom) <= ParkRectTolerance;

    private static bool WasSavedMinimized(HiddenWindowRecord record) =>
        record.SavedShowCmd is Native.SW_SHOWMINIMIZED or Native.SW_MINIMIZE or Native.SW_SHOWMINNOACTIVE;

    private static bool TryGetEffectiveWindowRect(IntPtr h, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        if (Native.IsIconic(h))
        {
            var placement = new Native.WINDOWPLACEMENT
            {
                length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
            };
            if (!Native.GetWindowPlacement(h, ref placement)) return false;
            rect = FromRECT(placement.rcNormalPosition);
            return rect.Width > 0 && rect.Height > 0;
        }

        if (!Native.GetWindowRect(h, out var wr)) return false;
        rect = FromRECT(wr);
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>
    /// Journal-aware parking check. Ordinary user windows still use the strict
    /// IsWindowOffScreen predicate. A window that we recorded while minimized stays
    /// logically parked for as long as it remains iconic: Chromium and other apps
    /// may rewrite rcNormalPosition back on-screen without actually restoring the
    /// window. Once restored, only the thin edge-anchor representation is accepted.
    /// </summary>
    private static bool IsManagedWindowParked(IntPtr h, HiddenWindowRecord record)
    {
        if (!record.Parked) return false;
        bool iconic = Native.IsIconic(h);
        if (WasSavedMinimized(record) && iconic) return true;
        if (!TryGetEffectiveWindowRect(h, out Rectangle actual)) return false;
        if (!IntersectsAnyScreen(actual)) return true;
        if (!WasSavedMinimized(record) || !IsEffectivelyParkedRect(actual)) return false;

        var saved = Rectangle.FromLTRB(
            record.NormalLeft, record.NormalTop, record.NormalRight, record.NormalBottom);
        return TryGetMinimizedParkRect(record.ParkMonitor, saved.Size, out Rectangle expected) &&
               RectApproximatelyEquals(actual, expected);
    }

    private sealed record ParkCandidate(
        IntPtr Handle, HiddenWindowRecord Record, Rectangle ParkRect, bool WasIconic);

    private enum ParkOutcome { Parked, Failed, Destroyed, IdentityMismatch }

    /// <summary>Diagnostic snapshot of one parking attempt; it does not affect business decisions and is used only for the final WARN and retry INFO logs.</summary>
    private sealed record ParkAttemptDiag(
        bool ApiSucceeded,
        int? Win32Error,
        bool IdentityMatches,
        bool IsVisible,
        bool IsIconic,
        bool IsZoomed,
        Rectangle? WindowRect,
        Rectangle? NormalRect,
        uint? ShowCmd);

    private bool TryCaptureParkCandidate(IntPtr h, out ParkCandidate candidate)
    {
        candidate = null!;
        if (!TryCaptureWindowIdentity(h, out var identity)) return false;

        var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(h, ref pl)) return false;
        if (!Native.GetWindowRect(h, out var wr)) return false;
        string? dev = Native.GetMonitorDeviceOfWindow(h);
        if (dev == null) return false;

        var normal = FromRECT(pl.rcNormalPosition);
        bool iconic = Native.IsIconic(h);
        var size = iconic ? normal.Size : new Size(wr.Right - wr.Left, wr.Bottom - wr.Top);
        if (size.Width <= 0 || size.Height <= 0) return false;

        Rectangle park;
        if (iconic)
        {
            if (!TryGetMinimizedParkRect(dev, normal.Size, out park)) return false;
        }
        else
        {
            park = GetParkRect(dev, size);
        }

        var record = identity with
        {
            Parked = true,
            NormalLeft = normal.Left,
            NormalTop = normal.Top,
            NormalRight = normal.Right,
            NormalBottom = normal.Bottom,
            SavedShowCmd = (int)pl.showCmd,
            ParkMonitor = dev
        };
        candidate = new ParkCandidate(h, record, park, iconic);
        return true;
    }

    /// <summary>A single parking attempt: right after the API call, save the result and error code, then collect
    /// the snapshot and verify the window is fully off-screen. Only the current window state is refreshed; the
    /// recovery geometry captured first time is never touched (c.Record stays unchanged).</summary>
    private (ParkOutcome Outcome, ParkAttemptDiag? Diag) TryParkOnce(ParkCandidate c)
    {
        if (!Native.IsWindow(c.Handle)) return (ParkOutcome.Destroyed, null);
        if (!MatchesWindowIdentity(c.Handle, c.Record)) return (ParkOutcome.IdentityMismatch, null);

        bool applied;
        int? win32Error = null;
        bool iconic = Native.IsIconic(c.Handle);
        if (c.WasIconic)
        {
            if (!iconic)
            {
                Rectangle? changedRect = Native.GetWindowRect(c.Handle, out var changed)
                    ? FromRECT(changed)
                    : null;
                return (ParkOutcome.Failed,
                    new ParkAttemptDiag(false, null, true, Native.IsWindowVisible(c.Handle), false,
                        Native.IsZoomed(c.Handle), changedRect, null, null));
            }
            var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(c.Handle, ref pl))
            {
                // Keep the original flags and only change showCmd and the restore position: a minimized window stays minimized after parking.
                pl.showCmd = Native.SW_SHOWMINIMIZED;
                pl.rcNormalPosition = ToRECT(c.ParkRect);
                applied = Native.SetWindowPlacement(c.Handle, ref pl);
            }
            else
            {
                applied = false;
            }
            if (!applied) win32Error = Marshal.GetLastWin32Error();
        }
        else
        {
            applied = Native.SetWindowPos(c.Handle, IntPtr.Zero, c.ParkRect.X, c.ParkRect.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (!applied) win32Error = Marshal.GetLastWin32Error();
        }

        bool visible = Native.IsWindowVisible(c.Handle);
        bool zoomed = Native.IsZoomed(c.Handle);
        Rectangle? windowRect = Native.GetWindowRect(c.Handle, out var wr) ? FromRECT(wr) : null;
        uint? showCmd = null;
        Rectangle? normalRect = null;
        var placement = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (Native.GetWindowPlacement(c.Handle, ref placement))
        {
            showCmd = placement.showCmd;
            normalRect = FromRECT(placement.rcNormalPosition);
        }

        bool parked;
        if (c.WasIconic)
        {
            parked = applied && Native.IsIconic(c.Handle) && normalRect.HasValue &&
                     RectApproximatelyEquals(normalRect.Value, c.ParkRect) &&
                     IsEffectivelyParkedRect(normalRect.Value);
        }
        else
        {
            parked = applied && IsWindowOffScreen(c.Handle);
        }
        return (parked ? ParkOutcome.Parked : ParkOutcome.Failed,
            new ParkAttemptDiag(applied, win32Error, true, visible, iconic, zoomed,
                windowRect, normalRect, showCmd));
    }

    /// <summary>Restore a managed window on-screen according to its record; does not touch _hidden or the logs (the caller handles those).</summary>
    private bool RestoreParkedWindow(IntPtr h, HiddenWindowRecord rec)
    {
        var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        bool hasPlacement = Native.GetWindowPlacement(h, ref pl);

        var saved = Rectangle.FromLTRB(rec.NormalLeft, rec.NormalTop, rec.NormalRight, rec.NormalBottom);
        if (saved.Width <= 0 || saved.Height <= 0) return false;

        Screen? target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == rec.ParkMonitor);
        target ??= NearestScreen(saved) ?? Screen.PrimaryScreen;
        if (target == null) return false;

        if (!IntersectsAnyScreen(saved))
        {
            // After a display is unplugged the saved rectangle may lie outside every screen: migrate it to the nearest one.
            var wa = target.WorkingArea;
            saved.Width = Math.Min(saved.Width, wa.Width);
            saved.Height = Math.Min(saved.Height, wa.Height);
            saved.Location = new Point(wa.Left + (wa.Width - saved.Width) / 2,
                wa.Top + (wa.Height - saved.Height) / 2);
        }
        else
        {
            // WINDOWPLACEMENT can retain a normal rectangle on a different display
            // while a maximized window is physically hosted by ParkMonitor.
            saved = NormalizeWindowRectForScreen(saved, target);
        }

        if (Native.IsIconic(h))
        {
            // Fast path: the restore rectangle already matches the record — the
            // common case when nothing moved the window while it was hidden. The
            // caller's SW_SHOWNA reveals it minimized without any show -> place ->
            // re-minimize cycle (which would flash and animate every window).
            if (TryGetEffectiveWindowRect(h, out Rectangle current) &&
                RectApproximatelyEquals(current, saved))
                return true;
            // The user minimized a parked window (e.g. Win+D): keep it minimized and
            // only move the restore position back on-screen. Since 25H2
            // SetWindowPlacement ignores rcNormalPosition, we must use the
            // show -> place -> re-minimize chain.
            return RestoreIconicPlacement(h, saved) &&
                   IsNormalPlacementAssignedToDisplay(h, target.DeviceName);
        }

        if (Native.IsZoomed(h) || rec.SavedShowCmd == Native.SW_SHOWMAXIMIZED)
        {
            var wa = target.WorkingArea;
            if (!Native.SetWindowPos(h, IntPtr.Zero, wa.X, wa.Y, wa.Width, wa.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                return false;
            if (hasPlacement)
            {
                pl.showCmd = Native.SW_SHOWMAXIMIZED;
                pl.rcNormalPosition = ToRECT(saved);
                if (!Native.SetWindowPlacement(h, ref pl)) return false;
            }
            return IsWindowPhysicallyAssignedToDisplay(h, target.DeviceName);
        }

        return Native.SetWindowPos(h, IntPtr.Zero, saved.X, saved.Y, saved.Width, saved.Height,
                   Native.SWP_NOZORDER | Native.SWP_NOACTIVATE) &&
               IsWindowPhysicallyAssignedToDisplay(h, target.DeviceName);
    }

    /// <summary>Bring a visible, unjournaled window back after its display disappears.</summary>
    private bool RestoreUnjournaledOffScreenWindow(IntPtr h)
    {
        if (!IsWindowOffScreen(h)) return true;

        var placement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        bool hasPlacement = Native.GetWindowPlacement(h, ref placement);
        Rectangle saved;
        if (hasPlacement)
            saved = FromRECT(placement.rcNormalPosition);
        else if (Native.GetWindowRect(h, out var rect))
            saved = FromRECT(rect);
        else
            return false;

        if (saved.Width <= 0 || saved.Height <= 0) return false;
        Screen? host = NearestScreen(saved) ?? Screen.PrimaryScreen;
        if (host == null) return false;
        Rectangle area = host.WorkingArea;
        saved.Size = new Size(Math.Min(saved.Width, area.Width), Math.Min(saved.Height, area.Height));
        saved.Location = new Point(area.Left + (area.Width - saved.Width) / 2,
            area.Top + (area.Height - saved.Height) / 2);

        if (Native.IsIconic(h))
        {
            // Since 25H2 SetWindowPlacement ignores rcNormalPosition; use the show -> place -> re-minimize chain.
            return RestoreIconicPlacement(h, saved);
        }

        if (Native.IsZoomed(h) && hasPlacement)
        {
            if (!Native.SetWindowPos(h, IntPtr.Zero, area.X, area.Y, area.Width, area.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                return false;
            placement.showCmd = Native.SW_SHOWMAXIMIZED;
            placement.rcNormalPosition = ToRECT(saved);
            return Native.SetWindowPlacement(h, ref placement) && !IsWindowOffScreen(h);
        }

        return Native.SetWindowPos(h, IntPtr.Zero, saved.X, saved.Y, saved.Width, saved.Height,
                   Native.SWP_NOZORDER | Native.SWP_NOACTIVATE) && !IsWindowOffScreen(h);
    }

    /// <summary>
    /// Move a minimized window's restore position back on-screen (keeping it minimized). Since
    /// Windows 11 25H2 (build 26200) SetWindowPlacement ignores rcNormalPosition (reproduced even
    /// on a plain DefWindowProc window); what works: briefly restore (SW_SHOWNOACTIVATE: no
    /// activation, no focus steal) -> place it with SetWindowPos -> minimize again, and the
    /// restore position then follows the normal rectangle.
    /// </summary>
    private bool RestoreIconicPlacement(IntPtr h, Rectangle saved)
    {
        if (!Native.IsWindow(h)) return false;
        if (!Native.IsIconic(h)) return !IsWindowOffScreen(h);
        var originalPlacement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        bool hasPlacement = Native.GetWindowPlacement(h, ref originalPlacement);
        Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
        if (Native.IsIconic(h)) return false;
        bool moved = Native.SetWindowPos(h, IntPtr.Zero, saved.X, saved.Y, saved.Width, saved.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!moved || IsWindowOffScreen(h))
        {
            // If placement failed, pull it back to minimized so the window is not left visible off-screen.
            MarkInternalMinimize(h);
            Native.ShowWindow(h, Native.SW_SHOWMINNOACTIVE);
            return false;
        }
        MarkInternalMinimize(h);
        Native.ShowWindow(h, Native.SW_SHOWMINNOACTIVE);
        if (hasPlacement)
        {
            // Restore flags such as WPF_RESTORETOMAXIMIZED after the temporary
            // no-activate restore. The normal rectangle is on-screen here, so it
            // is not subject to the off-screen correction that broke parking.
            originalPlacement.showCmd = Native.SW_SHOWMINIMIZED;
            originalPlacement.rcNormalPosition = ToRECT(saved);
            Native.SetWindowPlacement(h, ref originalPlacement);
        }

        var verified = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        return Native.IsIconic(h) && Native.GetWindowPlacement(h, ref verified) &&
               RectApproximatelyEquals(FromRECT(verified.rcNormalPosition), saved) &&
               !IsWindowOffScreen(h);
    }

    /// <summary>
    /// Parks the manageable windows of a desktop off-screen. Windows that are known
    /// to be outside our integrity boundary are skipped and stay visible. Operational
    /// failures are transient: roll this attempt back and let a later switch retry.
    /// </summary>
    private bool ParkManagedWindows(IEnumerable<IntPtr> handles)
    {
        var candidates = new List<ParkCandidate>();
        foreach (IntPtr h in handles.Distinct())
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) continue;
            // An already parked window is left as-is (keeping its original on-screen position record).
            if (_hidden.TryGetValue(h, out var existing) && existing.Parked &&
                MatchesWindowIdentity(h, existing) && IsManagedWindowParked(h, existing))
                continue;
            if (IsKnownUnmanageable(h))
            {
                NotifyKnownUnmanageable(h);
                continue;
            }
            if (ShouldSkipForIntegrity(h, out string reason))
            {
                RegisterUnmanageable(h, reason);
                continue;
            }
            if (!TryCaptureParkCandidate(h, out var candidate))
            {
                if (!Native.IsWindow(h)) continue; // destroyed while being captured
                RaiseWindowControlWarning(nameof(ParkManagedWindows),
                    "Cannot park the current desktop safely: window state capture failed; switch cancelled and will be retried",
                    h);
                return false;
            }
            candidates.Add(candidate);
        }

        if (candidates.Count == 0) return true;

        var planned = _hidden.Values.ToDictionary(r => r.Handle);
        foreach (var c in candidates)
            planned[c.Record.Handle] = c.Record;
        if (!PersistHiddenSnapshot(planned.Values))
        {
            RaiseWindowControlWarning(nameof(ParkManagedWindows),
                "Cannot park the current desktop safely: the recovery snapshot could not be persisted");
            return false;
        }

        var touched = new List<IntPtr>();
        var diags = new Dictionary<IntPtr, ParkAttemptDiag>();
        var pending = new List<ParkCandidate>();
        foreach (var c in candidates)
        {
            // The durable snapshot already contains this record. Add it to the
            // in-memory journal before touching the HWND so a failed verification
            // can be rolled back through the normal recovery path.
            SetHiddenRecord(c.Handle, c.Record);
            touched.Add(c.Handle);
            var (outcome, diag) = TryParkOnce(c);
            if (RouteParkOutcome(c, outcome, diag, touched, diags))
                pending.Add(c);
        }

        // Batched backoff retry: the whole group waits only once per round (250 ms), so the number of failed windows does not amplify the total delay.
        for (int attempt = 2; pending.Count > 0 && attempt <= ParkAttemptCount; attempt++)
        {
            AppLog.Info(nameof(ParkManagedWindows),
                $"Parking verification failed for {pending.Count} window(s) " +
                $"({string.Join(", ", pending.Select(c => c.Handle))}); " +
                $"retry {attempt}/{ParkAttemptCount} scheduled in {ParkRetryDelayMs} ms.");
            Thread.Sleep(ParkRetryDelayMs);
            var remaining = new List<ParkCandidate>();
            foreach (var c in pending)
            {
                var (outcome, diag) = TryParkOnce(c);
                if (RouteParkOutcome(c, outcome, diag, touched, diags))
                    remaining.Add(c);
            }
            pending = remaining;
        }

        if (pending.Count > 0)
        {
            // Transaction cancellation: restore every window touched this round in reverse order
            // and write the recovery journal back to the true state; the desktop index is not
            // committed, and a window that failed the operation never enters _unmanageable.
            bool rolledBack = RollBackParkAttempt(touched);
            PersistHidden();
            foreach (var c in pending.Skip(1))
                AppLog.Warning(nameof(ParkManagedWindows), DescribeParkFailure(c, diags[c.Handle], rolledBack));
            ParkCandidate first = pending[0];
            RaiseWindowControlWarning(nameof(ParkManagedWindows),
                DescribeParkFailure(first, diags[first.Handle], rolledBack), first.Handle);
            return false;
        }

        PersistHidden();
        return true;
    }

    /// <summary>Handle a non-success parking outcome; returns true when the window can still be retried (alive, with matching identity).</summary>
    private bool RouteParkOutcome(ParkCandidate c, ParkOutcome outcome, ParkAttemptDiag? diag,
        List<IntPtr> touched, Dictionary<IntPtr, ParkAttemptDiag> diags)
    {
        if (outcome == ParkOutcome.Parked) return false;
        if (outcome == ParkOutcome.Destroyed || !Native.IsWindow(c.Handle))
        {
            RemoveHiddenRecord(c.Handle);
            touched.Remove(c.Handle);
            return false;
        }
        if (outcome == ParkOutcome.IdentityMismatch || !MatchesWindowIdentity(c.Handle, c.Record))
        {
            RemoveHiddenRecord(c.Handle);
            touched.Remove(c.Handle);
            // The handle was reused while parking: bring the replacement back on screen.
            if (IsManagedWindowParked(c.Handle, c.Record))
            {
                RestoreParkedWindow(c.Handle, c.Record);
                AppLog.Warning(nameof(ParkManagedWindows),
                    $"HWND={c.Handle} changed identity while parking; restored it to the screen.");
            }
            return false;
        }
        diags[c.Handle] = diag!;
        return true;
    }

    private static string DescribeParkFailure(ParkCandidate c, ParkAttemptDiag d, bool rolledBack)
    {
        string api = c.WasIconic ? "SetWindowPlacement" : "SetWindowPos";
        string mode = c.WasIconic ? "minimized-edge-anchor" : "normal-offscreen";
        Rectangle? effective = d.IsIconic ? d.NormalRect : d.WindowRect;
        string visiblePixels = effective.HasValue
            ? VisibleIntersectionPixels(effective.Value).ToString()
            : "null";
        string rollback = rolledBack
            ? "rollbackSucceeded=True; switch cancelled and all changed windows were restored"
            : "rollbackIncomplete=True; switch cancelled and recovery records were retained";
        return $"A managed window could not be parked after {ParkAttemptCount} attempts; {rollback}; " +
               $"api={api}, apiSucceeded={d.ApiSucceeded}, win32Error={(d.Win32Error?.ToString() ?? "null")}, " +
               $"visible={d.IsVisible}, iconic={d.IsIconic}, zoomed={d.IsZoomed}, " +
               $"identityMatches={d.IdentityMatches}, parkMode={mode}, " +
               $"showCmd={(d.ShowCmd?.ToString() ?? "null")}, visibleIntersectionPixels={visiblePixels}, " +
               $"windowRect={FormatRect(d.WindowRect)}, normalRect={FormatRect(d.NormalRect)}, " +
               $"parkTarget=({c.ParkRect.Left},{c.ParkRect.Top},{c.ParkRect.Right},{c.ParkRect.Bottom})";
    }

    private static string FormatRect(Rectangle? r) =>
        r.HasValue ? $"({r.Value.Left},{r.Value.Top},{r.Value.Right},{r.Value.Bottom})" : "null";

    private bool RollBackParkAttempt(IEnumerable<IntPtr> handles)
    {
        bool success = true;
        foreach (IntPtr h in handles.Reverse())
        {
            if (!Native.IsWindow(h))
            {
                RemoveHiddenRecord(h);
                continue;
            }
            success &= UnparkManagedWindow(h);
        }
        return success;
    }

    /// <summary>Restore a parked/hidden window (records left over from hidden mode are simply shown). Only handles the record itself.</summary>
    private bool UnparkManagedWindow(IntPtr h)
    {
        if (!_hidden.TryGetValue(h, out var record)) return false;
        if (!record.Parked) return ShowManagedWindow(h);
        if (!MatchesWindowIdentity(h, record))
        {
            RemoveHiddenRecord(h);
            return false;
        }
        if (!IsManagedWindowParked(h, record))
        {
            // Already back on-screen (the app moved or restored it itself): ensure visibility, then clean up the record.
            if (!Native.IsWindowVisible(h))
            {
                Native.ShowWindow(h, Native.SW_SHOWNA);
                if (!Native.IsWindowVisible(h)) return false;
            }
            RemoveHiddenRecord(h);
            return true;
        }
        if (!RestoreParkedWindow(h, record))
        {
            AppLog.Warning(nameof(UnparkManagedWindow), $"Could not restore parked HWND={h}.");
            return false;
        }
        if (!Native.IsWindowVisible(h))
        {
            Native.ShowWindow(h, Native.SW_SHOWNA);
            if (!Native.IsWindowVisible(h))
            {
                AppLog.Warning(nameof(UnparkManagedWindow),
                    $"HWND={h} was repositioned but could not be shown.");
                return false;
            }
        }
        RemoveHiddenRecord(h);
        return true;
    }

    private bool HideOrParkManagedWindows(IEnumerable<IntPtr> handles) =>
        SharedTaskbar ? ParkManagedWindows(handles) : HideManagedWindows(handles);

    private bool ShowOrUnparkManagedWindow(IntPtr h)
    {
        if (_hidden.TryGetValue(h, out var record))
            return record.Parked ? UnparkManagedWindow(h) : ShowManagedWindow(h);
        return Native.IsWindow(h) && Native.IsWindowVisible(h) && !IsWindowOffScreen(h);
    }

    private bool RestoreManagedWindows(IEnumerable<IntPtr> handles)
    {
        bool success = true;
        foreach (IntPtr h in handles.Distinct())
        {
            if (!Native.IsWindow(h)) continue;
            if (!ShowOrUnparkManagedWindow(h))
            {
                AppLog.Warning(nameof(RestoreManagedWindows),
                    $"Could not make {DescribeWindow(h)} accessible.");
                success = false;
            }
        }
        return success;
    }

    // ---------- pencere uygunluğu ----------

    private bool IsEligible(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        if (Native.GetAncestor(h, Native.GA_ROOT) != h) return false;
        if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 || pid == _ownPid)
            return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        string cls = Native.GetWindowClass(h);
        return !ClassBlacklist.Contains(cls);
    }

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    // ---------- numaralandırma ----------

    /// <summary>Monitörleri ekran düzenine göre (soldan sağa) sıralar; numaralandırma bu sıraya dayanır.</summary>
    private List<MonitorState> OrderedMonitors()
    {
        var bounds = Screen.AllScreens.ToDictionary(s => s.DeviceName, s => s.Bounds);
        return _monitors.Values
            .OrderBy(m => bounds.TryGetValue(m.Device, out var b) ? b.X : int.MaxValue)
            .ThenBy(m => bounds.TryGetValue(m.Device, out var b) ? b.Y : 0)
            .ToList();
    }

    private static SwitchInfo BuildInfo(MonitorState st)
    {
        return new SwitchInfo(st.Device, st.Current);
    }

    /// <summary>Global masaüstü numarasını (1 tabanlı) sahibi monitöre ve yerel index'e çözer.</summary>
    private (MonitorState st, int local)? ResolveGlobal(int number)
    {
        int n = number;
        foreach (var m in OrderedMonitors())
        {
            if (n <= m.Desktops.Count) return (m, n - 1);
            n -= m.Desktops.Count;
        }
        return null;
    }

    // ---------- yapı yönetimi ----------

    private static void AddDesktop(MonitorState st)
    {
        st.Desktops.Add(new HashSet<IntPtr>());
        st.LastActive.Add(IntPtr.Zero);
    }

    private bool AddWindow(HashSet<IntPtr> desktop, IntPtr h)
    {
        if (!desktop.Add(h)) return false;
        _retainedEmptyDesktops.Remove(desktop);
        return true;
    }

    /// <summary>Aktif masaüstünün gerisindeki boş son masaüstlerini kaldırır.</summary>
    private bool PruneTrailingEmpty(MonitorState st)
    {
        bool changed = false;
        while (st.Desktops.Count - 1 > st.Current &&
               st.Desktops[^1].Count == 0 &&
               !_retainedEmptyDesktops.Contains(st.Desktops[^1]))
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
            st.LastActive.RemoveAt(st.LastActive.Count - 1);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Durumu gerçekle senkronlar: yeni pencereleri sahiplen, kapananları temizle,
    /// monitör değiştirenleri taşı, kaybolan monitörlerin gizli pencerelerini kurtar.
    /// Invariant: görünür bir pencere her zaman bulunduğu monitörün aktif masaüstü setindedir.
    /// </summary>
    public bool Sync()
    {
        if (_syncInProgress)
            return SyncCore();

        _syncInProgress = true;
        BeginIdentityCache();
        try
        {
            return SyncCore();
        }
        finally
        {
            EndIdentityCache();
            _syncInProgress = false;
        }
    }

    /// <summary>
    /// Opens a batch scope for the per-PID identity cache: within one sync or desktop
    /// transaction every process is queried (GetProcessById + StartTime) at most once,
    /// instead of once per window per hide/restore verification.
    /// </summary>
    private void BeginIdentityCache()
    {
        if (_identityCacheDepth++ == 0)
            _processIdentityCache.Clear();
    }

    private void EndIdentityCache()
    {
        if (--_identityCacheDepth == 0)
            _processIdentityCache.Clear();
    }

    private bool SyncCore()
    {
        bool changed = false;
        foreach (var h in _unmanageable.Keys.Where(h => !Native.IsWindow(h)).ToList())
            _unmanageable.Remove(h);
        var currentDevices = Screen.AllScreens.Select(s => s.DeviceName).ToHashSet();

        foreach (string dev in currentDevices)
            if (!_monitors.ContainsKey(dev))
            {
                _monitors[dev] = new MonitorState { Device = dev };
                changed = true;
            }

        foreach (string dev in _monitors.Keys.Where(d => !currentDevices.Contains(d)).ToList())
        {
            MonitorState removedMonitor = _monitors[dev];
            MonitorState? fallback = _monitors.Values
                .FirstOrDefault(m => currentDevices.Contains(m.Device));
            var displacedWindows = new HashSet<IntPtr>();
            foreach (var set in removedMonitor.Desktops)
            {
                _retainedEmptyDesktops.Remove(set);
                foreach (var h in set)
                    displacedWindows.Add(h);
            }
            _monitors.Remove(dev);
            foreach (IntPtr h in displacedWindows)
            {
                if (!Native.IsWindow(h))
                {
                    RemoveHiddenRecord(h);
                    continue;
                }

                bool restored = ShowOrUnparkManagedWindow(h);
                if (!restored && !_hidden.ContainsKey(h) && Native.IsWindowVisible(h))
                    restored = RestoreUnjournaledOffScreenWindow(h);
                if (!restored)
                    AppLog.Warning(nameof(Sync),
                        $"Could not restore HWND={h} after display '{dev}' was disconnected.");

                if (fallback != null &&
                    (Native.IsWindowVisible(h) || _hidden.ContainsKey(h)))
                    AddWindow(fallback.Desktops[fallback.Current], h);
            }
            changed = true;
        }

        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                if (set.RemoveWhere(h =>
                {
                    if (!Native.IsWindow(h))
                    {
                        RemoveHiddenRecord(h);
                        return true;
                    }
                    if (_hidden.TryGetValue(h, out var record) && !MatchesWindowIdentity(h, record))
                    {
                        RemoveHiddenRecord(h);
                        return true;
                    }
                    // Windows hidden by their own application (for example a
                    // close-to-tray window) have no recovery record. Keeping such
                    // an HWND in a desktop creates a ghost member: it can make an
                    // empty desktop grow forever and later block target restore.
                    if (!Native.IsWindowVisible(h) && !_hidden.ContainsKey(h))
                        return true;
                    if (Native.IsWindowVisible(h) && !IsEligible(h))
                    {
                        RemoveHiddenRecord(h);
                        return true;
                    }
                    return false;
                }) > 0)
                    changed = true;

        foreach (var (h, record) in _hidden.ToList())
            if (!MatchesWindowIdentity(h, record))
            {
                if (Native.IsWindow(h))
                    AppLog.Warning(nameof(Sync),
                        $"Dropping the recovery record of a live HWND={h}: the identity no longer matches.");
                changed |= RemoveHiddenRecord(h);
            }

        foreach (var h in EnumerateTopLevelWindows())
        {
            if (!IsEligible(h)) continue;

            // Shared taskbar mode: a window parked off-screen keeps its desktop membership;
            // its taskbar button is the entry point for jumping back to that desktop.
            if (_hidden.TryGetValue(h, out var parkedRecord) && parkedRecord.Parked &&
                MatchesWindowIdentity(h, parkedRecord) && IsManagedWindowParked(h, parkedRecord))
                continue;

            string? dev = Native.GetMonitorDeviceOfWindow(h);
            if (dev == null || !_monitors.TryGetValue(dev, out var st)) continue;

            // An application or the user may have shown one of our hidden windows.
            // Visible windows belong to the current desktop and must not remain in
            // the crash-recovery journal, even if already present in that set.
            if (_hidden.TryGetValue(h, out var journalRecord) && journalRecord.Parked)
                AppLog.Warning(nameof(Sync),
                    $"Parked HWND={h} is back on screen; adopting it into the current desktop of '{dev}'.");
            changed |= RemoveHiddenRecord(h);

            if (!st.Desktops[st.Current].Contains(h))
            {
                // Başka bir set'te kayıtlıysa oradan çıkar (monitör değiştirmiş
                // veya gizliyken uygulama tarafından tekrar gösterilmiş olabilir)
                bool migrated = false;
                foreach (var other in _monitors.Values)
                    foreach (var set in other.Desktops)
                        if (set.Remove(h))
                        {
                            migrated = true;
                            changed = true;
                        }
                if (migrated && !_unmanageable.ContainsKey(h))
                    AppLog.Warning(nameof(Sync),
                        $"Visible window HWND={h} migrated into the current desktop of '{dev}'.");
                changed |= AddWindow(st.Desktops[st.Current], h);
            }
        }

        foreach (var st in _monitors.Values)
            changed |= PruneTrailingEmpty(st);

        PersistHidden();
        return changed;
    }

    /// <summary>Genel bakış arayüzü için tam düzen (monitör başına yerel numaralarla).</summary>
    public IReadOnlyList<MonitorEntry> GetLayout()
    {
        Sync();
        var result = new List<MonitorEntry>();
        int ordinal = 0;
        foreach (var st in OrderedMonitors())
        {
            ordinal++;
            var desktops = new List<DesktopEntry>();
            for (int i = 0; i < st.Desktops.Count; i++)
            {
                var windows = st.Desktops[i]
                    .Where(Native.IsWindow)
                    .Select(h =>
                    {
                        string t = Native.GetWindowTitle(h);
                        return new WindowEntry(h, t.Length > 0 ? t : Native.GetWindowClass(h));
                    })
                    .ToList();
                desktops.Add(new DesktopEntry(i, i == st.Current, windows));
            }
            result.Add(new MonitorEntry(st.Device, ordinal, desktops));
        }
        return result;
    }

    // ---------- geçişler ----------

    /// <summary>Fare imlecinin bulunduğu monitörde bir sonraki/önceki masaüstüne geçer.
    /// Son masaüstünde ileri geçiş, masaüstünde pencere varsa yeni masaüstü oluşturur.</summary>
    public void SwitchRelative(int delta)
    {
        string? dev = Native.GetMonitorDeviceUnderCursor();
        if (dev == null)
        {
            AppLog.Info(nameof(SwitchRelative), "Switch ignored: no monitor under the cursor.");
            return;
        }
        Sync();
        if (!_monitors.TryGetValue(dev, out var st))
        {
            AppLog.Info(nameof(SwitchRelative), $"Switch ignored: unknown monitor '{dev}'.");
            return;
        }

        int target = st.Current + delta;
        if (target < 0)
        {
            AppLog.Info(nameof(SwitchRelative), $"Switch edge: already at the first desktop of '{dev}'.");
            DesktopSwitched?.Invoke(BuildInfo(st)); // uçta: sadece OSD göster
            return;
        }
        bool createdDesktop = false;
        if (target >= st.Desktops.Count)
        {
            // An unmanageable window (e.g. an elevated one) always follows the current desktop and
            // cannot count as proof that "the desktop is non-empty"; when only such windows remain,
            // treat the desktop as empty to avoid creating desktops endlessly at the end. Uncached
            // windows get a live integrity probe (query only, no registration — registration happens
            // only when a window is actually hidden/parked).
            bool hasEffectiveWindows = st.Desktops[st.Current]
                .Any(h => Native.IsWindow(h) && !IsKnownUnmanageable(h) && !ShouldSkipForIntegrity(h, out _));
            bool canGrow = delta > 0
                && st.Desktops.Count < MaxDesktopsPerMonitor
                && hasEffectiveWindows; // boş masaüstünden yenisi açılmaz
            if (!canGrow)
            {
                AppLog.Info(nameof(SwitchRelative),
                    $"Switch edge: cannot grow past desktop {st.Current} of '{dev}'.");
                DesktopSwitched?.Invoke(BuildInfo(st));
                return;
            }
            AddDesktop(st);
            createdDesktop = true;
            target = st.Desktops.Count - 1;
        }
        AppLog.Info(nameof(SwitchRelative), $"Switch requested on '{dev}': {st.Current} -> {target}.");
        if (!SwitchToCore(st, target) && createdDesktop)
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
            st.LastActive.RemoveAt(st.LastActive.Count - 1);
        }
    }

    /// <summary>Global masaüstü numarasına geçer (hangi monitörde olduğunu kendisi bulur).</summary>
    public void SwitchToGlobal(int number)
    {
        Sync();
        var resolved = ResolveGlobal(number);
        if (resolved != null)
            SwitchToCore(resolved.Value.st, resolved.Value.local);
        else
        {
            string? device = Native.GetMonitorDeviceUnderCursor();
            if (device != null && _monitors.TryGetValue(device, out var current))
                DesktopSwitched?.Invoke(BuildInfo(current));
        }
    }

    /// <summary>Belirli monitörde belirli yerel masaüstüne geçer.</summary>
    public void SwitchTo(string device, int localIndex)
    {
        Sync();
        if (_monitors.TryGetValue(device, out var st) && localIndex >= 0 && localIndex < st.Desktops.Count)
            SwitchToCore(st, localIndex);
    }

    /// <summary>Verilen monitörde yeni boş masaüstü oluşturur ve ona geçer.</summary>
    public void CreateDesktopAndSwitch(string device)
    {
        Sync();
        if (!_monitors.TryGetValue(device, out var st)) return;
        if (st.Desktops.Count >= MaxDesktopsPerMonitor) return;
        AddDesktop(st);
        if (!SwitchToCore(st, st.Desktops.Count - 1))
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
            st.LastActive.RemoveAt(st.LastActive.Count - 1);
        }
    }

    /// <summary>Yeni boş masaüstü oluşturur ancak aktif masaüstünü değiştirmez.</summary>
    public bool CreateDesktop(string device)
    {
        Sync();
        if (!_monitors.TryGetValue(device, out var st)) return false;
        if (st.Desktops.Count >= MaxDesktopsPerMonitor) return false;
        AddDesktop(st);
        _retainedEmptyDesktops.Add(st.Desktops[^1]);
        return true;
    }

    /// <summary>Yeni bir masaüstü oluşturur ve sürüklenen pencereyi ona taşır;
    /// genel bakışın açık kalabilmesi için yeni masaüstüne geçiş yapmaz.</summary>
    public bool CreateDesktopAndMoveWindow(IntPtr h, string dstDevice)
    {
        BeginIdentityCache();
        try
        {
            return CreateDesktopAndMoveWindowCore(h, dstDevice);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private bool CreateDesktopAndMoveWindowCore(IntPtr h, string dstDevice)
    {
        Sync();
        if (!Native.IsWindow(h)) return false;
        if (!_monitors.TryGetValue(dstDevice, out var dst)) return false;
        if (dst.Desktops.Count >= MaxDesktopsPerMonitor) return false;
        // Integrity-skipped windows remain visible by design. Do not create an
        // empty desktop and only pretend to move one in the model.
        if (!CanReassignWindow(h, nameof(CreateDesktopAndMoveWindow))) return false;
        string? srcDevice = FindWindowDevice(h);
        bool wasAccessible = Native.IsWindowVisible(h) && !IsWindowOffScreen(h);
        if (!HideOrParkManagedWindows(new[] { h })) return false;

        if (srcDevice != null && srcDevice != dstDevice &&
            !RepositionWindow(h, srcDevice, dstDevice))
        {
            if (wasAccessible)
                ShowOrUnparkManagedWindow(h);
            PersistHidden();
            return false;
        }

        AddDesktop(dst);
        int target = dst.Desktops.Count - 1;
        _retainedEmptyDesktops.Add(dst.Desktops[target]);

        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                set.Remove(h);

        AddWindow(dst.Desktops[target], h);
        dst.LastActive[target] = h;

        foreach (var st in _monitors.Values) PruneTrailingEmpty(st);
        PersistHidden();
        return true;
    }

    /// <summary>Bir masaüstünü kapatır ve pencerelerini önceki masaüstüne taşır.
    /// İlk masaüstü kapatılırsa pencereler ikinci masaüstüne gider. Her monitörde
    /// en az bir masaüstü kalır.</summary>
    public bool DeleteDesktop(string device, int localIndex)
    {
        BeginIdentityCache();
        try
        {
            return DeleteDesktopCore(device, localIndex);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private bool DeleteDesktopCore(string device, int localIndex)
    {
        Sync();
        if (!_monitors.TryGetValue(device, out var st)) return false;
        if (st.Desktops.Count <= 1) return false;
        if (localIndex < 0 || localIndex >= st.Desktops.Count) return false;

        var removed = st.Desktops[localIndex];
        var current = st.Desktops[st.Current];
        bool removedWasCurrent = ReferenceEquals(removed, current);
        int targetBeforeRemoval = localIndex > 0 ? localIndex - 1 : 1;
        var target = st.Desktops[targetBeforeRemoval];

        IntPtr movedFocus = st.LastActive[localIndex];
        if (!Native.IsWindow(movedFocus) || !removed.Contains(movedFocus))
            movedFocus = removed.FirstOrDefault(Native.IsWindow);

        foreach (var h in removed)
            if (Native.IsWindow(h))
                AddWindow(target, h);

        _retainedEmptyDesktops.Remove(removed);
        st.Desktops.RemoveAt(localIndex);
        st.LastActive.RemoveAt(localIndex);

        int targetIndex = st.Desktops.IndexOf(target);
        st.Current = removedWasCurrent ? targetIndex : st.Desktops.IndexOf(current);

        if (targetIndex >= 0 && movedFocus != IntPtr.Zero &&
            (removedWasCurrent || !Native.IsWindow(st.LastActive[targetIndex])))
            st.LastActive[targetIndex] = movedFocus;

        bool targetIsCurrent = st.Current == targetIndex;
        foreach (var h in target.ToList())
        {
            if (!Native.IsWindow(h))
            {
                target.Remove(h);
                RemoveHiddenRecord(h);
                continue;
            }

            if (targetIsCurrent)
            {
                ShowOrUnparkManagedWindow(h);
            }
        }

        if (!targetIsCurrent)
            HideOrParkManagedWindows(target);

        PruneTrailingEmpty(st);
        PersistHidden();
        return true;
    }

    private bool SwitchToCore(MonitorState st, int target)
    {
        bool ownsGuard = !_switchInProgress;
        if (ownsGuard) _switchInProgress = true;
        BeginIdentityCache();
        try
        {
            return SwitchToCoreGuarded(st, target);
        }
        finally
        {
            EndIdentityCache();
            if (ownsGuard) _switchInProgress = false;
        }
    }

    private bool SwitchToCoreGuarded(MonitorState st, int target)
    {
        if (st.Current == target)
        {
            bool accessible = RestoreManagedWindows(st.Desktops[target]);
            PersistHidden();
            if (!accessible)
                RaiseWindowControlWarning(nameof(SwitchToCore),
                    "Cannot restore the current desktop safely: a managed window is inaccessible");
            DesktopSwitched?.Invoke(BuildInfo(st));
            return accessible;
        }

        SwitchStarting?.Invoke(st.Device, st.Current, target);
        AppLog.Info(nameof(SwitchToCore),
            $"Switch begin: '{st.Device}' {st.Current} -> {target}.");

        IntPtr fg = Native.GetForegroundWindow();
        if (st.Desktops[st.Current].Contains(fg))
            st.LastActive[st.Current] = fg;

        int source = st.Current;
        var sourceWindows = st.Desktops[source].ToList();
        var targetWindows = st.Desktops[target].ToList();
        var targetInitiallyManaged = targetWindows.Where(_hidden.ContainsKey).ToHashSet();
        var targetSuccessfullyRestored = new List<IntPtr>();
        IntPtr targetRestoreFailure = IntPtr.Zero;

        if (!HideOrParkManagedWindows(sourceWindows))
        {
            DesktopSwitched?.Invoke(BuildInfo(st));
            return false;
        }

        bool targetRestored = true;
        var restorePending = new List<IntPtr>();
        foreach (var h in targetWindows)
        {
            if (!Native.IsWindow(h)) { st.Desktops[target].Remove(h); continue; }
            if (!Native.IsWindowVisible(h) && !_hidden.ContainsKey(h))
            {
                // The application hid this window itself after the last sync.
                // It is not ours to show; discard the stale membership and let a
                // later Sync adopt it wherever it becomes visible again.
                st.Desktops[target].Remove(h);
                continue;
            }
            switch (TryRestoreTargetWindow(h, targetInitiallyManaged, targetSuccessfullyRestored))
            {
                case TargetRestoreOutcome.Pruned:
                    st.Desktops[target].Remove(h);
                    break;
                case TargetRestoreOutcome.Failed:
                    restorePending.Add(h);
                    break;
            }
        }

        // Batched backoff retry: a transient restore refusal (the window is being
        // dragged, the app clamps its position) must not cancel the switch on the
        // first try; the whole group waits only once per round.
        for (int attempt = 2; restorePending.Count > 0 && attempt <= ParkAttemptCount; attempt++)
        {
            AppLog.Info(nameof(SwitchToCore),
                $"Target restore failed for {restorePending.Count} window(s) " +
                $"({string.Join(", ", restorePending)}); " +
                $"retry {attempt}/{ParkAttemptCount} scheduled in {ParkRetryDelayMs} ms.");
            Thread.Sleep(ParkRetryDelayMs);
            var remaining = new List<IntPtr>();
            foreach (var h in restorePending)
            {
                if (!Native.IsWindow(h)) continue; // destroyed while retrying
                switch (TryRestoreTargetWindow(h, targetInitiallyManaged, targetSuccessfullyRestored))
                {
                    case TargetRestoreOutcome.Pruned:
                        st.Desktops[target].Remove(h);
                        break;
                    case TargetRestoreOutcome.Failed:
                        remaining.Add(h);
                        break;
                }
            }
            restorePending = remaining;
        }

        if (restorePending.Count > 0)
        {
            targetRestored = false;
            targetRestoreFailure = restorePending[0];
        }

        if (!targetRestored)
        {
            // Roll back in reverse order: hide/re-park target windows that this
            // attempt exposed, then restore the original current desktop.
            bool targetRolledBack = HideOrParkManagedWindows(targetSuccessfullyRestored);
            bool sourceRolledBack = RestoreManagedWindows(sourceWindows);
            PersistHidden();
            if (!targetRolledBack || !sourceRolledBack)
                AppLog.Warning(nameof(SwitchToCore),
                    "Desktop switch rollback was incomplete; recovery records were retained.");
            RaiseWindowControlWarning(nameof(SwitchToCore),
                "Cannot switch desktops safely: the target desktop could not be restored; rolled back",
                targetRestoreFailure);
            DesktopSwitched?.Invoke(BuildInfo(st));
            return false;
        }

        // The externally visible window state is now complete; commit the model.
        st.Current = target;
        AppLog.Info(nameof(SwitchToCore),
            $"Switch committed: '{st.Device}' -> desktop {target}.");

        // Odağı hedef masaüstünde en son aktif olan görünür pencereye ver
        IntPtr focus = st.LastActive[target];
        if (!Native.IsWindow(focus) || !Native.IsWindowVisible(focus) ||
            !st.Desktops[target].Contains(focus))
            focus = st.Desktops[target].FirstOrDefault(h =>
                Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h));
        if (focus != IntPtr.Zero)
            Native.SetForegroundWindow(focus);

        // In shared mode never leave the foreground on an off-screen window (fallback for when SetForegroundWindow fails).
        if (SharedTaskbar)
        {
            IntPtr now = Native.GetForegroundWindow();
            if (now != IntPtr.Zero && _hidden.TryGetValue(now, out var rec) && rec.Parked)
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero)
                    Native.SetForegroundWindow(tray);
            }
        }

        PruneTrailingEmpty(st);
        PersistHidden();
        DesktopSwitched?.Invoke(BuildInfo(st));
        return true;
    }

    private enum TargetRestoreOutcome { Restored, Pruned, Failed }

    /// <summary>One restore attempt for a window of the target desktop. Pruned means the
    /// HWND was reused (identity mismatch removed its record) and its stale membership
    /// must be dropped instead of failing the whole transition.</summary>
    private TargetRestoreOutcome TryRestoreTargetWindow(IntPtr h,
        HashSet<IntPtr> targetInitiallyManaged, List<IntPtr> targetSuccessfullyRestored)
    {
        if (ShowOrUnparkManagedWindow(h))
        {
            if (targetInitiallyManaged.Contains(h))
                targetSuccessfullyRestored.Add(h);
            return TargetRestoreOutcome.Restored;
        }
        if (targetInitiallyManaged.Contains(h) && !_hidden.ContainsKey(h))
            return TargetRestoreOutcome.Pruned;
        return TargetRestoreOutcome.Failed;
    }

    /// <summary>
    /// Record a user/system minimization of a window on the current desktop. The
    /// foreground event that immediately follows may merely be Windows selecting
    /// the next Z-order window, not an intentional taskbar jump.
    /// </summary>
    public void HandleMinimizeStarted(IntPtr h)
    {
        if (!SharedTaskbar || _switchInProgress || h == IntPtr.Zero) return;
        long now = Environment.TickCount64;
        if (_internalMinimizeUntil.TryGetValue(h, out long internalUntil))
        {
            if (now <= internalUntil) return;
            _internalMinimizeUntil.Remove(h);
        }
        bool belongsToCurrent = _monitors.Values.Any(st => st.Desktops[st.Current].Contains(h));
        if (!belongsToCurrent) return;
        if (_hidden.TryGetValue(h, out var record) && record.Parked) return;

        _minimizeSource = h;
        _suppressParkedForegroundUntil = now + MinimizeForegroundSuppressMs;
        _explicitMinimizeRestore = IntPtr.Zero;
        _explicitMinimizeRestoreUntil = 0;
        AppLog.Info(nameof(HandleMinimizeStarted),
            $"Minimize started for current HWND={h}; suppressing one incidental parked foreground activation.");
    }

    /// <summary>A minimized parked window being restored is an intentional jump signal.</summary>
    public void HandleMinimizeEnded(IntPtr h)
    {
        if (!SharedTaskbar || _switchInProgress || h == IntPtr.Zero) return;
        if (!_hidden.TryGetValue(h, out var record) || !record.Parked ||
            !MatchesWindowIdentity(h, record)) return;

        _explicitMinimizeRestore = h;
        _explicitMinimizeRestoreUntil = Environment.TickCount64 + MinimizeForegroundSuppressMs;
    }

    private void ExpireMinimizeMarkers(long now)
    {
        // Runs on every foreground change: keep it allocation-free in the common case.
        if (_internalMinimizeUntil.Count > 0)
        {
            List<IntPtr>? expired = null;
            foreach (var pair in _internalMinimizeUntil)
                if (now > pair.Value)
                    (expired ??= new List<IntPtr>()).Add(pair.Key);
            if (expired != null)
                foreach (IntPtr h in expired)
                    _internalMinimizeUntil.Remove(h);
        }
        if (now > _suppressParkedForegroundUntil)
        {
            _minimizeSource = IntPtr.Zero;
            _suppressParkedForegroundUntil = 0;
        }
        if (now > _explicitMinimizeRestoreUntil)
        {
            _explicitMinimizeRestore = IntPtr.Zero;
            _explicitMinimizeRestoreUntil = 0;
        }
    }

    private static void FocusShell()
    {
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
            Native.SetForegroundWindow(tray);
    }

    private void MarkInternalMinimize(IntPtr h) =>
        _internalMinimizeUntil[h] = Environment.TickCount64 + MinimizeForegroundSuppressMs;

    /// <summary>
    /// Foreground window changed: jump to the desktop of a window that IndepenDesk keeps on
    /// another desktop — parked in shared mode, hidden in default mode with taskbar jump.
    /// </summary>
    public void HandleForegroundActivated(IntPtr h)
    {
        if (_switchInProgress) return;
        // Mode-independent: MarkInternalMinimize is written in both modes (the iconic
        // restore chain), so the markers must also expire in both modes.
        ExpireMinimizeMarkers(Environment.TickCount64);
        if (SharedTaskbar)
        {
            HandleParkedForegroundActivated(h);
            return;
        }
        if (TaskbarJump)
            HandleHiddenForegroundActivated(h);
    }

    /// <summary>Shared mode: the taskbar/Alt-Tab activated a parked window — jump to the desktop it belongs to.</summary>
    private void HandleParkedForegroundActivated(IntPtr h)
    {
        long now = Environment.TickCount64;
        if (h == IntPtr.Zero || !_hidden.TryGetValue(h, out var record) || !record.Parked) return;
        if (!MatchesWindowIdentity(h, record))
        {
            AppLog.Warning(nameof(HandleForegroundActivated),
                $"Parked HWND={h} failed the identity check; taskbar jump ignored.");
            return;
        }

        bool explicitRestore =
            (_explicitMinimizeRestore == h && now <= _explicitMinimizeRestoreUntil) ||
            (WasSavedMinimized(record) && !Native.IsIconic(h));
        if (!explicitRestore && _minimizeSource != IntPtr.Zero &&
            now <= _suppressParkedForegroundUntil)
        {
            AppLog.Info(nameof(HandleForegroundActivated),
                $"Ignored incidental parked foreground HWND={h} after minimizing HWND={_minimizeSource}.");
            _minimizeSource = IntPtr.Zero;
            _suppressParkedForegroundUntil = 0;
            FocusShell();
            return;
        }

        _explicitMinimizeRestore = IntPtr.Zero;
        _explicitMinimizeRestoreUntil = 0;
        _minimizeSource = IntPtr.Zero;
        _suppressParkedForegroundUntil = 0;

        _switchInProgress = true;
        try
        {
            Sync();
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (st.Desktops[i].Contains(h))
                    {
                        st.LastActive[i] = h;
                        AppLog.Info(nameof(HandleForegroundActivated),
                            $"Taskbar jump: HWND={h} -> '{st.Device}' desktop {i}.");
                        SwitchToCore(st, i);
                        return;
                    }

            // A record exists but the window belongs to no desktop (e.g. a crash-recovery leftover): adopt it into the current desktop of its display and show it.
            string? dev = record.ParkMonitor != null && _monitors.ContainsKey(record.ParkMonitor)
                ? record.ParkMonitor
                : Native.GetMonitorDeviceUnderCursor();
            if (dev != null && _monitors.TryGetValue(dev, out var adopt))
            {
                AppLog.Warning(nameof(HandleForegroundActivated),
                    $"Parked HWND={h} belongs to no desktop; adopting it into the current desktop of '{dev}'.");
                AddWindow(adopt.Desktops[adopt.Current], h);
                UnparkManagedWindow(h);
                PersistHidden();
            }
        }
        finally
        {
            _switchInProgress = false;
        }
    }

    /// <summary>
    /// Default (hidden) mode with taskbar jump: the application re-showed and activated one of
    /// our hidden windows (taskbar/pinned/tray icon click of a single-instance app). Jump to the
    /// desktop the window belongs to instead of adopting it into the current one.
    /// </summary>
    private void HandleHiddenForegroundActivated(IntPtr h)
    {
        if (h == IntPtr.Zero || !_hidden.TryGetValue(h, out var record) || record.Parked) return;
        if (!MatchesWindowIdentity(h, record))
        {
            AppLog.Warning(nameof(HandleForegroundActivated),
                $"Hidden HWND={h} failed the identity check; taskbar jump ignored.");
            return;
        }

        // The app already re-showed the window, so the membership lookup must not go through
        // Sync(): it would adopt the visible window into the current desktop and drop the
        // record this jump decision depends on.
        _switchInProgress = true;
        try
        {
            string? dev = Native.GetMonitorDeviceOfWindow(h);
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (st.Desktops[i].Contains(h))
                    {
                        if (i != st.Current && st.Device == dev)
                        {
                            st.LastActive[i] = h;
                            AppLog.Info(nameof(HandleForegroundActivated),
                                $"Taskbar jump (hidden mode): HWND={h} -> '{st.Device}' desktop {i}.");
                            SwitchToCore(st, i);
                        }
                        else if (i != st.Current)
                        {
                            // The app moved the window to another monitor before showing it:
                            // leave it to Sync, which adopts it into that monitor's current desktop.
                            AppLog.Info(nameof(HandleForegroundActivated),
                                $"Re-shown HWND={h} now lives on '{dev}' (desktop owner '{st.Device}'); leaving it for Sync adoption.");
                        }
                        // i == st.Current cannot normally happen (hidden records exist only for
                        // non-current desktops); if it ever does, let the next Sync clean up.
                        return;
                    }

            // No desktop membership (e.g. a crash-recovery leftover): do not jump; the next
            // Sync adopts the re-shown window into the current desktop as before.
            AppLog.Info(nameof(HandleForegroundActivated),
                $"Re-shown HWND={h} has no desktop membership; leaving it for Sync adoption.");
        }
        finally
        {
            _switchInProgress = false;
        }
    }

    /// <summary>Switch to the desktop containing the given window and hand it the focus; unminimize
    /// first when the window is minimized. Used by the double-click direct jump of window entries.</summary>
    public void SwitchToWindow(IntPtr h)
    {
        if (!Native.IsWindow(h)) return;
        Sync();
        foreach (var st in _monitors.Values)
            for (int i = 0; i < st.Desktops.Count; i++)
                if (st.Desktops[i].Contains(h))
                {
                    st.LastActive[i] = h;
                    if (SwitchToCore(st, i))
                        RestoreAndFocusWindow(h);
                    return;
                }
    }

    /// <summary>
    /// Final step of the direct-jump path: restore a visible but minimized window, then try to
    /// give it the foreground as a fallback. Only "unminimize" is handled here; visibility is never
    /// restored proactively — SwitchToCore already showed/brought back the managed windows, and
    /// windows hidden by their own apps are out of our recovery scope. Restoring and focusing an
    /// elevated window is silently denied by UIPI; in that case degrade to switching only the desktop.
    /// </summary>
    private static void RestoreAndFocusWindow(IntPtr h)
    {
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return;
        if (Native.IsIconic(h))
            Native.ShowWindow(h, Native.SW_RESTORE);
        Native.SetForegroundWindow(h);
        if (Native.GetForegroundWindow() != h)
            AppLog.Info(nameof(RestoreAndFocusWindow),
                $"HWND={h} could not take the foreground (possibly elevated); desktop switch only.");
    }

    /// <summary>Taskbar jump needs no window-state transition; it only gates future foreground events.</summary>
    public void SetTaskbarJump(bool enable)
    {
        if (TaskbarJump == enable) return;
        TaskbarJump = enable;
        AppLog.Info(nameof(SetTaskbarJump), $"Taskbar jump {(enable ? "enabled" : "disabled")}.");
    }

    /// <summary>
    /// Whether a window that just became visible is one we keep hidden/parked on another
    /// desktop. Used to schedule an immediate reconciliation (adoption or taskbar jump)
    /// instead of waiting for the periodic sync, which may be backed off by up to 15 s
    /// while idle.
    /// </summary>
    public bool NeedsShownReconcile(IntPtr h) =>
        !_switchInProgress && h != IntPtr.Zero &&
        _hidden.ContainsKey(h) && Native.IsWindowVisible(h);

    /// <summary>Switch shared taskbar mode transactionally; commit the mode only after every window state has been updated.</summary>
    public bool SetSharedTaskbarMode(bool enable)
    {
        if (enable == SharedTaskbar) return true;
        BeginIdentityCache();
        try
        {
            return SetSharedTaskbarModeCore(enable);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private bool SetSharedTaskbarModeCore(bool enable)
    {
        Sync();

        if (enable)
        {
            var originallyHidden = _hidden.Where(kv => !kv.Value.Parked)
                .Select(kv => kv.Key).ToList();
            var restored = new List<IntPtr>();
            IntPtr restoreFailure = IntPtr.Zero;
            foreach (IntPtr h in originallyHidden)
            {
                if (ShowManagedWindow(h))
                    restored.Add(h);
                else
                    restoreFailure = h;
            }
            PersistHidden();

            if (restoreFailure != IntPtr.Zero)
            {
                HideManagedWindows(restored);
                PersistHidden();
                RaiseWindowControlWarning(nameof(SetSharedTaskbarMode),
                    "Cannot switch to shared taskbar mode: a managed window could not be shown", restoreFailure);
                return false;
            }

            bool failed = false;
            foreach (var st in _monitors.Values)
            {
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (i != st.Current && st.Desktops[i].Count > 0 && !ParkManagedWindows(st.Desktops[i]))
                    {
                        failed = true;
                        break;
                    }
                if (failed) break;
            }

            if (failed)
            {
                foreach (var h in _hidden.Where(kv => kv.Value.Parked).Select(kv => kv.Key).ToList())
                    UnparkManagedWindow(h);
                foreach (var m in _monitors.Values)
                    for (int i = 0; i < m.Desktops.Count; i++)
                        if (i != m.Current)
                            HideManagedWindows(m.Desktops[i]);
                PersistHidden();
                RaiseWindowControlWarning(nameof(SetSharedTaskbarMode),
                    "Cannot switch to shared taskbar mode: parking other desktops failed; previous mode restored");
                return false;
            }

            SharedTaskbar = true;
            PersistHidden();
            return true;
        }
        else
        {
            var originallyParked = _hidden.Where(kv => kv.Value.Parked)
                .Select(kv => kv.Key).ToList();
            var restored = new List<IntPtr>();
            IntPtr restoreFailure = IntPtr.Zero;
            foreach (IntPtr h in originallyParked)
            {
                if (UnparkManagedWindow(h))
                    restored.Add(h);
                else
                    restoreFailure = h;
            }

            if (restoreFailure != IntPtr.Zero)
            {
                ParkManagedWindows(restored);
                PersistHidden();
                RaiseWindowControlWarning(nameof(SetSharedTaskbarMode),
                    "Cannot leave shared taskbar mode: a parked window could not be unparked", restoreFailure);
                return false;
            }

            bool failed = false;
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (i != st.Current && st.Desktops[i].Count > 0 && !HideManagedWindows(st.Desktops[i]))
                        failed = true;

            if (failed)
            {
                foreach (var h in _hidden.Where(kv => !kv.Value.Parked).Select(kv => kv.Key).ToList())
                    ShowManagedWindow(h);
                foreach (var st in _monitors.Values)
                    for (int i = 0; i < st.Desktops.Count; i++)
                        if (i != st.Current && st.Desktops[i].Count > 0)
                            ParkManagedWindows(st.Desktops[i]);
                PersistHidden();
                RaiseWindowControlWarning(nameof(SetSharedTaskbarMode),
                    "Cannot leave shared taskbar mode: hiding other desktops failed; previous mode restored");
                return false;
            }

            SharedTaskbar = false;
            PersistHidden();
            return true;
        }
    }

    // ---------- pencere ve masaüstü taşıma ----------

    /// <summary>
    /// A window that crosses our integrity boundary may follow desktop switches,
    /// but it cannot be assigned to an inactive desktop because it cannot be hidden
    /// or parked there. Reject such model mutations before they change any state.
    /// </summary>
    private bool CanReassignWindow(IntPtr h, string operation)
    {
        if (!Native.IsWindow(h)) return false;
        if (IsKnownUnmanageable(h))
        {
            AppLog.Info(operation,
                $"Move ignored: HWND={h} is unmanageable and stays on the current desktop.");
            NotifyKnownUnmanageable(h);
            return false;
        }
        if (!ShouldSkipForIntegrity(h, out string reason)) return true;

        RegisterUnmanageable(h, reason);
        AppLog.Info(operation,
            $"Move ignored: HWND={h} crossed the integrity boundary (reason={reason}).");
        return false;
    }

    /// <summary>Moves the active window to the adjacent desktop on its monitor and follows it.
    /// Moving forward from the last desktop creates a new desktop. An unmanageable window
    /// (e.g. an elevated one) is not moved and no desktop is created; the UI is notified.</summary>
    public void MoveActiveWindow(int delta)
    {
        BeginIdentityCache();
        try
        {
            MoveActiveWindowCore(delta);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private void MoveActiveWindowCore(int delta)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsEligible(fg)) return;
        // When the active window cannot be controlled (e.g. an elevated window), moving is
        // meaningless: do not create a desktop and do not move — only notify (no repeated log
        // entries; the balloon is de-duplicated per session by the tray layer).
        if (!CanReassignWindow(fg, nameof(MoveActiveWindow))) return;
        Sync();
        string? dev = Native.GetMonitorDeviceOfWindow(fg);
        if (dev == null || !_monitors.TryGetValue(dev, out var st)) return;

        int target = st.Current + delta;
        if (target < 0) return;
        bool createdDesktop = false;
        if (target >= st.Desktops.Count)
        {
            if (delta <= 0 || st.Desktops.Count >= MaxDesktopsPerMonitor) return;
            AddDesktop(st);
            createdDesktop = true;
            target = st.Desktops.Count - 1;
        }

        if (!HideOrParkManagedWindows(st.Desktops[st.Current].Where(h => h != fg)))
        {
            if (createdDesktop)
            {
                st.Desktops.RemoveAt(target);
                st.LastActive.RemoveAt(target);
            }
            return;
        }

        st.Desktops[st.Current].Remove(fg);
        AddWindow(st.Desktops[target], fg);
        st.LastActive[target] = fg;
        SwitchToCore(st, target);
    }

    /// <summary>Bir pencereyi herhangi bir monitörün herhangi bir masaüstüne taşır
    /// (genel bakıştaki sürükle-bırak ve sağ tık menüsü bunu kullanır).</summary>
    public void MoveWindowToDesktop(IntPtr h, string dstDevice, int dstLocal)
    {
        BeginIdentityCache();
        try
        {
            MoveWindowToDesktopCore(h, dstDevice, dstLocal);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private void MoveWindowToDesktopCore(IntPtr h, string dstDevice, int dstLocal)
    {
        Sync();
        if (!Native.IsWindow(h)) return;
        if (!_monitors.TryGetValue(dstDevice, out var dst)) return;
        if (dstLocal < 0 || dstLocal >= dst.Desktops.Count) return;
        if (!CanReassignWindow(h, nameof(MoveWindowToDesktop))) return;
        string? srcDevice = FindWindowDevice(h);
        bool wasAccessible = Native.IsWindowVisible(h) && !IsWindowOffScreen(h);
        if (dst.Current != dstLocal && !HideOrParkManagedWindows(new[] { h })) return;

        if (srcDevice != null && srcDevice != dstDevice &&
            !RepositionWindow(h, srcDevice, dstDevice))
        {
            // Hiding/parking happens before cross-display repositioning. If the
            // source desktop was visible, undo that presentation change as well;
            // desktop membership has deliberately not been touched yet.
            if (wasAccessible)
                ShowOrUnparkManagedWindow(h);
            PersistHidden();
            return;
        }

        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                set.Remove(h);

        AddWindow(dst.Desktops[dstLocal], h);
        dst.LastActive[dstLocal] = h;

        if (dst.Current == dstLocal)
        {
            ShowOrUnparkManagedWindow(h);
        }

        foreach (var st in _monitors.Values) PruneTrailingEmpty(st);
        PersistHidden();
    }

    private string? FindWindowDevice(IntPtr h)
    {
        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                if (set.Contains(h))
                    return st.Device;
        return null;
    }

    /// <summary>Bir masaüstünü aynı veya başka monitörde belirtilen ekleme konumuna taşır.</summary>
    public bool MoveDesktop(string srcDevice, int srcLocal, string dstDevice, int dstInsertIndex)
    {
        BeginIdentityCache();
        try
        {
            return MoveDesktopCore(srcDevice, srcLocal, dstDevice, dstInsertIndex);
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private bool MoveDesktopCore(string srcDevice, int srcLocal, string dstDevice, int dstInsertIndex)
    {
        Sync();
        if (!_monitors.TryGetValue(srcDevice, out var src)) return false;
        if (!_monitors.TryGetValue(dstDevice, out var dst)) return false;
        if (srcLocal < 0 || srcLocal >= src.Desktops.Count) return false;

        if (srcDevice == dstDevice)
        {
            var current = src.Desktops[src.Current];
            var reorderedDesktop = src.Desktops[srcLocal];
            var reorderedLastActive = src.LastActive[srcLocal];

            dstInsertIndex = Math.Clamp(dstInsertIndex, 0, src.Desktops.Count);
            src.Desktops.RemoveAt(srcLocal);
            src.LastActive.RemoveAt(srcLocal);
            if (dstInsertIndex > srcLocal) dstInsertIndex--;

            src.Desktops.Insert(dstInsertIndex, reorderedDesktop);
            src.LastActive.Insert(dstInsertIndex, reorderedLastActive);
            src.Current = src.Desktops.IndexOf(current);
            PersistHidden();
            return true;
        }

        if (dst.Desktops.Count >= MaxDesktopsPerMonitor) return false;
        dstInsertIndex = Math.Clamp(dstInsertIndex, 0, dst.Desktops.Count);

        var set = src.Desktops[srcLocal];
        // Repositioning a whole desktop across displays is all-or-nothing. A
        // single integrity-skipped window must reject the operation before the
        // source/destination lists or any window geometry are changed.
        foreach (IntPtr h in set.Where(Native.IsWindow))
            if (!CanReassignWindow(h, nameof(MoveDesktop)))
                return false;
        bool wasCurrent = src.Current == srcLocal;
        if (!HideOrParkManagedWindows(set)) return false;
        var last = src.LastActive[srcLocal];
        var dstCurrent = dst.Desktops[dst.Current];

        // Do not mutate either desktop list until every physical window move has
        // been verified. If one move fails, return already moved windows to the
        // source display and restore visibility when the source desktop was active.
        var repositioned = new List<IntPtr>();
        foreach (var h in set.ToList())
        {
            if (!Native.IsWindow(h)) { set.Remove(h); continue; }
            if (RepositionWindow(h, srcDevice, dstDevice))
            {
                repositioned.Add(h);
                continue;
            }

            bool geometryRolledBack = true;
            foreach (IntPtr moved in repositioned.AsEnumerable().Reverse())
                geometryRolledBack &= RepositionWindow(moved, dstDevice, srcDevice);
            bool presentationRolledBack = !wasCurrent || RestoreManagedWindows(set);
            PersistHidden();
            if (!geometryRolledBack || !presentationRolledBack)
                AppLog.Warning(nameof(MoveDesktop),
                    "Cross-display desktop move was cancelled, but rollback was incomplete.");
            return false;
        }

        src.Desktops.RemoveAt(srcLocal);
        src.LastActive.RemoveAt(srcLocal);
        if (src.Desktops.Count == 0) AddDesktop(src);
        if (src.Current > srcLocal) src.Current--;
        if (src.Current >= src.Desktops.Count) src.Current = src.Desktops.Count - 1;

        dst.Desktops.Insert(dstInsertIndex, set);
        dst.LastActive.Insert(dstInsertIndex, last);
        dst.Current = dst.Desktops.IndexOf(dstCurrent);

        // Kaynak monitörde aktif masaüstü taşındıysa kalan aktif masaüstünü görünür yap
        if (wasCurrent)
            foreach (var h in src.Desktops[src.Current])
                ShowOrUnparkManagedWindow(h);

        PruneTrailingEmpty(src);
        PersistHidden();
        return true;
    }

    /// <summary>Pencereyi kaynak monitördeki göreli konumunu koruyarak hedef monitöre taşır.
    /// For a parked window: map the saved rectangle by DPI and transfer it to the target display's parking area.</summary>
    private bool RepositionWindow(IntPtr h, string srcDevice, string dstDevice)
    {
        HiddenWindowRecord? hiddenRecord = null;
        if (_hidden.TryGetValue(h, out var rec))
        {
            if (rec.Parked)
                return RepositionParkedWindow(h, rec, srcDevice, dstDevice);
            hiddenRecord = rec;
        }

        var srcScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == srcDevice);
        var dstScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == dstDevice);
        if (srcScreen == null || dstScreen == null) return false;
        bool wasVisible = Native.IsWindowVisible(h);
        bool wasMaximized = Native.IsZoomed(h) ||
                            hiddenRecord?.SavedShowCmd == Native.SW_SHOWMAXIMIZED;
        // A minimized window is its own case: SetWindowPos only moves the
        // minimized stub (rcNormalPosition stays on the source display), and
        // since 25H2 SetWindowPlacement ignores rcNormalPosition for minimized
        // windows entirely (see RestoreIconicPlacement). Neither can move the
        // restore rectangle across displays; treat iconic first, even when the
        // placement would restore maximized (WPF_RESTORETOMAXIMIZED is kept).
        bool wasIconic = Native.IsIconic(h);

        var originalPlacement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        bool hasPlacement = Native.GetWindowPlacement(h, ref originalPlacement);
        bool hasWindowRect = Native.GetWindowRect(h, out Native.RECT originalWindowRect);

        Rectangle sourceRect;
        if (hiddenRecord != null && HasSavedPlacement(hiddenRecord))
            sourceRect = Rectangle.FromLTRB(hiddenRecord.NormalLeft, hiddenRecord.NormalTop,
                hiddenRecord.NormalRight, hiddenRecord.NormalBottom);
        else if (hasPlacement)
            sourceRect = FromRECT(originalPlacement.rcNormalPosition);
        else if (hasWindowRect)
            sourceRect = FromRECT(originalWindowRect);
        else
            return false;

        sourceRect = NormalizeWindowRectForScreen(sourceRect, srcScreen);
        Rectangle mapped = MapWindowRect(ToRECT(sourceRect), srcScreen, dstScreen);
        bool applied = false;
        int? win32Error = null;
        for (int attempt = 1; attempt <= ParkAttemptCount; attempt++)
        {
            if (wasIconic)
            {
                // Move the restore rectangle through the verified show -> place ->
                // re-minimize chain. For a hidden window the fix-up below re-hides it.
                applied = RestoreIconicPlacement(h, mapped);
            }
            else if (wasMaximized && hasPlacement)
            {
                var targetPlacement = originalPlacement;
                targetPlacement.rcNormalPosition = ToRECT(mapped);
                targetPlacement.showCmd = (uint)(wasVisible ? Native.SW_MAXIMIZE : Native.SW_HIDE);
                applied = Native.SetWindowPlacement(h, ref targetPlacement);
            }
            else
            {
                applied = Native.SetWindowPos(h, IntPtr.Zero,
                    mapped.X, mapped.Y, mapped.Width, mapped.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            if (!applied && !wasIconic)
                win32Error = Marshal.GetLastWin32Error();

            // SetWindowPlacement also controls the show state. Enforce the original
            // hidden state in case a third-party window changes it while moving.
            if (!wasVisible && Native.IsWindowVisible(h))
                Native.ShowWindow(h, Native.SW_HIDE);

            // For a minimized window the restore rectangle is the authoritative
            // position (MonitorFromWindow also resolves iconic windows via their
            // restore position, so it follows once the placement moved).
            bool assigned = wasIconic
                ? IsNormalPlacementAssignedToDisplay(h, dstDevice)
                : IsWindowPhysicallyAssignedToDisplay(h, dstDevice) ||
                  (!wasVisible && hiddenRecord != null &&
                   IsNormalPlacementAssignedToDisplay(h, dstDevice));
            if (applied && assigned)
            {
                if (hiddenRecord != null)
                {
                    SetHiddenRecord(h, hiddenRecord with
                    {
                        NormalLeft = mapped.Left,
                        NormalTop = mapped.Top,
                        NormalRight = mapped.Right,
                        NormalBottom = mapped.Bottom,
                        ParkMonitor = dstDevice
                    });
                }
                return true;
            }

            if (attempt < ParkAttemptCount)
            {
                AppLog.Info(nameof(RepositionWindow),
                    $"Cross-display positioning verification failed for HWND={h}; " +
                    $"retry {attempt + 1}/{ParkAttemptCount} scheduled in {ParkRetryDelayMs} ms.");
                Thread.Sleep(ParkRetryDelayMs);
            }
        }

        bool rolledBack;
        if (wasIconic && hasPlacement)
        {
            // Put the restore rectangle back on the source display with the same
            // chain; a SetWindowPos rollback is meaningless for a minimized stub.
            rolledBack = RestoreIconicPlacement(h, FromRECT(originalPlacement.rcNormalPosition));
        }
        else if (wasMaximized && hasPlacement)
        {
            var rollbackPlacement = originalPlacement;
            if (!wasVisible)
                rollbackPlacement.showCmd = Native.SW_HIDE;
            rolledBack = Native.SetWindowPlacement(h, ref rollbackPlacement);
        }
        else if (hasWindowRect)
        {
            rolledBack = Native.SetWindowPos(h, IntPtr.Zero,
                originalWindowRect.Left, originalWindowRect.Top,
                Math.Max(1, originalWindowRect.Right - originalWindowRect.Left),
                Math.Max(1, originalWindowRect.Bottom - originalWindowRect.Top),
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        else
        {
            rolledBack = false;
        }

        if (!wasVisible && Native.IsWindowVisible(h))
            Native.ShowWindow(h, Native.SW_HIDE);
        else if (wasVisible && !Native.IsWindowVisible(h))
            Native.ShowWindow(h, Native.SW_SHOWNA);

        RaiseWindowControlWarning(nameof(RepositionWindow),
            $"Could not reposition HWND={h} from '{srcDevice}' to '{dstDevice}' after " +
            $"{ParkAttemptCount} attempts; apiSucceeded={applied}, " +
            $"win32Error={(win32Error?.ToString() ?? "null")}, rollbackSucceeded={rolledBack}", h);
        return false;
    }

    private static bool IsWindowPhysicallyAssignedToDisplay(IntPtr h, string device) =>
        Native.GetMonitorDeviceOfWindow(h) == device;

    private static bool IsNormalPlacementAssignedToDisplay(IntPtr h, string device)
    {
        var placement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        if (!Native.GetWindowPlacement(h, ref placement)) return false;
        Rectangle normal = FromRECT(placement.rcNormalPosition);
        return normal.Width > 0 && normal.Height > 0 &&
               Screen.FromRectangle(normal).DeviceName == device;
    }

    /// <summary>Move a parked window across displays: map its saved on-screen rectangle by DPI and transfer the window to the target display's parking area.</summary>
    private bool RepositionParkedWindow(IntPtr h, HiddenWindowRecord rec, string srcDevice, string dstDevice)
    {
        var srcScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == srcDevice);
        var dstScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == dstDevice);
        if (srcScreen == null || dstScreen == null) return false;
        if (!Native.IsWindow(h) || !MatchesWindowIdentity(h, rec)) return false;

        var saved = Rectangle.FromLTRB(rec.NormalLeft, rec.NormalTop, rec.NormalRight, rec.NormalBottom);
        Rectangle mapped = MapWindowRect(ToRECT(saved), srcScreen, dstScreen);

        bool iconic = Native.IsIconic(h);
        Size size = mapped.Size;
        if (!iconic && Native.GetWindowRect(h, out var wr))
            size = new Size(wr.Right - wr.Left, wr.Bottom - wr.Top);

        Rectangle park;
        if (iconic)
        {
            if (!TryGetMinimizedParkRect(dstDevice, mapped.Size, out park))
            {
                AppLog.Warning(nameof(RepositionParkedWindow),
                    $"No safe minimized parking anchor exists for HWND={h} on '{dstDevice}'.");
                return false;
            }
        }
        else
        {
            park = GetParkRect(dstDevice, size);
        }

        var updated = rec with
        {
            NormalLeft = mapped.Left,
            NormalTop = mapped.Top,
            NormalRight = mapped.Right,
            NormalBottom = mapped.Bottom,
            SavedShowCmd = iconic ? Native.SW_SHOWMINIMIZED : rec.SavedShowCmd,
            ParkMonitor = dstDevice
        };
        var target = new ParkCandidate(h, updated, park, iconic);
        bool moved = TryParkCandidateWithRetry(target, nameof(RepositionParkedWindow),
            out ParkOutcome outcome, out ParkAttemptDiag? diag);
        if (moved)
        {
            SetHiddenRecord(h, updated);
            return true;
        }

        // A failed target move can leave the HWND on-screen even when the Win32
        // call returned success. Put it back in a valid source parking area before
        // returning; the original recovery record remains authoritative throughout.
        bool rolledBack = outcome is ParkOutcome.Destroyed or ParkOutcome.IdentityMismatch;
        if (outcome == ParkOutcome.Failed && Native.IsWindow(h) && MatchesWindowIdentity(h, rec))
        {
            Rectangle sourcePark;
            if (iconic)
            {
                rolledBack = TryGetMinimizedParkRect(srcDevice, saved.Size, out sourcePark) &&
                    TryParkCandidateWithRetry(new ParkCandidate(h, rec, sourcePark, true),
                        nameof(RepositionParkedWindow), out _, out _);
            }
            else
            {
                sourcePark = GetParkRect(srcDevice, size);
                rolledBack = TryParkCandidateWithRetry(new ParkCandidate(h, rec, sourcePark, false),
                    nameof(RepositionParkedWindow), out _, out _);
            }
        }

        if (outcome is ParkOutcome.Destroyed or ParkOutcome.IdentityMismatch)
            RemoveHiddenRecord(h);

        string diagnostic = diag == null
            ? $"outcome={outcome}"
            : $"outcome={outcome}, apiSucceeded={diag.ApiSucceeded}, " +
              $"win32Error={(diag.Win32Error?.ToString() ?? "null")}, " +
              $"windowRect={FormatRect(diag.WindowRect)}, normalRect={FormatRect(diag.NormalRect)}";
        RaiseWindowControlWarning(nameof(RepositionParkedWindow),
            $"Could not re-park HWND={h} on '{dstDevice}'; " +
            $"rollbackSucceeded={rolledBack}; {diagnostic}", h);
        return false;
    }

    private bool TryParkCandidateWithRetry(ParkCandidate candidate, string operation,
        out ParkOutcome outcome, out ParkAttemptDiag? diag)
    {
        (outcome, diag) = TryParkOnce(candidate);
        for (int attempt = 2;
             outcome == ParkOutcome.Failed && attempt <= ParkAttemptCount;
             attempt++)
        {
            AppLog.Info(operation,
                $"Parking verification failed for HWND={candidate.Handle}; " +
                $"retry {attempt}/{ParkAttemptCount} scheduled in {ParkRetryDelayMs} ms.");
            Thread.Sleep(ParkRetryDelayMs);
            (outcome, diag) = TryParkOnce(candidate);
        }
        return outcome == ParkOutcome.Parked;
    }

    private static Rectangle MapWindowRect(Native.RECT rect, Screen srcScreen, Screen dstScreen)
    {
        var sourceArea = srcScreen.WorkingArea;
        var destinationArea = dstScreen.WorkingArea;

        uint sourceDpi = GetScreenDpi(srcScreen);
        uint destinationDpi = GetScreenDpi(dstScreen);
        double dpiRatio = sourceDpi > 0 ? destinationDpi / (double)sourceDpi : 1.0;

        int originalWidth = Math.Max(1, rect.Right - rect.Left);
        int originalHeight = Math.Max(1, rect.Bottom - rect.Top);
        int width = Math.Clamp((int)Math.Round(originalWidth * dpiRatio), 1, destinationArea.Width);
        int height = Math.Clamp((int)Math.Round(originalHeight * dpiRatio), 1, destinationArea.Height);

        double relativeX = sourceArea.Width > 0
            ? (rect.Left - sourceArea.Left) / (double)sourceArea.Width
            : 0;
        double relativeY = sourceArea.Height > 0
            ? (rect.Top - sourceArea.Top) / (double)sourceArea.Height
            : 0;
        int x = destinationArea.Left + (int)Math.Round(relativeX * destinationArea.Width);
        int y = destinationArea.Top + (int)Math.Round(relativeY * destinationArea.Height);
        x = Math.Clamp(x, destinationArea.Left, Math.Max(destinationArea.Left, destinationArea.Right - width));
        y = Math.Clamp(y, destinationArea.Top, Math.Max(destinationArea.Top, destinationArea.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private static Rectangle NormalizeWindowRectForScreen(Rectangle rect, Screen targetScreen)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return rect;
        Screen currentScreen = Screen.FromRectangle(rect);
        return currentScreen.DeviceName == targetScreen.DeviceName
            ? rect
            : MapWindowRect(ToRECT(rect), currentScreen, targetScreen);
    }

    private static uint GetScreenDpi(Screen screen)
    {
        var bounds = screen.Bounds;
        var point = new Native.POINT
        {
            X = bounds.Left + bounds.Width / 2,
            Y = bounds.Top + bounds.Height / 2
        };
        return Native.GetEffectiveMonitorDpi(
            Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>Tüm gizli/parked pencereleri geri getirir (çıkışta ve tray menüsünden çağrılır).</summary>
    public void RestoreAll()
    {
        foreach (var st in _monitors.Values)
            st.Current = 0;

        foreach (var h in _hidden.Keys.ToList())
            UnparkManagedWindow(h);
        PersistHidden();
    }
}
