using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IndepenDesk;

/// <summary>Geçiş bilgisi: OSD için.</summary>
internal sealed record SwitchInfo(string Device, int LocalIndex);

internal sealed record WindowEntry(IntPtr Handle, string Title);
internal sealed record DesktopEntry(int LocalIndex, bool IsCurrent, IReadOnlyList<WindowEntry> Windows);
internal sealed record MonitorEntry(string Device, int Ordinal, bool CanCreateDesktop,
    IReadOnlyList<DesktopEntry> Desktops);

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
        public required string StableId;
        public List<DesktopState> Desktops = new();
        public int Current;
        public DisplaySnapshot? LastDisplay;
    }

    /// <summary>
    /// A desktop has its own runtime identity.  HomeStableId is its durable owner for
    /// the lifetime of this process; HostStableId is the display currently presenting
    /// it.  The two differ while a sleeping laptop is resumed without one of the
    /// displays that was present at suspend time.
    /// </summary>
    private sealed class DesktopState
    {
        public readonly Guid RuntimeId = Guid.NewGuid();
        public readonly HashSet<IntPtr> Windows = new();
        public IntPtr LastActive;
        public required string HomeStableId;
        public required string HostStableId;
        public bool ReturnWhenOnline;
        public int OriginalOrder;
    }

    private sealed record WindowTransitSnapshot(
        HiddenWindowRecord Identity,
        Rectangle NormalRect,
        DisplaySnapshot NormalRectDisplay,
        uint ShowCmd,
        Rectangle? BorrowedBaseline = null,
        DisplaySnapshot? BorrowedDisplay = null);

    private sealed class BorrowedDisplayState
    {
        public required DisplaySnapshot HomeDisplay;
        public required List<Guid> OriginalOrder;
        public required Guid PreferredCurrent;
        public required string HostStableId;
        public required Guid HostPreviousCurrent;
    }

    /// <summary>
    /// One window's pre-computed migration back to its home display: the recovery
    /// record that must become authoritative (journal-first) plus the physical
    /// placement that still has to be materialized and verified.
    /// </summary>
    private sealed record TopologyPlacementPlan(
        IntPtr Handle,
        HiddenWindowRecord? OriginalRecord,
        HiddenWindowRecord? TargetRecord,
        Rectangle TargetRect,
        string TargetDevice,
        string SourceDevice,
        uint SavedShowCmd,
        DisplaySnapshot Destination,
        DesktopState? Desktop);

    /// <summary>
    /// In-process marker that a window's topology placement has not landed yet:
    /// the journal already points at the home display while the physical placement
    /// may still be on the borrowed one. Prevents Sync/taskbar paths from
    /// interpreting the stale physical position as an app-initiated cross-display
    /// move. Deliberately not persisted (the journal already holds the target).
    /// </summary>
    private sealed record PendingTopologyPlacement(
        IntPtr Handle,
        HiddenWindowRecord TargetRecord,
        string TargetDevice,
        Rectangle TargetRect,
        bool Parked,
        long MuteLogUntil,
        long RetryNotBeforeTick);

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
    // Some applications apply window-state changes asynchronously (position clamps,
    // throttled background renderers). A state read taken immediately after the API
    // call can therefore misread a pending change as a refusal; give the batch one
    // bounded settle wait before spending retry rounds (and re-issuing commands,
    // which is what made windows visibly bounce).
    private const int ParkVerifyPollDelayMs = 25;
    private const int ParkVerifyPollCount = 10;
    private const int ParkAnchorThickness = 2;
    private const int ParkRectTolerance = TopologyDecisions.RectTolerance;
    private const int MinimizeForegroundSuppressMs = 750;
    // After a display topology change many applications react to WM_DISPLAYCHANGE by
    // restoring windows that IndepenDesk had parked on a non-current desktop. Those
    // windows have a clear desktop membership and must be re-parked, not adopted into
    // the current desktop (which would silently empty their home desktops). The
    // disturbed period starts when the topology transaction completes.
    private const int TopologyDisturbanceMs = 5000;
    // A pending topology placement keeps failing (window hung, app clamping its
    // position): the retry warning is re-emitted at most this often per HWND so a
    // stuck window cannot flood the log from the periodic sync.
    private const long PendingPlacementLogIntervalMs = 30000;
    // Minimum spacing between two physical retry attempts for the same pending
    // placement: the periodic sync runs on the UI thread as often as every second,
    // and a responsive-but-refusing window costs a full bounded retry round each
    // time (see TryParkCandidateWithRetry).
    private const long PendingPlacementRetryMs = 2000;

    private readonly Dictionary<string, MonitorState> _monitors = new();
    private readonly Dictionary<IntPtr, HiddenWindowRecord> _hidden = new();
    private readonly Dictionary<IntPtr, PendingTopologyPlacement> _pendingTopologyPlacements = new();
    private readonly HashSet<DesktopState> _retainedEmptyDesktops = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, BorrowedDisplayState> _borrowedDisplays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, WindowTransitSnapshot> _sleepWindowSnapshots = new();
    private Dictionary<string, DisplaySnapshot> _displayTopology = new(StringComparer.OrdinalIgnoreCase);
    private bool _topologyTransitionInProgress;
    private bool _hasSuspendSnapshot;
    private bool _borrowedSinceSuspend;
    private long _topologyDisturbanceUntil;
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
    /// Shared taskbar mode: windows on non-current desktops are not hidden but parked
    /// (minimized in place; edge-anchored when already minimized at capture).
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
        // A journal rewrite to a different placement target invalidates any pending
        // topology placement that still points at the old display/rectangle.
        if (_pendingTopologyPlacements.TryGetValue(h, out var pending) &&
            TopologyDecisions.ShouldWithdrawPending(
                pending.TargetDevice, pending.TargetRect, record.ParkMonitor,
                Rectangle.FromLTRB(record.NormalLeft, record.NormalTop,
                    record.NormalRight, record.NormalBottom)))
        {
            _pendingTopologyPlacements.Remove(h);
            AppLog.Info(nameof(SetHiddenRecord),
                $"Pending topology placement of HWND={h} withdrawn: the journal target changed.");
        }
        _hidden[h] = record;
        _hiddenVersion++;
    }

    private bool RemoveHiddenRecord(IntPtr h)
    {
        _pendingTopologyPlacements.Remove(h);
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
    /// <summary>Window liveness probe budget; healthy windows answer in well under a millisecond.</summary>
    private const uint HangProbeTimeoutMs = 50;

    /// <summary>
    /// Cheap liveness probe. IsHungAppWindow only reports windows the system has already
    /// flagged (some earlier message to them timed out); a suspended process with an
    /// empty queue stays unflagged until the first blocked call. A WM_NULL round-trip
    /// with SMTO_ABORTIFHUNG fails within the budget for both flagged-hung and merely
    /// suspended windows, so callers can refuse them before any blocking call.
    /// </summary>
    internal static bool WindowResponds(IntPtr h)
    {
        Native.SendMessageTimeout(h, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero,
            Native.SMTO_ABORTIFHUNG, HangProbeTimeoutMs, out _);
        return Marshal.GetLastWin32Error() == 0;
    }

    /// <summary>
    /// Whether it is safe to send a window synchronous messages right now. ShowWindow,
    /// SetWindowPos and SetForegroundWindow deliver their effects through the target
    /// window's thread; calling one on a hung or suspended window blocks this UI thread
    /// indefinitely and freezes all desktop management until that app recovers (observed
    /// in the wild with an unresponsive WinUI Notepad). An unresponsive window is treated
    /// as a transient refusal so the batched retries and rollback paths resolve it in
    /// bounded time.
    /// </summary>
    private static bool SafeToModifyWindow(IntPtr h) =>
        Native.IsWindow(h) && !Native.IsHungAppWindow(h) && WindowResponds(h);

    private bool TryHideOnce((IntPtr Handle, HiddenWindowRecord Record) candidate)
    {
        if (!SafeToModifyWindow(candidate.Handle)) return false;
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
            if (!Native.IsWindowVisible(candidate.Handle) && SafeToModifyWindow(candidate.Handle))
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
                if (!Native.IsWindowVisible(h) && SafeToModifyWindow(h))
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
        if (!SafeToModifyWindow(h)) return false;
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
    //
    // Normal & maximized windows are parked by MINIMIZING them (geometry untouched,
    // restore rectangle stays at the recorded on-screen position): a minimize is
    // applied immediately by the shell/DWM, while SetWindowPos to an off-screen
    // position takes ~500 ms to land for maximized windows (the window visibly
    // bounces in and out on every switch). The taskbar button is kept, preserving
    // shared taskbar semantics; unparking restores the recorded state normally.
    // Windows already minimized at capture time keep position-anchored parking.

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
        // Any minimized window with a parked record is logically parked: normal and
        // maximized windows are parked by minimizing them, and apps (Chromium & co.)
        // rewriting rcNormalPosition cannot break that representation.
        if (iconic) return true;
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
    /// the snapshot and verify the parked state. Only the current window state is refreshed; the
    /// recovery geometry captured first time is never touched (c.Record stays unchanged).</summary>
    private (ParkOutcome Outcome, ParkAttemptDiag? Diag) TryParkOnce(ParkCandidate c)
    {
        if (!Native.IsWindow(c.Handle)) return (ParkOutcome.Destroyed, null);
        if (!MatchesWindowIdentity(c.Handle, c.Record)) return (ParkOutcome.IdentityMismatch, null);
        // Never send window messages to an unresponsive app: the call would block the UI
        // thread until it recovers. Report it as a failed attempt with a full diagnostic
        // so the retry rounds and the rollback log have something to describe.
        if (!SafeToModifyWindow(c.Handle))
            return (ParkOutcome.Failed,
                new ParkAttemptDiag(false, null, true, Native.IsWindowVisible(c.Handle),
                    Native.IsIconic(c.Handle), Native.IsZoomed(c.Handle), null, null, null));

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
            // Park normal & maximized windows by minimizing them: the shell applies
            // a minimize immediately and atomically, whereas SetWindowPos to an
            // off-screen rectangle needs ~500 ms to land for a maximized window and
            // makes it visibly bounce (see the section comment above). The geometry
            // stays exactly where it was; unparking restores the recorded state.
            MarkInternalMinimize(c.Handle);
            applied = Native.ShowWindow(c.Handle, Native.SW_SHOWMINNOACTIVE);
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
            parked = applied && Native.IsIconic(c.Handle);
        }
        return (parked ? ParkOutcome.Parked : ParkOutcome.Failed,
            new ParkAttemptDiag(applied, win32Error, true, visible, iconic, zoomed,
                windowRect, normalRect, showCmd));
    }

    /// <summary>Whether a park issued earlier has by now landed (the same predicate as
    /// TryParkOnce's verification, re-evaluated after a settle wait — cheap, no API calls
    /// against the window beyond placement/rect queries).</summary>
    private static bool HasParkLanded(ParkCandidate c)
    {
        if (!Native.IsWindow(c.Handle)) return false;
        if (!c.WasIconic)
            return Native.IsIconic(c.Handle);
        if (!Native.IsIconic(c.Handle)) return false;
        var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(c.Handle, ref pl)) return false;
        Rectangle normal = FromRECT(pl.rcNormalPosition);
        return RectApproximatelyEquals(normal, c.ParkRect) && IsEffectivelyParkedRect(normal);
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
    /// Parks the manageable windows of a desktop (see the section comment for the two
    /// parking representations). Windows that are known to be outside our integrity
    /// boundary are skipped and stay visible. Operational failures are transient:
    /// roll this attempt back and let a later switch retry.
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

        // The first pass may have misread a still-pending state change as a refusal
        // (some applications apply them asynchronously — see ParkVerifyPollDelayMs).
        // Give the whole group ONE bounded settle wait before spending retry rounds:
        // a change that lands on its own then consumes no retry and, crucially,
        // no second command is issued, so the window does not visibly bounce in and
        // out of the parked state.
        for (int i = 0; i < ParkVerifyPollCount && pending.Count > 0; i++)
        {
            Thread.Sleep(ParkVerifyPollDelayMs);
            pending.RemoveAll(HasParkLanded);
        }

        // Batched backoff retry: the whole group waits only once per round (100 ms), so the number of failed windows does not amplify the total delay.
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
        string api = c.WasIconic ? "SetWindowPlacement" : "ShowWindow(SW_SHOWMINNOACTIVE)";
        string mode = c.WasIconic ? "minimized-edge-anchor" : "minimized-in-place";
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
        if (!SafeToModifyWindow(h)) return false;
        if (!MatchesWindowIdentity(h, record))
        {
            RemoveHiddenRecord(h);
            return false;
        }
        if (!IsManagedWindowParked(h, record))
        {
            // A pending topology placement means the journal still points at the
            // window's home display while the app already put the window back
            // on-screen on the borrowed one. The record is authoritative: fall
            // through to the recorded placement instead of adopting the on-screen
            // position (the placement is not "done" until it lands on the target).
            if (TopologyDecisions.DecideUnparkEarlyReturn(_pendingTopologyPlacements.ContainsKey(h)) ==
                TopologyDecisions.UnparkEarlyReturn.AdoptOnScreenPosition)
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
            AppLog.Info(nameof(UnparkManagedWindow),
                $"HWND={h} is on-screen before its topology placement landed; " +
                $"restoring it to the recorded display '{record.ParkMonitor}'.");
        }
        if (!RestoreParkedWindow(h, record))
        {
            AppLog.Warning(nameof(UnparkManagedWindow), $"Could not restore parked HWND={h}.");
            return false;
        }
        if (Native.IsIconic(h) && !WasSavedMinimized(record))
        {
            // This window was normal/maximized when recorded: we minimized it to
            // park it (geometry untouched). Bring it back in its recorded state.
            // A genuinely user-minimized window (WasSavedMinimized) stays minimized.
            if (record.SavedShowCmd == Native.SW_SHOWMAXIMIZED)
            {
                var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
                if (Native.GetWindowPlacement(h, ref pl))
                {
                    pl.showCmd = Native.SW_SHOWMAXIMIZED;
                    Native.SetWindowPlacement(h, ref pl);
                }
            }
            else
            {
                Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
            }
            if (Native.IsIconic(h))
            {
                AppLog.Warning(nameof(UnparkManagedWindow),
                    $"HWND={h} could not be restored from its parking minimize.");
                return false;
            }
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

    /// <summary>
    /// Numbering order for monitor ordinals and the Ctrl+Alt+1..9 global desktop
    /// numbers: the internal display always comes first, external displays follow by
    /// screen position (left-to-right, top-to-bottom within each group). Offline
    /// monitors keep their group but sort last inside it.
    /// </summary>
    private List<MonitorState> OrderedMonitors()
    {
        var bounds = Screen.AllScreens.ToDictionary(s => s.DeviceName, s => s.Bounds);
        return _monitors.Values
            .OrderBy(m => !IsInternalMonitor(m))
            .ThenBy(m => bounds.TryGetValue(m.Device, out var b) ? b.X : int.MaxValue)
            .ThenBy(m => bounds.TryGetValue(m.Device, out var b) ? b.Y : 0)
            .ToList();
    }

    /// <summary>Whether this monitor is a built-in panel; an offline monitor keeps its last known identity.</summary>
    private static bool IsInternalMonitor(MonitorState m) => m.LastDisplay?.IsInternal == true;

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

    private static DesktopState AddDesktop(MonitorState st)
    {
        var desktop = new DesktopState
        {
            HomeStableId = st.StableId,
            HostStableId = st.StableId,
            OriginalOrder = st.Desktops.Count
        };
        st.Desktops.Add(desktop);
        return desktop;
    }

    private static int OwnedDesktopCount(MonitorState st) => st.Desktops.Count(d =>
        string.Equals(d.HomeStableId, st.StableId, StringComparison.OrdinalIgnoreCase));

    private bool AddWindow(DesktopState desktop, IntPtr h)
    {
        if (!desktop.Windows.Add(h)) return false;
        _retainedEmptyDesktops.Remove(desktop);
        return true;
    }

    /// <summary>Aktif masaüstünün gerisindeki boş son masaüstlerini kaldırır.</summary>
    private bool PruneTrailingEmpty(MonitorState st)
    {
        bool changed = false;
        while (st.Desktops.Count - 1 > st.Current &&
               st.Desktops[^1].Windows.Count == 0 &&
               !_retainedEmptyDesktops.Contains(st.Desktops[^1]))
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
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

    /// <summary>Freeze ordinary reconciliation and take a bounded in-memory snapshot before suspend.</summary>
    public void PrepareForSuspend()
    {
        bool transitionAlreadyArmed = _topologyTransitionInProgress;
        if (!transitionAlreadyArmed)
        {
            Sync();
            _topologyTransitionInProgress = true;
        }
        _hasSuspendSnapshot = true;
        _borrowedSinceSuspend = _borrowedDisplays.Count > 0;
        if (!transitionAlreadyArmed || _sleepWindowSnapshots.Count == 0)
            CaptureWindowTransitSnapshots();
        AppLog.Info(nameof(PrepareForSuspend),
            $"Suspend snapshot captured: {_monitors.Count} display(s), {_sleepWindowSnapshots.Count} window(s).");
    }

    /// <summary>Keep periodic Sync from seeing a transient one-display resume topology.</summary>
    public void BeginResume()
    {
        _topologyTransitionInProgress = true;
        AppLog.Info(nameof(BeginResume), "Resume topology stabilization started.");
    }

    /// <summary>Display-change messages use the same reversible in-memory topology transaction.</summary>
    public void BeginDisplayTopologyChange()
    {
        if (_topologyTransitionInProgress) return;
        CaptureWindowTransitSnapshots();
        _topologyTransitionInProgress = true;
    }

    /// <summary>Apply a stable topology sample, then resume normal window reconciliation.</summary>
    public void CompleteDisplayTopologyChange()
    {
        BeginIdentityCache();
        try
        {
            ReconcileDisplayTopology(DisplayTopology.Capture());
        }
        finally
        {
            EndIdentityCache();
            _topologyTransitionInProgress = false;
        }

        _topologyDisturbanceUntil = Environment.TickCount64 + TopologyDisturbanceMs;
        Sync();
        AppLog.Info(nameof(CompleteDisplayTopologyChange),
            $"Topology stabilization completed; {_monitors.Count} display(s) online, " +
            $"{_borrowedDisplays.Count} borrowed display group(s).");
    }

    public bool TopologyTransitionInProgress => _topologyTransitionInProgress;

    public void CancelDisplayTopologyChange()
    {
        _topologyTransitionInProgress = false;
        _topologyDisturbanceUntil = Environment.TickCount64 + TopologyDisturbanceMs;
        AppLog.Warning(nameof(CancelDisplayTopologyChange),
            "Topology stabilization was cancelled after an unexpected error; periodic sync resumed.");
    }

    private void CaptureWindowTransitSnapshots()
    {
        if (_borrowedDisplays.Count == 0)
            _sleepWindowSnapshots.Clear();

        BeginIdentityCache();
        try
        {
            foreach (MonitorState monitor in _monitors.Values)
            {
                DisplaySnapshot? placementDisplay = monitor.LastDisplay;
                if (_displayTopology.TryGetValue(monitor.Device, out DisplaySnapshot? display))
                    placementDisplay = monitor.LastDisplay = display;
                if (placementDisplay == null) continue;

                foreach (DesktopState desktop in monitor.Desktops)
                {
                    foreach (IntPtr h in desktop.Windows.Where(Native.IsWindow))
                    {
                        // Preserve the first pre-borrow placement so a later display
                        // return can restore it exactly instead of round-tripping DPI.
                        if (_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? prior))
                        {
                            if (MatchesWindowIdentity(h, prior.Identity)) continue;
                            _sleepWindowSnapshots.Remove(h);
                        }
                        HiddenWindowRecord identity;
                        if (_hidden.TryGetValue(h, out HiddenWindowRecord? hidden) &&
                            MatchesWindowIdentity(h, hidden))
                            identity = hidden;
                        else if (!TryCaptureWindowIdentity(h, out identity))
                            continue;

                        Rectangle normal = Rectangle.FromLTRB(identity.NormalLeft, identity.NormalTop,
                            identity.NormalRight, identity.NormalBottom);
                        if (normal.Width <= 0 || normal.Height <= 0) continue;
                        _sleepWindowSnapshots[h] = new WindowTransitSnapshot(
                            identity, normal, placementDisplay,
                            unchecked((uint)identity.SavedShowCmd));
                    }
                }
            }
        }
        finally
        {
            EndIdentityCache();
        }
    }

    private bool ReconcileDisplayTopology(Dictionary<string, DisplaySnapshot> current)
    {
        bool changed = false;

        // A physical display can return under a different \\.\DISPLAYx name, and
        // two displays can even swap those names. Rebuild the routing dictionary by
        // stable identity in one pass; applying old->new names one at a time would
        // overwrite one state (and one set of recovery records) during a swap.
        List<MonitorState> previousStates = _monitors.Values.Distinct().ToList();
        var matchedStates = new HashSet<MonitorState>(ReferenceEqualityComparer.Instance);
        var monitorNameChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _monitors.Clear();
        foreach (DisplaySnapshot display in current.Values)
        {
            MonitorState? existing = previousStates.FirstOrDefault(m =>
                string.Equals(m.StableId, display.StableId, StringComparison.OrdinalIgnoreCase));
            if (existing == null) continue;
            matchedStates.Add(existing);
            string oldDevice = existing.Device;
            existing.Device = display.DeviceName;
            existing.LastDisplay = display;
            _monitors[display.DeviceName] = existing;
            if (!string.Equals(oldDevice, display.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                monitorNameChanges[oldDevice] = display.DeviceName;
                changed = true;
                AppLog.Info(nameof(ReconcileDisplayTopology),
                    $"Display '{existing.StableId}' rebound: '{oldDevice}' -> '{display.DeviceName}'.");
            }
        }

        foreach (MonitorState unmatched in previousStates.Where(m => !matchedStates.Contains(m)))
        {
            string key = unmatched.Device;
            if (_monitors.ContainsKey(key))
                key = $"<offline:{unmatched.StableId}>";
            _monitors[key] = unmatched;
        }
        RewriteHiddenMonitorNames(monitorNameChanges);

        foreach (DisplaySnapshot display in current.Values)
        {
            if (_monitors.TryGetValue(display.DeviceName, out MonitorState? existing))
            {
                existing.LastDisplay = display;
                continue;
            }

            var state = new MonitorState
            {
                Device = display.DeviceName,
                StableId = display.StableId,
                LastDisplay = display
            };
            if (!_borrowedDisplays.ContainsKey(display.StableId))
                AddDesktop(state);
            _monitors[display.DeviceName] = state;
            changed = true;
        }

        // Return groups first. This matters when one display comes back while the
        // display temporarily hosting its desktops disappears in the same sample.
        foreach (string homeId in _borrowedDisplays.Keys.ToList())
        {
            DisplaySnapshot? returned = current.Values.FirstOrDefault(d =>
                string.Equals(d.StableId, homeId, StringComparison.OrdinalIgnoreCase));
            if (returned != null)
                changed |= ReturnBorrowedDisplay(homeId, returned);
        }

        var currentStableIds = current.Values.Select(d => d.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorState missing in _monitors.Values
                     .Where(m => !currentStableIds.Contains(m.StableId)).ToList())
        {
            MonitorState? fallback = ChooseFallbackMonitor(current);
            if (fallback == null)
            {
                AppLog.Warning(nameof(ReconcileDisplayTopology),
                    $"Display '{missing.Device}' disappeared but no online fallback exists; retaining its state.");
                continue;
            }
            changed |= BorrowMissingDisplay(missing, fallback, current[fallback.Device]);
        }

        foreach (MonitorState monitor in _monitors.Values)
        {
            DisplaySnapshot? display = current.Values.FirstOrDefault(d =>
                string.Equals(d.StableId, monitor.StableId, StringComparison.OrdinalIgnoreCase));
            if (display != null)
                monitor.LastDisplay = display;
        }

        if (_borrowedDisplays.Count == 0)
        {
            if (_hasSuspendSnapshot && !_borrowedSinceSuspend)
                RestoreOnlineSuspendPlacements(current);
            _sleepWindowSnapshots.Clear();
            _hasSuspendSnapshot = false;
            _borrowedSinceSuspend = false;
        }
        _displayTopology = current;
        if (changed)
        {
            // A real topology event (rebind, new display, borrow or return) was just
            // applied — possibly by a periodic sync that raced ahead of the
            // WM_DISPLAYCHANGE transaction. Start the disturbance window NOW so the
            // very same sync pass re-parks membership windows that applications
            // restored in reaction to the change instead of adopting them.
            _topologyDisturbanceUntil = Environment.TickCount64 + TopologyDisturbanceMs;
        }
        return changed;
    }

    private void RestoreOnlineSuspendPlacements(
        IReadOnlyDictionary<string, DisplaySnapshot> current)
    {
        var displaysByStableId = current.Values.ToDictionary(d => d.StableId,
            StringComparer.OrdinalIgnoreCase);
        foreach (MonitorState monitor in _monitors.Values)
        {
            if (!displaysByStableId.TryGetValue(monitor.StableId,
                    out DisplaySnapshot? destination))
                continue;
            foreach (DesktopState desktop in monitor.Desktops)
            {
                if (!string.Equals(desktop.HomeStableId, desktop.HostStableId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (IntPtr h in desktop.Windows.Where(Native.IsWindow))
                {
                    if (!_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? transit) ||
                        !MatchesWindowIdentity(h, transit.Identity))
                        continue;
                    Rectangle target = MapWindowRect(transit.NormalRect,
                        transit.NormalRectDisplay, destination);
                    if (_hidden.TryGetValue(h, out HiddenWindowRecord? record) &&
                        MatchesWindowIdentity(h, record))
                    {
                        SetHiddenRecord(h, record with
                        {
                            NormalLeft = target.Left,
                            NormalTop = target.Top,
                            NormalRight = target.Right,
                            NormalBottom = target.Bottom,
                            ParkMonitor = destination.DeviceName
                        });
                    }
                    else if (Native.IsWindowVisible(h))
                    {
                        ApplyTopologyPlacement(h, target, transit.ShowCmd, destination);
                    }
                }
            }
        }
        PersistHidden();
    }

    private MonitorState? ChooseFallbackMonitor(IReadOnlyDictionary<string, DisplaySnapshot> current)
    {
        var displaysByStableId = current.Values.ToDictionary(d => d.StableId,
            StringComparer.OrdinalIgnoreCase);
        IEnumerable<MonitorState> online = _monitors.Values
            .Where(m => displaysByStableId.ContainsKey(m.StableId));
        return online.FirstOrDefault(m => displaysByStableId[m.StableId].IsInternal)
            ?? online.FirstOrDefault(m => displaysByStableId[m.StableId].IsPrimary)
            ?? online.FirstOrDefault();
    }

    private bool BorrowMissingDisplay(MonitorState source, MonitorState fallback,
        DisplaySnapshot fallbackDisplay)
    {
        if (ReferenceEquals(source, fallback)) return false;
        if (_hasSuspendSnapshot)
            _borrowedSinceSuspend = true;
        DisplaySnapshot sourceDisplay = source.LastDisplay ?? new DisplaySnapshot(
            source.StableId, source.Device, Rectangle.Empty, Rectangle.Empty, 96, false, false);
        DesktopState? sourceCurrent = source.Desktops.Count > 0
            ? source.Desktops[Math.Clamp(source.Current, 0, source.Desktops.Count - 1)]
            : null;
        DesktopState hostCurrent = fallback.Desktops[fallback.Current];
        List<DesktopState> nativeDesktops = source.Desktops
            .Where(d => string.Equals(d.HomeStableId, source.StableId,
                StringComparison.OrdinalIgnoreCase)).ToList();

        if (nativeDesktops.Count > 0 && !_borrowedDisplays.ContainsKey(source.StableId))
        {
            DesktopState preferred = sourceCurrent != null && nativeDesktops.Contains(sourceCurrent)
                ? sourceCurrent
                : nativeDesktops[0];
            _borrowedDisplays[source.StableId] = new BorrowedDisplayState
            {
                HomeDisplay = sourceDisplay,
                OriginalOrder = nativeDesktops.Select(d => d.RuntimeId).ToList(),
                PreferredCurrent = preferred.RuntimeId,
                HostStableId = fallback.StableId,
                HostPreviousCurrent = hostCurrent.RuntimeId
            };
        }

        for (int i = 0; i < source.Desktops.Count; i++)
        {
            DesktopState desktop = source.Desktops[i];
            desktop.OriginalOrder = i;
            desktop.HostStableId = fallback.StableId;
            if (string.Equals(desktop.HomeStableId, source.StableId,
                    StringComparison.OrdinalIgnoreCase))
                desktop.ReturnWhenOnline = true;
            if (_borrowedDisplays.TryGetValue(desktop.HomeStableId, out BorrowedDisplayState? group))
                group.HostStableId = fallback.StableId;
            fallback.Desktops.Add(desktop);
            _retainedEmptyDesktops.Add(desktop);
            PrepareDesktopForBorrow(desktop, sourceDisplay, fallbackDisplay);
        }

        fallback.Current = fallback.Desktops.IndexOf(hostCurrent);
        string? sourceKey = _monitors.FirstOrDefault(kv => ReferenceEquals(kv.Value, source)).Key;
        if (sourceKey != null)
            _monitors.Remove(sourceKey);
        AppLog.Info(nameof(BorrowMissingDisplay),
            $"Display '{source.Device}' went offline; moved {source.Desktops.Count} desktop(s) " +
            $"as separate units to '{fallback.Device}'.");
        return true;
    }

    private void PrepareDesktopForBorrow(DesktopState desktop, DisplaySnapshot source,
        DisplaySnapshot destination)
    {
        foreach (IntPtr h in desktop.Windows.Where(Native.IsWindow).ToList())
        {
            if (_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? transit) &&
                !MatchesWindowIdentity(h, transit.Identity))
            {
                _sleepWindowSnapshots.Remove(h);
                desktop.Windows.Remove(h);
                RemoveHiddenRecord(h);
                continue;
            }
            if (transit == null)
            {
                if (!TryCaptureWindowIdentity(h, out HiddenWindowRecord identity)) continue;
                Rectangle captured = Rectangle.FromLTRB(identity.NormalLeft, identity.NormalTop,
                    identity.NormalRight, identity.NormalBottom);
                DisplaySnapshot capturedOn = string.Equals(identity.ParkMonitor,
                        destination.DeviceName, StringComparison.OrdinalIgnoreCase)
                    ? destination
                    : source;
                transit = new WindowTransitSnapshot(identity, captured,
                    capturedOn, unchecked((uint)identity.SavedShowCmd));
            }

            Rectangle currentRect = GetRecordedNormalRect(h);
            Rectangle sourceRect;
            DisplaySnapshot mappingSource;
            if (transit.BorrowedBaseline is Rectangle priorBaseline &&
                transit.BorrowedDisplay != null &&
                RectApproximatelyEquals(currentRect, priorBaseline))
            {
                sourceRect = priorBaseline;
                mappingSource = transit.BorrowedDisplay;
            }
            else if (transit.BorrowedBaseline == null)
            {
                sourceRect = transit.NormalRect;
                mappingSource = transit.NormalRectDisplay;
            }
            else
            {
                sourceRect = currentRect;
                mappingSource = source;
            }
            Rectangle mapped = MapWindowRect(sourceRect, mappingSource, destination);
            _sleepWindowSnapshots[h] = transit with
            {
                BorrowedBaseline = mapped,
                BorrowedDisplay = destination
            };
            if (Native.IsWindowVisible(h) && !_hidden.ContainsKey(h))
                ApplyTopologyPlacement(h, mapped, transit.ShowCmd, destination);
        }

        // Incoming desktops never merge with the fallback's current desktop.
        // Failures are logged by the existing bounded transaction; membership is
        // still preserved and the next sync retries instead of adopting the HWND.
        HideOrParkManagedWindows(desktop.Windows);

        foreach (IntPtr h in desktop.Windows)
        {
            if (!_hidden.TryGetValue(h, out HiddenWindowRecord? record) ||
                !_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? transit) ||
                transit.BorrowedBaseline is not Rectangle mapped ||
                !MatchesWindowIdentity(h, record))
                continue;
            SetHiddenRecord(h, record with
            {
                NormalLeft = mapped.Left,
                NormalTop = mapped.Top,
                NormalRight = mapped.Right,
                NormalBottom = mapped.Bottom,
                ParkMonitor = destination.DeviceName
            });
        }
        PersistHidden();
    }

    private bool ReturnBorrowedDisplay(string homeId, DisplaySnapshot returnedDisplay)
    {
        if (!_borrowedDisplays.TryGetValue(homeId, out BorrowedDisplayState? group)) return false;
        var located = _monitors.Values
            .SelectMany(m => m.Desktops.Select(d => (Monitor: m, Desktop: d)))
            .Where(x => x.Desktop.ReturnWhenOnline &&
                        string.Equals(x.Desktop.HomeStableId, homeId,
                            StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!_monitors.TryGetValue(returnedDisplay.DeviceName, out MonitorState? home))
        {
            home = new MonitorState
            {
                Device = returnedDisplay.DeviceName,
                StableId = homeId,
                LastDisplay = returnedDisplay
            };
            _monitors[returnedDisplay.DeviceName] = home;
        }

        foreach (var hostGroup in located.GroupBy(x => x.Monitor))
        {
            MonitorState host = hostGroup.Key;
            DesktopState? previousCurrent = host.Desktops.Count > 0 ? host.Desktops[host.Current] : null;
            foreach (var item in hostGroup)
                HideOrParkManagedWindows(item.Desktop.Windows);
            foreach (var item in hostGroup)
                host.Desktops.Remove(item.Desktop);

            if (host.Desktops.Count == 0 && !ReferenceEquals(host, home))
                AddDesktop(host);
            if (host.Desktops.Count > 0)
            {
                DesktopState? preferredHost = host.Desktops.FirstOrDefault(d =>
                    d.RuntimeId == group.HostPreviousCurrent);
                host.Current = preferredHost != null
                    ? host.Desktops.IndexOf(preferredHost)
                    : Math.Clamp(previousCurrent != null ? host.Desktops.IndexOf(previousCurrent) : 0,
                        0, host.Desktops.Count - 1);
                RestoreManagedWindows(host.Desktops[host.Current].Windows);
            }
        }

        List<DesktopState> returned = located.Select(x => x.Desktop)
            .OrderBy(d => group.OriginalOrder.IndexOf(d.RuntimeId) is int index && index >= 0
                ? index
                : int.MaxValue)
            .ToList();
        foreach (DesktopState desktop in returned)
        {
            desktop.HostStableId = homeId;
            desktop.ReturnWhenOnline = false;
            home.Desktops.Add(desktop);
        }

        // Stage 1 (plan): compute the whole return group's placement plan against one
        // topology snapshot, before any journal or physical placement is modified.
        List<TopologyPlacementPlan> plans = returned
            .SelectMany(d => BuildDesktopReturnPlans(d, group, returnedDisplay))
            .ToList();

        // Stage 2 (journal-first): persist every target record as one batch before a
        // single HWND is touched. A crash afterwards leaves a journal that points at
        // the final home display instead of a stale borrowed position.
        bool journalPersisted = plans.Count == 0 || PersistReturnPlacementSnapshot(plans);
        if (!journalPersisted)
        {
            // Keep the borrowed state so the next topology sync retries the return;
            // the physical migration of this batch must not start. HostStableId stays
            // with the previous host so SyncCore keeps treating re-shown windows of
            // these desktops as incoming-borrowed (re-park, never adopt).
            foreach (DesktopState desktop in returned)
            {
                desktop.ReturnWhenOnline = true;
                desktop.HostStableId = group.HostStableId;
            }
            AppLog.Warning(nameof(ReturnBorrowedDisplay),
                $"Could not persist the return plan for '{returnedDisplay.DeviceName}'; " +
                "the physical migration was not started and the return will be retried.");
        }
        else
        {
            // Commit the in-memory journal and mark every placement as pending
            // before the first Win32 call, then materialize each plan.
            foreach (TopologyPlacementPlan plan in plans)
            {
                if (plan.TargetRecord is not { } target) continue;
                SetHiddenRecord(plan.Handle, target);
                _pendingTopologyPlacements[plan.Handle] = new PendingTopologyPlacement(
                    plan.Handle, target, plan.TargetDevice, plan.TargetRect, target.Parked, 0, 0);
                AppLog.Info(nameof(ReturnBorrowedDisplay),
                    $"Topology placement planned: HWND={plan.Handle} -> '{plan.TargetDevice}' " +
                    $"target=({plan.TargetRect.Left},{plan.TargetRect.Top},{plan.TargetRect.Right},{plan.TargetRect.Bottom}) " +
                    $"parked={target.Parked}.");
            }
            foreach (TopologyPlacementPlan plan in plans)
                ExecuteTopologyPlacementPlan(plan);
        }

        if (home.Desktops.Count == 0)
            AddDesktop(home);
        DesktopState? preferred = home.Desktops.FirstOrDefault(d => d.RuntimeId == group.PreferredCurrent);
        home.Current = preferred != null ? home.Desktops.IndexOf(preferred) : 0;
        RestoreManagedWindows(home.Desktops[home.Current].Windows);
        if (journalPersisted)
            _borrowedDisplays.Remove(homeId);
        PersistHidden();
        AppLog.Info(nameof(ReturnBorrowedDisplay),
            $"Display '{returnedDisplay.DeviceName}' returned; restored {returned.Count} desktop(s) as a group.");
        return true;
    }

    /// <summary>
    /// Compute the per-window migration plan of one returning desktop. Pure with
    /// respect to journal and placement: stale-HWND cleanup aside, no record is
    /// written and no window is moved; callers persist and execute the batch.
    /// </summary>
    private List<TopologyPlacementPlan> BuildDesktopReturnPlans(DesktopState desktop,
        BorrowedDisplayState group, DisplaySnapshot destination)
    {
        var plans = new List<TopologyPlacementPlan>();
        DisplaySnapshot source = _displayTopology.Values.FirstOrDefault(d =>
                string.Equals(d.StableId, group.HostStableId, StringComparison.OrdinalIgnoreCase))
            ?? group.HomeDisplay;
        foreach (IntPtr h in desktop.Windows.Where(Native.IsWindow).ToList())
        {
            if (_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? knownTransit) &&
                !MatchesWindowIdentity(h, knownTransit.Identity))
            {
                _sleepWindowSnapshots.Remove(h);
                desktop.Windows.Remove(h);
                RemoveHiddenRecord(h);
                continue;
            }
            Rectangle currentRect = GetRecordedNormalRect(h);
            Rectangle targetRect;
            if (_sleepWindowSnapshots.TryGetValue(h, out WindowTransitSnapshot? transit) &&
                transit.BorrowedBaseline is Rectangle baseline &&
                RectApproximatelyEquals(currentRect, baseline))
            {
                targetRect = MapWindowRect(transit.NormalRect,
                    transit.NormalRectDisplay, destination);
            }
            else
            {
                DisplaySnapshot mappingSource = transit?.BorrowedDisplay ?? source;
                targetRect = MapWindowRect(currentRect, mappingSource, destination);
            }

            if (_hidden.TryGetValue(h, out HiddenWindowRecord? record) && MatchesWindowIdentity(h, record))
            {
                plans.Add(new TopologyPlacementPlan(
                    h, record,
                    record with
                    {
                        NormalLeft = targetRect.Left,
                        NormalTop = targetRect.Top,
                        NormalRight = targetRect.Right,
                        NormalBottom = targetRect.Bottom,
                        ParkMonitor = destination.DeviceName
                    },
                    targetRect, destination.DeviceName, source.DeviceName,
                    unchecked((uint)record.SavedShowCmd), destination, desktop));
            }
            else if (Native.IsWindowVisible(h))
            {
                // No recovery record (e.g. an unmanageable window): only the physical
                // placement can be migrated; there is no journal to persist for it.
                uint showCmd = _sleepWindowSnapshots.TryGetValue(h, out transit)
                    ? transit.ShowCmd
                    : 0;
                plans.Add(new TopologyPlacementPlan(
                    h, null, null, targetRect, destination.DeviceName, source.DeviceName,
                    showCmd, destination, desktop));
            }
        }
        return plans;
    }

    /// <summary>Persist the target records of a return batch as one snapshot (§6: journal-first).</summary>
    private bool PersistReturnPlacementSnapshot(List<TopologyPlacementPlan> plans)
    {
        var planned = _hidden.Values.ToDictionary(r => r.Handle);
        foreach (TopologyPlacementPlan plan in plans)
            if (plan.TargetRecord is { } target)
                planned[target.Handle] = target;
        return PersistHiddenSnapshot(planned.Values);
    }

    /// <summary>
    /// Execute one planned placement. Record-bearing plans were already marked
    /// pending; success clears the marker, a destroyed/reused HWND cleans up all of
    /// its state, and a refusal keeps membership + journal + pending for a later
    /// retry (§13: never silently damage the logical membership).
    /// </summary>
    private void ExecuteTopologyPlacementPlan(TopologyPlacementPlan plan)
    {
        IntPtr h = plan.Handle;
        if (plan.TargetRecord == null)
        {
            bool landed = Native.IsWindow(h) && SafeToModifyWindow(h) &&
                ApplyTopologyPlacement(h, plan.TargetRect, plan.SavedShowCmd, plan.Destination) &&
                (Native.IsIconic(h)
                    ? IsNormalPlacementAssignedToDisplay(h, plan.TargetDevice)
                    : Native.GetWindowRect(h, out Native.RECT wr) &&
                      IsRectAssignedToDisplay(FromRECT(wr), plan.TargetDevice));
            if (landed)
            {
                AppLog.Info(nameof(ExecuteTopologyPlacementPlan),
                    $"Topology placement applied: HWND={h} -> '{plan.TargetDevice}'.");
            }
            else
            {
                AppLog.Warning(nameof(ExecuteTopologyPlacementPlan),
                    $"Could not move unrecorded HWND={h} to '{plan.TargetDevice}' while returning its desktop.");
            }
            return;
        }

        switch (TryMaterializeTopologyPlacement(plan))
        {
            case TopologyPlacementOutcome.Applied:
                _pendingTopologyPlacements.Remove(h);
                AppLog.Info(nameof(ExecuteTopologyPlacementPlan),
                    $"Topology placement applied: HWND={h} -> '{plan.TargetDevice}'.");
                break;
            case TopologyPlacementOutcome.Destroyed:
            case TopologyPlacementOutcome.IdentityMismatch:
                // The HWND died or was reused mid-return: drop every trace of the old
                // window; a replacement HWND goes through normal discovery.
                plan.Desktop?.Windows.Remove(h);
                RemoveHiddenRecord(h);
                AppLog.Info(nameof(ExecuteTopologyPlacementPlan),
                    $"Topology placement of HWND={h} dropped: the window was destroyed or its identity changed.");
                break;
            default:
                LogPendingPlacementFailure(h, plan.TargetRecord);
                break;
        }
    }

    // TopologyPlacementOutcome lives at namespace level (see TopologyDecisions.cs).

    /// <summary>
    /// Materialize a journaled topology placement (§7): move the window's real
    /// placement (or its minimized restore rectangle) to the target display while
    /// preserving the hidden/parked representation, then verify the actual result —
    /// Win32 boolean returns alone are not trusted.
    /// </summary>
    private TopologyPlacementOutcome TryMaterializeTopologyPlacement(TopologyPlacementPlan plan)
    {
        IntPtr h = plan.Handle;
        HiddenWindowRecord target = plan.TargetRecord!;
        if (!Native.IsWindow(h)) return TopologyPlacementOutcome.Destroyed;
        if (!MatchesWindowIdentity(h, target)) return TopologyPlacementOutcome.IdentityMismatch;
        if (plan.TargetRect.Width <= 0 || plan.TargetRect.Height <= 0 ||
            !SafeToModifyWindow(h))
            return TopologyPlacementOutcome.Failed;

        if (target.Parked)
        {
            // Parked windows keep their minimized/off-screen representation; only the
            // restore rectangle and the parking anchor migrate to the target display.
            string sourceDevice = Native.GetMonitorDeviceOfWindow(h) ?? plan.SourceDevice;
            if (!RepositionParkedWindowToTarget(h, target, plan.TargetRect, sourceDevice, plan.TargetDevice) ||
                !MatchesWindowIdentity(h, target) || !IsManagedWindowParked(h, target))
                return ClassifyPlacementFailure(h, target);
            return TopologyPlacementOutcome.Applied;
        }

        // Hidden-mode window: preserve visible/iconic/zoomed semantics across the move.
        bool wasVisible = Native.IsWindowVisible(h);
        bool wasIconic = Native.IsIconic(h);
        bool wasZoomed = Native.IsZoomed(h);
        bool savedMaximized = target.SavedShowCmd == Native.SW_SHOWMAXIMIZED;
        var placement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        bool hasPlacement = Native.GetWindowPlacement(h, ref placement);

        bool applied;
        if (wasIconic)
        {
            // Move the restore rectangle through the verified show -> place ->
            // re-minimize chain; RestoreIconicPlacement briefly shows the window,
            // so a hidden window must be re-hidden afterwards (§7.3).
            applied = RestoreIconicPlacement(h, plan.TargetRect);
            if (applied && !wasVisible)
            {
                Native.ShowWindow(h, Native.SW_HIDE);
                applied = !Native.IsWindowVisible(h) && Native.IsIconic(h);
            }
        }
        else if (wasZoomed || savedMaximized)
        {
            // Live rectangle onto the target working area, normal rectangle onto the
            // pre-computed target; keep the window hidden when it was hidden.
            Rectangle area = plan.Destination.WorkingArea;
            applied = Native.SetWindowPos(h, IntPtr.Zero, area.X, area.Y, area.Width, area.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (applied && hasPlacement)
            {
                placement.rcNormalPosition = ToRECT(plan.TargetRect);
                placement.showCmd = Native.SW_SHOWMAXIMIZED;
                applied = Native.SetWindowPlacement(h, ref placement);
            }
            if (applied && !wasVisible)
            {
                Native.ShowWindow(h, Native.SW_HIDE);
                applied = !Native.IsWindowVisible(h);
            }
        }
        else
        {
            applied = Native.SetWindowPos(h, IntPtr.Zero, plan.TargetRect.X, plan.TargetRect.Y,
                plan.TargetRect.Width, plan.TargetRect.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        if (!applied) return ClassifyPlacementFailure(h, target);

        // §7.4: verify what actually happened, not what the API promised.
        bool verified = MatchesWindowIdentity(h, target) &&
                        Native.IsWindowVisible(h) == wasVisible &&
                        Native.IsIconic(h) == wasIconic &&
                        Native.IsZoomed(h) == wasZoomed;
        if (verified)
        {
            if (wasIconic)
                verified = TryGetEffectiveWindowRect(h, out Rectangle normal) &&
                           IsRectAssignedToDisplay(normal, plan.TargetDevice);
            else if (wasZoomed || savedMaximized)
                verified = Native.GetWindowRect(h, out Native.RECT live) &&
                           IsRectAssignedToDisplay(FromRECT(live), plan.TargetDevice) &&
                           IsNormalPlacementAssignedToDisplay(h, plan.TargetDevice);
            else
                verified = Native.GetWindowRect(h, out Native.RECT wr) &&
                           IsRectAssignedToDisplay(FromRECT(wr), plan.TargetDevice);
        }
        return verified ? TopologyPlacementOutcome.Applied : ClassifyPlacementFailure(h, target);
    }

    /// <summary>A failed placement is only retriable while the same window still lives
    /// behind the HWND; a destroyed or reused handle is a terminal outcome.</summary>
    private TopologyPlacementOutcome ClassifyPlacementFailure(IntPtr h, HiddenWindowRecord target) =>
        TopologyDecisions.ClassifyPlacementFailure(Native.IsWindow(h), MatchesWindowIdentity(h, target));

    private static bool IsRectAssignedToDisplay(Rectangle rect, string device) =>
        rect.Width > 0 && rect.Height > 0 &&
        Screen.FromRectangle(rect).DeviceName == device;

    /// <summary>Record a failed physical attempt (throttles the next retry) and emit a
    /// rate-limited failure log for a pending topology placement (§14).</summary>
    private void LogPendingPlacementFailure(IntPtr h, HiddenWindowRecord target)
    {
        if (!_pendingTopologyPlacements.TryGetValue(h, out PendingTopologyPlacement? pending)) return;
        long now = Environment.TickCount64;
        _pendingTopologyPlacements[h] = pending with
        {
            RetryNotBeforeTick = Math.Max(pending.RetryNotBeforeTick, now + PendingPlacementRetryMs),
            MuteLogUntil = Math.Max(pending.MuteLogUntil, now + PendingPlacementLogIntervalMs)
        };
        if (now < pending.MuteLogUntil) return;
        AppLog.Warning(nameof(TryMaterializeTopologyPlacement),
            $"Topology placement verification failed for HWND={h}; kept the membership, the " +
            $"journal and the pending placement targeting '{pending.TargetDevice}' " +
            $"rect=({pending.TargetRect.Left},{pending.TargetRect.Top},{pending.TargetRect.Right},{pending.TargetRect.Bottom}); " +
            $"retrying later. {DescribeWindow(h)}");
    }

    private Rectangle GetRecordedNormalRect(IntPtr h)
    {
        if (_hidden.TryGetValue(h, out HiddenWindowRecord? record))
            return Rectangle.FromLTRB(record.NormalLeft, record.NormalTop,
                record.NormalRight, record.NormalBottom);
        var placement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        if (Native.GetWindowPlacement(h, ref placement))
            return FromRECT(placement.rcNormalPosition);
        return Native.GetWindowRect(h, out Native.RECT rect) ? FromRECT(rect) : Rectangle.Empty;
    }

    private bool ApplyTopologyPlacement(IntPtr h, Rectangle target, uint savedShowCmd,
        DisplaySnapshot destination)
    {
        if (target.Width <= 0 || target.Height <= 0 || !SafeToModifyWindow(h)) return false;
        if (Native.IsIconic(h))
            return RestoreIconicPlacement(h, target);
        if (Native.IsZoomed(h) || savedShowCmd == Native.SW_SHOWMAXIMIZED)
        {
            Rectangle area = destination.WorkingArea;
            bool moved = Native.SetWindowPos(h, IntPtr.Zero,
                area.X, area.Y, area.Width, area.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            var placement = new Native.WINDOWPLACEMENT
            {
                length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
            };
            if (!Native.GetWindowPlacement(h, ref placement)) return moved;
            placement.rcNormalPosition = ToRECT(target);
            placement.showCmd = Native.SW_SHOWMAXIMIZED;
            return moved && Native.SetWindowPlacement(h, ref placement);
        }
        return Native.SetWindowPos(h, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void RewriteHiddenMonitorNames(IReadOnlyDictionary<string, string> changes)
    {
        if (changes.Count == 0) return;
        // Rename pending placement targets first: SetHiddenRecord withdraws a pending
        // whose target device no longer matches the rewritten record.
        foreach ((IntPtr h, PendingTopologyPlacement pending) in _pendingTopologyPlacements.ToList())
            if (pending.TargetRecord.ParkMonitor != null &&
                changes.TryGetValue(pending.TargetRecord.ParkMonitor, out string? pendingDevice))
                _pendingTopologyPlacements[h] = pending with
                {
                    TargetRecord = pending.TargetRecord with { ParkMonitor = pendingDevice },
                    TargetDevice = pendingDevice
                };
        foreach ((IntPtr h, HiddenWindowRecord record) in _hidden.ToList())
            if (record.ParkMonitor != null &&
                changes.TryGetValue(record.ParkMonitor, out string? newDevice))
                SetHiddenRecord(h, record with { ParkMonitor = newDevice });
        PersistHidden();
    }

    private bool SyncCore()
    {
        if (_topologyTransitionInProgress) return false;

        bool changed = false;
        foreach (var h in _unmanageable.Keys.Where(h => !Native.IsWindow(h)).ToList())
            _unmanageable.Remove(h);
        foreach ((IntPtr h, WindowTransitSnapshot snapshot) in _sleepWindowSnapshots.ToList())
            if (!MatchesWindowIdentity(h, snapshot.Identity))
                _sleepWindowSnapshots.Remove(h);
        changed |= ReconcileDisplayTopology(DisplayTopology.Capture());

        foreach (var st in _monitors.Values)
            foreach (var desktop in st.Desktops)
                if (desktop.Windows.RemoveWhere(h =>
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

            // Shared taskbar mode: a parked window keeps its desktop membership;
            // its taskbar button is the entry point for jumping back to that desktop.
            if (_hidden.TryGetValue(h, out var parkedRecord) && parkedRecord.Parked &&
                MatchesWindowIdentity(h, parkedRecord) && IsManagedWindowParked(h, parkedRecord))
                continue;

            string? dev = Native.GetMonitorDeviceOfWindow(h);
            if (dev == null || !_monitors.TryGetValue(dev, out var st)) continue;

            // A topology placement that has not landed yet must not be interpreted as
            // an app-initiated cross-display move: the journal and the membership are
            // authoritative until the physical placement is verified on the target
            // display. Suppress adoption; the retry pass below re-places the window.
            if (_pendingTopologyPlacements.TryGetValue(h, out PendingTopologyPlacement? pending))
            {
                if (TopologyDecisions.DecideSyncPending(
                        pendingExists: true, identityMatches: MatchesWindowIdentity(h, pending.TargetRecord)) ==
                    TopologyDecisions.SyncPending.SuppressAdoption)
                {
                    // Rate-limited: a stuck window is re-suppressed on every sync pass.
                    long now = Environment.TickCount64;
                    if (now >= pending.MuteLogUntil)
                    {
                        _pendingTopologyPlacements[h] = pending with
                            { MuteLogUntil = now + PendingPlacementLogIntervalMs };
                        AppLog.Info(nameof(Sync),
                            $"Sync adoption suppressed by pending placement: HWND={h} " +
                            $"waits for '{pending.TargetDevice}'.");
                    }
                    continue;
                }
                // The HWND was reused: the pending belongs to the old window only.
                _pendingTopologyPlacements.Remove(h);
                AppLog.Info(nameof(Sync),
                    $"Pending topology placement of HWND={h} dropped due to identity mismatch.");
            }

            // A display-topology transaction deliberately keeps an incoming desktop
            // separate from the fallback's current desktop. If Windows made one of
            // its windows visible while moving it off the vanished display, retry the
            // presentation change but never adopt it into the fallback desktop.
            DesktopState? logicalOwner = _monitors.Values
                .SelectMany(m => m.Desktops)
                .FirstOrDefault(d => d.Windows.Contains(h));
            bool ownerIsIncomingBorrowed = logicalOwner is { ReturnWhenOnline: true } &&
                string.Equals(logicalOwner.HostStableId, st.StableId,
                    StringComparison.OrdinalIgnoreCase);
            // Right after any topology change applications restore parked windows in
            // reaction to WM_DISPLAYCHANGE (no user intent behind it). Re-park those
            // instead of adopting them, or every non-current desktop of the surviving
            // display drains into the current one.
            bool topologyDisturbed = Environment.TickCount64 < _topologyDisturbanceUntil;
            if (logicalOwner != null &&
                !ReferenceEquals(logicalOwner, st.Desktops[st.Current]) &&
                (ownerIsIncomingBorrowed || topologyDisturbed))
            {
                HideOrParkManagedWindows(new[] { h });
                continue;
            }

            // An application or the user may have shown one of our hidden windows.
            // Visible windows belong to the current desktop and must not remain in
            // the crash-recovery journal, even if already present in that set.
            if (_hidden.TryGetValue(h, out var journalRecord) && journalRecord.Parked)
                AppLog.Warning(nameof(Sync),
                    $"Parked HWND={h} is back on screen; adopting it into the current desktop of '{dev}'.");
            changed |= RemoveHiddenRecord(h);

            if (!st.Desktops[st.Current].Windows.Contains(h))
            {
                // Başka bir set'te kayıtlıysa oradan çıkar (monitör değiştirmiş
                // veya gizliyken uygulama tarafından tekrar gösterilmiş olabilir)
                bool migrated = false;
                foreach (var other in _monitors.Values)
                    foreach (var desktop in other.Desktops)
                        if (desktop.Windows.Remove(h))
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

        // Pending placements were skipped by the adoption pass above; give each of
        // them one bounded retry here (windows that are still hidden/parked included).
        RetryPendingTopologyPlacements();

        PersistHidden();
        return changed;
    }

    /// <summary>The desktop a window currently belongs to, if any.</summary>
    private (MonitorState Monitor, DesktopState Desktop, int Index)? FindWindowDesktop(IntPtr h)
    {
        foreach (var st in _monitors.Values)
            for (int i = 0; i < st.Desktops.Count; i++)
                if (st.Desktops[i].Windows.Contains(h))
                    return (st, st.Desktops[i], i);
        return null;
    }

    private void RemoveWindowMembership(IntPtr h)
    {
        foreach (var st in _monitors.Values)
            foreach (var desktop in st.Desktops)
                desktop.Windows.Remove(h);
    }

    /// <summary>
    /// One bounded retry round for every pending topology placement (§8.1/§11): a
    /// window whose placement has not landed keeps its membership and journal; this
    /// pass re-attempts the physical migration and resolves visibility according to
    /// the logical owner. Pending state never expires with the topology disturbance
    /// window — it ends only with a verified placement or a dead/reused HWND.
    /// </summary>
    private void RetryPendingTopologyPlacements()
    {
        if (_pendingTopologyPlacements.Count == 0) return;
        long now = Environment.TickCount64;
        foreach ((IntPtr h, PendingTopologyPlacement pending) in _pendingTopologyPlacements.ToList())
        {
            bool alive = Native.IsWindow(h);
            switch (TopologyDecisions.DecidePendingRetry(
                alive, MatchesWindowIdentity(h, pending.TargetRecord), now, pending.RetryNotBeforeTick))
            {
                case TopologyDecisions.PendingRetry.DropStale:
                    _pendingTopologyPlacements.Remove(h);
                    RemoveWindowMembership(h);
                    RemoveHiddenRecord(h);
                    if (alive)
                        AppLog.Info(nameof(RetryPendingTopologyPlacements),
                            $"Pending topology placement of HWND={h} dropped due to identity mismatch.");
                    break;
                case TopologyDecisions.PendingRetry.Retry:
                    RetryPendingTopologyPlacement(h, pending);
                    break;
            }
        }
    }

    private void RetryPendingTopologyPlacement(IntPtr h, PendingTopologyPlacement pending)
    {
        if (!_displayTopology.TryGetValue(pending.TargetDevice, out DisplaySnapshot? destination))
        {
            // The target display is offline again: the borrow/return machinery owns
            // this window now (it rewrites the journal and withdraws the pending).
            return;
        }
        var owner = FindWindowDesktop(h);
        var plan = new TopologyPlacementPlan(
            h, pending.TargetRecord, pending.TargetRecord, pending.TargetRect,
            pending.TargetDevice, Native.GetMonitorDeviceOfWindow(h) ?? pending.TargetDevice,
            unchecked((uint)pending.TargetRecord.SavedShowCmd), destination, owner?.Desktop);

        TopologyPlacementOutcome outcome = TryMaterializeTopologyPlacement(plan);
        if (outcome == TopologyPlacementOutcome.Failed)
        {
            LogPendingPlacementFailure(h, pending.TargetRecord);
            return;
        }
        if (outcome != TopologyPlacementOutcome.Applied)
        {
            _pendingTopologyPlacements.Remove(h);
            RemoveWindowMembership(h);
            RemoveHiddenRecord(h);
            AppLog.Info(nameof(RetryPendingTopologyPlacement),
                $"Pending topology placement of HWND={h} dropped: the window was destroyed or its identity changed.");
            return;
        }

        _pendingTopologyPlacements.Remove(h);
        AppLog.Info(nameof(RetryPendingTopologyPlacement),
            $"Pending placement retry succeeded: HWND={h} landed on '{pending.TargetDevice}'.");
        bool ownerIsCurrent = owner != null && owner.Value.Index == owner.Value.Monitor.Current;
        bool parkedRepresentation = _hidden.TryGetValue(h, out var parked) && parked.Parked &&
                                    IsManagedWindowParked(h, parked);
        switch (TopologyDecisions.DecidePendingLandedVisibility(
                   owner != null, ownerIsCurrent, Native.IsWindowVisible(h), parkedRepresentation))
        {
            case TopologyDecisions.PendingLandedVisibility.ShowWindow:
                // The owner desktop is being presented: the window must be visible on it.
                ShowOrUnparkManagedWindow(h);
                break;
            case TopologyDecisions.PendingLandedVisibility.RehideOrRepark:
                // Re-hide or re-park a re-shown window of a non-current desktop now
                // that it sits on the correct display.
                HideOrParkManagedWindows(new[] { h });
                break;
        }
    }

    /// <summary>Genel bakış arayüzü için tam düzen (monitör başına yerel numaralarla).
    /// Ordinals follow the OrderedMonitors numbering order (the internal display is
    /// always 1); the presentation order is flipped so the overview lists external
    /// monitors first and pins the internal display's row to the bottom.</summary>
    public IReadOnlyList<MonitorEntry> GetLayout()
    {
        if (!_topologyTransitionInProgress)
            Sync();
        var ordered = OrderedMonitors();
        var ordinalByState = new Dictionary<MonitorState, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < ordered.Count; i++)
            ordinalByState[ordered[i]] = i + 1;
        var result = new List<MonitorEntry>();
        foreach (var st in ordered.Where(m => !IsInternalMonitor(m))
                     .Concat(ordered.Where(IsInternalMonitor)))
        {
            var desktops = new List<DesktopEntry>();
            for (int i = 0; i < st.Desktops.Count; i++)
            {
                var windows = st.Desktops[i].Windows
                    .Where(Native.IsWindow)
                    .Select(h =>
                    {
                        string t = Native.GetWindowTitle(h);
                        return new WindowEntry(h, t.Length > 0 ? t : Native.GetWindowClass(h));
                    })
                    .ToList();
                desktops.Add(new DesktopEntry(i, i == st.Current, windows));
            }
            result.Add(new MonitorEntry(st.Device, ordinalByState[st],
                OwnedDesktopCount(st) < MaxDesktopsPerMonitor, desktops));
        }
        return result;
    }

    // ---------- geçişler ----------

    /// <summary>Fare imlecinin bulunduğu monitörde bir sonraki/önceki masaüstüne geçer.
    /// Son masaüstünde ileri geçiş, masaüstünde pencere varsa yeni masaüstü oluşturur.</summary>
    public void SwitchRelative(int delta)
    {
        if (_topologyTransitionInProgress) return;
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
            bool hasEffectiveWindows = st.Desktops[st.Current].Windows
                .Any(h => Native.IsWindow(h) && !IsKnownUnmanageable(h) && !ShouldSkipForIntegrity(h, out _));
            bool canGrow = delta > 0
                && OwnedDesktopCount(st) < MaxDesktopsPerMonitor
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
        }
    }

    /// <summary>Global masaüstü numarasına geçer (hangi monitörde olduğunu kendisi bulur).</summary>
    public void SwitchToGlobal(int number)
    {
        if (_topologyTransitionInProgress) return;
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
        if (_topologyTransitionInProgress) return;
        Sync();
        if (_monitors.TryGetValue(device, out var st) && localIndex >= 0 && localIndex < st.Desktops.Count)
            SwitchToCore(st, localIndex);
    }

    /// <summary>Verilen monitörde yeni boş masaüstü oluşturur ve ona geçer.</summary>
    public void CreateDesktopAndSwitch(string device)
    {
        if (_topologyTransitionInProgress) return;
        Sync();
        if (!_monitors.TryGetValue(device, out var st)) return;
        if (OwnedDesktopCount(st) >= MaxDesktopsPerMonitor) return;
        AddDesktop(st);
        if (!SwitchToCore(st, st.Desktops.Count - 1))
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
        }
    }

    /// <summary>Yeni boş masaüstü oluşturur ancak aktif masaüstünü değiştirmez.</summary>
    public bool CreateDesktop(string device)
    {
        if (_topologyTransitionInProgress) return false;
        Sync();
        if (!_monitors.TryGetValue(device, out var st)) return false;
        if (OwnedDesktopCount(st) >= MaxDesktopsPerMonitor) return false;
        AddDesktop(st);
        _retainedEmptyDesktops.Add(st.Desktops[^1]);
        return true;
    }

    /// <summary>Yeni bir masaüstü oluşturur ve sürüklenen pencereyi ona taşır;
    /// genel bakışın açık kalabilmesi için yeni masaüstüne geçiş yapmaz.</summary>
    public bool CreateDesktopAndMoveWindow(IntPtr h, string dstDevice)
    {
        if (_topologyTransitionInProgress) return false;
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
        if (OwnedDesktopCount(dst) >= MaxDesktopsPerMonitor) return false;
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
            foreach (var desktop in st.Desktops)
                desktop.Windows.Remove(h);

        AddWindow(dst.Desktops[target], h);
        dst.Desktops[target].LastActive = h;

        foreach (var st in _monitors.Values) PruneTrailingEmpty(st);
        PersistHidden();
        return true;
    }

    /// <summary>Bir masaüstünü kapatır ve pencerelerini önceki masaüstüne taşır.
    /// İlk masaüstü kapatılırsa pencereler ikinci masaüstüne gider. Her monitörde
    /// en az bir masaüstü kalır.</summary>
    public bool DeleteDesktop(string device, int localIndex)
    {
        if (_topologyTransitionInProgress) return false;
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

        IntPtr movedFocus = removed.LastActive;
        if (!Native.IsWindow(movedFocus) || !removed.Windows.Contains(movedFocus))
            movedFocus = removed.Windows.FirstOrDefault(Native.IsWindow);

        foreach (var h in removed.Windows)
            if (Native.IsWindow(h))
                AddWindow(target, h);

        _retainedEmptyDesktops.Remove(removed);
        st.Desktops.RemoveAt(localIndex);

        int targetIndex = st.Desktops.IndexOf(target);
        st.Current = removedWasCurrent ? targetIndex : st.Desktops.IndexOf(current);

        if (targetIndex >= 0 && movedFocus != IntPtr.Zero &&
            (removedWasCurrent || !Native.IsWindow(st.Desktops[targetIndex].LastActive)))
            st.Desktops[targetIndex].LastActive = movedFocus;

        bool targetIsCurrent = st.Current == targetIndex;
        foreach (var h in target.Windows.ToList())
        {
            if (!Native.IsWindow(h))
            {
                target.Windows.Remove(h);
                RemoveHiddenRecord(h);
                continue;
            }

            if (targetIsCurrent)
            {
                ShowOrUnparkManagedWindow(h);
            }
        }

        if (!targetIsCurrent)
            HideOrParkManagedWindows(target.Windows);

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
            bool accessible = RestoreManagedWindows(st.Desktops[target].Windows);
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
        if (st.Desktops[st.Current].Windows.Contains(fg))
            st.Desktops[st.Current].LastActive = fg;

        int source = st.Current;
        var sourceWindows = st.Desktops[source].Windows.ToList();
        var targetWindows = st.Desktops[target].Windows.ToList();
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
            if (!Native.IsWindow(h)) { st.Desktops[target].Windows.Remove(h); continue; }
            if (!Native.IsWindowVisible(h) && !_hidden.ContainsKey(h))
            {
                // The application hid this window itself after the last sync.
                // It is not ours to show; discard the stale membership and let a
                // later Sync adopt it wherever it becomes visible again.
                st.Desktops[target].Windows.Remove(h);
                continue;
            }
            switch (TryRestoreTargetWindow(h, targetInitiallyManaged, targetSuccessfullyRestored))
            {
                case TargetRestoreOutcome.Pruned:
                    st.Desktops[target].Windows.Remove(h);
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
                        st.Desktops[target].Windows.Remove(h);
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
        DesktopState committedDesktop = st.Desktops[target];
        if (committedDesktop.ReturnWhenOnline &&
            _borrowedDisplays.TryGetValue(committedDesktop.HomeStableId,
                out BorrowedDisplayState? borrowed))
            borrowed.PreferredCurrent = committedDesktop.RuntimeId;
        AppLog.Info(nameof(SwitchToCore),
            $"Switch committed: '{st.Device}' -> desktop {target}.");

        // Odağı hedef masaüstünde en son aktif olan görünür pencereye ver
        IntPtr focus = st.Desktops[target].LastActive;
        if (!Native.IsWindow(focus) || !Native.IsWindowVisible(focus) ||
            !st.Desktops[target].Windows.Contains(focus))
            focus = st.Desktops[target].Windows.FirstOrDefault(h =>
                Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h));
        if (focus != IntPtr.Zero && WindowResponds(focus))
            Native.SetForegroundWindow(focus);

        // In shared mode never leave the foreground on an off-screen window (fallback for when SetForegroundWindow fails).
        if (SharedTaskbar)
        {
            IntPtr now = Native.GetForegroundWindow();
            if (now != IntPtr.Zero && _hidden.TryGetValue(now, out var rec) && rec.Parked)
            {
                IntPtr tray = FindTrayForDisplay(st.Device);
                if (tray == IntPtr.Zero)
                    tray = Native.FindWindow("Shell_TrayWnd", null);
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
        if (!SafeToModifyWindow(h)) return TargetRestoreOutcome.Failed;
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
        bool belongsToCurrent = _monitors.Values.Any(st =>
            st.Current >= 0 &&
            st.Current < st.Desktops.Count &&
            st.Desktops[st.Current].Windows.Contains(h));
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

    /// <summary>The secondary taskbar hosted on the given display. Used for focus
    /// fallbacks so activation stays on the display being switched instead of
    /// jumping to the primary taskbar (which lives on another monitor and makes
    /// that screen flicker).</summary>
    private static IntPtr FindTrayForDisplay(string device)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (found == IntPtr.Zero &&
                Native.GetWindowClass(h) == "Shell_SecondaryTrayWnd" &&
                Native.GetMonitorDeviceOfWindow(h) == device)
                found = h;
            return true;
        }, IntPtr.Zero);
        return found;
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
            // A taskbar/Alt-Tab click on a window parked-by-minimizing restores it
            // on screen BEFORE activating it. The membership lookup must therefore
            // not go through Sync(): it would adopt the visible window into the
            // current desktop and drop the record this jump decision depends on
            // (the same trap the hidden-mode jump documents in HandleHiddenForegroundActivated).
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (st.Desktops[i].Windows.Contains(h))
                    {
                        st.Desktops[i].LastActive = h;
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

        // A pending topology placement means the window's physical display is stale
        // (it still sits on the borrowed display while the journal already points at
        // its home display). The journal is authoritative for this event: the jump
        // must not be vetoed by the temporary physical position (§9).
        string? pendingTargetDevice = null;
        if (_pendingTopologyPlacements.TryGetValue(h, out PendingTopologyPlacement? pending) &&
            string.Equals(pending.TargetDevice, record.ParkMonitor,
                StringComparison.OrdinalIgnoreCase))
            pendingTargetDevice = pending.TargetDevice;

        // The app already re-showed the window, so the membership lookup must not go through
        // Sync(): it would adopt the visible window into the current desktop and drop the
        // record this jump decision depends on.
        _switchInProgress = true;
        try
        {
            string? dev = Native.GetMonitorDeviceOfWindow(h);
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (st.Desktops[i].Windows.Contains(h))
                    {
                        switch (TopologyDecisions.DecideHiddenTaskbarJump(
                                   ownerIsCurrent: i == st.Current,
                                   physicalDevice: dev,
                                   ownerDevice: st.Device,
                                   pendingTargetDevice: pendingTargetDevice))
                        {
                            case TopologyDecisions.HiddenTaskbarJump.Jump:
                                if (pendingTargetDevice != null && st.Device != dev)
                                    AppLog.Info(nameof(HandleForegroundActivated),
                                        $"Taskbar activation used pending logical owner: HWND={h} physically on " +
                                        $"'{dev}' but its topology placement targets '{st.Device}'.");
                                st.Desktops[i].LastActive = h;
                                AppLog.Info(nameof(HandleForegroundActivated),
                                    $"Taskbar jump (hidden mode): HWND={h} -> '{st.Device}' desktop {i}.");
                                // ShowManagedWindow re-places the window from the target
                                // record; a verified restore clears the pending marker.
                                SwitchToCore(st, i);
                                break;
                            case TopologyDecisions.HiddenTaskbarJump.LeaveForSync:
                                // The app moved the window to another monitor before showing it:
                                // leave it to Sync, which adopts it into that monitor's current desktop.
                                AppLog.Info(nameof(HandleForegroundActivated),
                                    $"Re-shown HWND={h} now lives on '{dev}' (desktop owner '{st.Device}'); leaving it for Sync adoption.");
                                break;
                        }
                        // Ignore (the owner desktop is already current) lets the next
                        // Sync clean up; hidden records exist only for non-current
                        // desktops, so this cannot normally happen.
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
        if (_topologyTransitionInProgress) return;
        if (!Native.IsWindow(h)) return;
        Sync();
        foreach (var st in _monitors.Values)
            for (int i = 0; i < st.Desktops.Count; i++)
                if (st.Desktops[i].Windows.Contains(h))
                {
                    st.Desktops[i].LastActive = h;
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
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || !WindowResponds(h)) return;
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
        if (_topologyTransitionInProgress) return false;
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

    /// <summary>All windows of every non-current desktop on every monitor. Mode changes
    /// operate on the whole union in one batch so the retry backoff is paid once per
    /// switch instead of once per desktop (N desktops used to mean N × ~600 ms).</summary>
    private List<IntPtr> CollectNonCurrentDesktopWindows() =>
        _monitors.Values
            .SelectMany(st => st.Desktops
                .Where((_, i) => i != st.Current)
                .SelectMany(desktop => desktop.Windows))
            .ToList();

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

            var toPark = CollectNonCurrentDesktopWindows();
            bool failed = toPark.Count > 0 && !ParkManagedWindows(toPark);

            if (failed)
            {
                foreach (var h in _hidden.Where(kv => kv.Value.Parked).Select(kv => kv.Key).ToList())
                    UnparkManagedWindow(h);
                var toHide = CollectNonCurrentDesktopWindows();
                if (toHide.Count > 0)
                    HideManagedWindows(toHide);
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

            var toHide = CollectNonCurrentDesktopWindows();
            bool failed = toHide.Count > 0 && !HideManagedWindows(toHide);

            if (failed)
            {
                foreach (var h in _hidden.Where(kv => !kv.Value.Parked).Select(kv => kv.Key).ToList())
                    ShowManagedWindow(h);
                var toPark = CollectNonCurrentDesktopWindows();
                if (toPark.Count > 0)
                    ParkManagedWindows(toPark);
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
        if (_topologyTransitionInProgress) return;
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
            if (delta <= 0 || OwnedDesktopCount(st) >= MaxDesktopsPerMonitor) return;
            AddDesktop(st);
            createdDesktop = true;
            target = st.Desktops.Count - 1;
        }

        if (!HideOrParkManagedWindows(st.Desktops[st.Current].Windows.Where(h => h != fg)))
        {
            if (createdDesktop)
            {
                st.Desktops.RemoveAt(target);
            }
            return;
        }

        st.Desktops[st.Current].Windows.Remove(fg);
        AddWindow(st.Desktops[target], fg);
        st.Desktops[target].LastActive = fg;
        SwitchToCore(st, target);
    }

    /// <summary>Bir pencereyi herhangi bir monitörün herhangi bir masaüstüne taşır
    /// (genel bakıştaki sürükle-bırak ve sağ tık menüsü bunu kullanır).</summary>
    public void MoveWindowToDesktop(IntPtr h, string dstDevice, int dstLocal)
    {
        if (_topologyTransitionInProgress) return;
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
            foreach (var desktop in st.Desktops)
                desktop.Windows.Remove(h);

        AddWindow(dst.Desktops[dstLocal], h);
        dst.Desktops[dstLocal].LastActive = h;

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
            foreach (var desktop in st.Desktops)
                if (desktop.Windows.Contains(h))
                    return st.Device;
        return null;
    }

    /// <summary>Bir masaüstünü aynı veya başka monitörde belirtilen ekleme konumuna taşır.</summary>
    public bool MoveDesktop(string srcDevice, int srcLocal, string dstDevice, int dstInsertIndex)
    {
        if (_topologyTransitionInProgress) return false;
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

            dstInsertIndex = Math.Clamp(dstInsertIndex, 0, src.Desktops.Count);
            src.Desktops.RemoveAt(srcLocal);
            if (dstInsertIndex > srcLocal) dstInsertIndex--;

            src.Desktops.Insert(dstInsertIndex, reorderedDesktop);
            src.Current = src.Desktops.IndexOf(current);
            if (reorderedDesktop.ReturnWhenOnline &&
                _borrowedDisplays.TryGetValue(reorderedDesktop.HomeStableId,
                    out BorrowedDisplayState? borrowed))
                borrowed.OriginalOrder = src.Desktops
                    .Where(d => string.Equals(d.HomeStableId, reorderedDesktop.HomeStableId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.RuntimeId)
                    .ToList();
            PersistHidden();
            return true;
        }

        if (OwnedDesktopCount(dst) >= MaxDesktopsPerMonitor) return false;
        dstInsertIndex = Math.Clamp(dstInsertIndex, 0, dst.Desktops.Count);

        var desktop = src.Desktops[srcLocal];
        // Repositioning a whole desktop across displays is all-or-nothing. A
        // single integrity-skipped window must reject the operation before the
        // source/destination lists or any window geometry are changed.
        foreach (IntPtr h in desktop.Windows.Where(Native.IsWindow))
            if (!CanReassignWindow(h, nameof(MoveDesktop)))
                return false;
        bool wasCurrent = src.Current == srcLocal;
        if (!HideOrParkManagedWindows(desktop.Windows)) return false;
        var dstCurrent = dst.Desktops[dst.Current];

        // Do not mutate either desktop list until every physical window move has
        // been verified. If one move fails, return already moved windows to the
        // source display and restore visibility when the source desktop was active.
        var repositioned = new List<IntPtr>();
        foreach (var h in desktop.Windows.ToList())
        {
            if (!Native.IsWindow(h)) { desktop.Windows.Remove(h); continue; }
            if (RepositionWindow(h, srcDevice, dstDevice))
            {
                repositioned.Add(h);
                continue;
            }

            bool geometryRolledBack = true;
            foreach (IntPtr moved in repositioned.AsEnumerable().Reverse())
                geometryRolledBack &= RepositionWindow(moved, dstDevice, srcDevice);
            bool presentationRolledBack = !wasCurrent || RestoreManagedWindows(desktop.Windows);
            PersistHidden();
            if (!geometryRolledBack || !presentationRolledBack)
                AppLog.Warning(nameof(MoveDesktop),
                    "Cross-display desktop move was cancelled, but rollback was incomplete.");
            return false;
        }

        src.Desktops.RemoveAt(srcLocal);
        if (src.Desktops.Count == 0) AddDesktop(src);
        if (src.Current > srcLocal) src.Current--;
        if (src.Current >= src.Desktops.Count) src.Current = src.Desktops.Count - 1;

        desktop.HomeStableId = dst.StableId;
        desktop.HostStableId = dst.StableId;
        desktop.ReturnWhenOnline = false;
        dst.Desktops.Insert(dstInsertIndex, desktop);
        dst.Current = dst.Desktops.IndexOf(dstCurrent);

        // Kaynak monitörde aktif masaüstü taşındıysa kalan aktif masaüstünü görünür yap
        if (wasCurrent)
            foreach (var h in src.Desktops[src.Current].Windows)
                ShowOrUnparkManagedWindow(h);

        PruneTrailingEmpty(src);
        PersistHidden();
        return true;
    }

    /// <summary>Pencereyi kaynak monitördeki göreli konumunu koruyarak hedef monitöre taşır.
    /// For a parked window: map the saved rectangle by DPI and transfer it to the target display's parking area.</summary>
    private bool RepositionWindow(IntPtr h, string srcDevice, string dstDevice)
    {
        if (!SafeToModifyWindow(h)) return false;
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

    /// <summary>Move a parked window across displays: map its saved on-screen rectangle by DPI
    /// (outer layer) and transfer the window to the target display's parking area.</summary>
    private bool RepositionParkedWindow(IntPtr h, HiddenWindowRecord rec, string srcDevice, string dstDevice)
    {
        var srcScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == srcDevice);
        var dstScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == dstDevice);
        if (srcScreen == null || dstScreen == null) return false;
        if (!Native.IsWindow(h) || !MatchesWindowIdentity(h, rec)) return false;

        var saved = Rectangle.FromLTRB(rec.NormalLeft, rec.NormalTop, rec.NormalRight, rec.NormalBottom);
        Rectangle mapped = MapWindowRect(ToRECT(saved), srcScreen, dstScreen);
        return RepositionParkedWindowToTarget(h, rec, mapped, srcDevice, dstDevice);
    }

    /// <summary>
    /// Bottom layer of the parked cross-display move: transfer a parked window to a
    /// pre-computed target rectangle, keeping the parked representation. Callers that
    /// already own an exact mapping (the topology return path uses WindowTransitSnapshot
    /// data) come here directly to avoid a second DPI round-trip.
    /// </summary>
    private bool RepositionParkedWindowToTarget(IntPtr h, HiddenWindowRecord rec, Rectangle mapped,
        string srcDevice, string dstDevice)
    {
        if (!Native.IsWindow(h) || !MatchesWindowIdentity(h, rec)) return false;

        bool iconic = Native.IsIconic(h);
        Size size = mapped.Size;
        if (!iconic && Native.GetWindowRect(h, out var wr))
            size = new Size(wr.Right - wr.Left, wr.Bottom - wr.Top);

        Rectangle park;
        if (iconic)
        {
            if (!TryGetMinimizedParkRect(dstDevice, mapped.Size, out park))
            {
                AppLog.Warning(nameof(RepositionParkedWindowToTarget),
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
            // SavedShowCmd is deliberately kept: a normal/maximized window parked by
            // minimizing must remember its recorded state across displays too, or it
            // would come back as merely-minimized on the target display.
            ParkMonitor = dstDevice
        };
        var target = new ParkCandidate(h, updated, park, iconic);
        bool moved = TryParkCandidateWithRetry(target, nameof(RepositionParkedWindowToTarget),
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
                rolledBack = TryGetMinimizedParkRect(srcDevice, mapped.Size, out sourcePark) &&
                    TryParkCandidateWithRetry(new ParkCandidate(h, rec, sourcePark, true),
                        nameof(RepositionParkedWindowToTarget), out _, out _);
            }
            else
            {
                sourcePark = GetParkRect(srcDevice, size);
                rolledBack = TryParkCandidateWithRetry(new ParkCandidate(h, rec, sourcePark, false),
                    nameof(RepositionParkedWindowToTarget), out _, out _);
            }
        }

        if (outcome is ParkOutcome.Destroyed or ParkOutcome.IdentityMismatch)
            RemoveHiddenRecord(h);

        string diagnostic = diag == null
            ? $"outcome={outcome}"
            : $"outcome={outcome}, apiSucceeded={diag.ApiSucceeded}, " +
              $"win32Error={(diag.Win32Error?.ToString() ?? "null")}, " +
              $"windowRect={FormatRect(diag.WindowRect)}, normalRect={FormatRect(diag.NormalRect)}";
        RaiseWindowControlWarning(nameof(RepositionParkedWindowToTarget),
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

    private static Rectangle MapWindowRect(Rectangle rect, DisplaySnapshot source,
        DisplaySnapshot destination)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return rect;
        Rectangle sourceArea = source.WorkingArea.Width > 0 && source.WorkingArea.Height > 0
            ? source.WorkingArea
            : source.Bounds;
        Rectangle destinationArea = destination.WorkingArea.Width > 0 && destination.WorkingArea.Height > 0
            ? destination.WorkingArea
            : destination.Bounds;
        if (sourceArea.Width <= 0 || sourceArea.Height <= 0 ||
            destinationArea.Width <= 0 || destinationArea.Height <= 0)
            return rect;

        double dpiRatio = source.Dpi > 0 ? destination.Dpi / (double)source.Dpi : 1.0;
        int width = Math.Clamp((int)Math.Round(rect.Width * dpiRatio), 1, destinationArea.Width);
        int height = Math.Clamp((int)Math.Round(rect.Height * dpiRatio), 1, destinationArea.Height);
        double relativeX = (rect.Left - sourceArea.Left) / (double)sourceArea.Width;
        double relativeY = (rect.Top - sourceArea.Top) / (double)sourceArea.Height;
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
