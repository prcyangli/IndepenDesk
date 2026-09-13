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

/// <summary>被跳过的不可管理窗口（句柄、进程名、标题、原因），供通知层显示。</summary>
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

    /// <summary>一个无法安全管理（隐藏/停靠）的窗口；跳过后保持可见并跟随当前桌面。</summary>
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

    // 停靠校验失败可能只是窗口侧的瞬时否决（窗口正被拖动、应用做了位置钳制等）：
    // 失败后整批退避重试，总计最多 3 次尝试，每轮间隔 250 ms，整场切换的额外延迟 ≤500 ms。
    private const int ParkAttemptCount = 3;
    private const int ParkRetryDelayMs = 250;
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
    private readonly Dictionary<uint, ObservedProcessIdentity?> _syncProcessIdentities = new();
    private readonly Dictionary<IntPtr, UnmanageableWindow> _unmanageable = new();
    private bool _syncInProgress;
    private bool _stateFileBlocked;
    private bool _switchInProgress;
    private IntPtr _minimizeSource;
    private long _suppressParkedForegroundUntil;
    private IntPtr _explicitMinimizeRestore;
    private long _explicitMinimizeRestoreUntil;
    private readonly Dictionary<IntPtr, long> _internalMinimizeUntil = new();

    /// <summary>自身是否以管理员权限运行；提权后可控制同用户的全部窗口，无需预检测。</summary>
    private static readonly bool OwnProcessElevated = Native.IsOwnProcessElevated();

    /// <summary>
    /// 共享任务栏模式：非当前桌面的窗口不隐藏，而是移到屏幕外停靠。
    /// 它们保留在任务栏/Alt-Tab 中；被激活时自动跳转到所在桌面。
    /// </summary>
    public bool SharedTaskbar { get; private set; }

    /// <summary>Geçiş kesinleşti, pencereler henüz gizlenmedi: (cihaz, eski index, yeni index).
    /// Animasyon katmanının ekran görüntüsünü bu anda alması gerekir.</summary>
    public event Action<string, int, int>? SwitchStarting;

    /// <summary>Geçiş tamamlandı (veya uçta OSD tazelemesi).</summary>
    public event Action<SwitchInfo>? DesktopSwitched;

    /// <summary>一个窗口被判定为不可管理（已跳过，保持可见），或一次桌面操作被取消
    /// （载荷为 null 时表示取消类错误）。</summary>
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
            if (_syncInProgress && _syncProcessIdentities.TryGetValue(pid, out identity))
            {
                return identity is { } cached && cached.SessionId == record.SessionId &&
                       cached.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks;
            }

            using var process = Process.GetProcessById(checked((int)pid));
            identity = new ObservedProcessIdentity(
                process.StartTime.ToUniversalTime().Ticks,
                process.SessionId);
            if (_syncInProgress)
                _syncProcessIdentities[pid] = identity;
            return identity.Value.SessionId == record.SessionId &&
                   identity.Value.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks;
        }
        catch (Exception ex)
        {
            if (Native.IsWindow(h))
                AppLog.Warning(nameof(MatchesWindowIdentity),
                    $"Identity check failed for a live HWND={h}: {ex.GetType().Name}: {ex.Message}.");
            if (_syncInProgress &&
                Native.GetWindowThreadProcessId(h, out uint failedPid) != 0 && failedPid != 0)
                _syncProcessIdentities[failedPid] = null;
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
        foreach (var candidate in candidates)
        {
            // The durable snapshot already contains this record. Add it to the
            // in-memory journal before touching the HWND so rollback can recover
            // even when verification itself fails.
            SetHiddenRecord(candidate.Handle, candidate.Record);
            touched.Add(candidate.Handle);
            Native.ShowWindow(candidate.Handle, Native.SW_HIDE);
            if (!Native.IsWindowVisible(candidate.Handle) &&
                MatchesWindowIdentity(candidate.Handle, candidate.Record))
                continue;

            if (!Native.IsWindow(candidate.Handle))
            {
                RemoveHiddenRecord(candidate.Handle);
                touched.RemoveAt(touched.Count - 1);
                continue;
            }
            if (!MatchesWindowIdentity(candidate.Handle, candidate.Record))
            {
                RemoveHiddenRecord(candidate.Handle);
                touched.RemoveAt(touched.Count - 1);
                // Hidden but the handle was reused: bring the replacement back on screen.
                if (!Native.IsWindowVisible(candidate.Handle))
                    Native.ShowWindow(candidate.Handle, Native.SW_SHOWNA);
                continue;
            }

            bool rolledBack = RollBackHiddenAttempt(touched, previousRecords);
            PersistHidden();
            RaiseWindowControlWarning(nameof(HideManagedWindows),
                rolledBack
                    ? "A normal window could not be hidden; switch cancelled and all changed windows were restored"
                    : "A normal window could not be hidden; switch cancelled but rollback was incomplete (recovery records retained)",
                candidate.Handle);
            return false;
        }

        PersistHidden();
        return true;
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

    /// <summary>该句柄是否已知不可管理；句柄被复用（PID/类名不符）时自动失效并重新检测。</summary>
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

    /// <summary>首次登记一个跨完整性边界、确定无法管理的窗口。</summary>
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

    /// <summary>已知不可管理窗口再次被跳过/操作时：不重复写日志，仅重新发送通知事件
    /// （托盘层按会话去重；通知曾被关闭时，重新开启后下一次跳过仍可弹出提示）。</summary>
    private void NotifyKnownUnmanageable(IntPtr h)
    {
        if (_unmanageable.TryGetValue(h, out var info))
            WindowControlFailed?.Invoke(new UnmanageableWindowInfo(h, info.ProcessName, info.Title, info.Reason));
    }

    /// <summary>
    /// 检测“目标窗口属于提权进程而自身未提权”的情况：UIPI 使这类窗口对
    /// ShowWindow/SetWindowPos 免疫，直接预先跳过；探测无法判定时保守跳过。
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
        if (HasSavedPlacement(record) && IsWindowOffScreen(h) &&
            !RestoreParkedWindow(h, record))
        {
            AppLog.Warning(nameof(ShowManagedWindow),
                $"Could not move HWND={h} from a disconnected display.");
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

    // ---------- 共享任务栏模式：离屏停靠 ----------

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

    /// <summary>停靠中的窗口（或其最小化还原位置）是否完全在所有显示器之外。</summary>
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
    /// 为指定显示器计算一个不与任何显示器相交的停靠矩形。优先放在目标显示器左侧
    /// （保持任务栏按钮归属该显示器），然后右侧，最后回退到远端区域。
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

    /// <summary>一次停靠尝试的诊断现场；不影响业务判定，只用于最终 WARN 与重试 INFO。</summary>
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

    /// <summary>单次停靠尝试：API 返回立即保存结果与错误码，再采集现场并校验是否完全离屏。
    /// 只刷新当前窗口状态，绝不改动第一次捕获的恢复几何（c.Record 保持不变）。</summary>
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
                // 保留原 flags，仅改 showCmd 与还原位置：最小化窗口停靠后仍保持最小化。
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

    /// <summary>把停靠窗口按记录还原到屏幕内；不动 _hidden 和日志（由调用方负责）。</summary>
    private bool RestoreParkedWindow(IntPtr h, HiddenWindowRecord rec)
    {
        var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        bool hasPlacement = Native.GetWindowPlacement(h, ref pl);

        var saved = Rectangle.FromLTRB(rec.NormalLeft, rec.NormalTop, rec.NormalRight, rec.NormalBottom);
        if (!IntersectsAnyScreen(saved))
        {
            // 显示器被拔掉后保存的矩形可能不在任何屏幕上：就近迁移。
            var host = NearestScreen(saved) ?? Screen.PrimaryScreen;
            if (host == null) return false;
            var wa = host.WorkingArea;
            saved.Width = Math.Min(saved.Width, wa.Width);
            saved.Height = Math.Min(saved.Height, wa.Height);
            saved.Location = new Point(wa.Left + (wa.Width - saved.Width) / 2,
                wa.Top + (wa.Height - saved.Height) / 2);
        }

        if (Native.IsIconic(h))
        {
            // 用户最小化了停靠窗口（如 Win+D）：保持最小化，只把还原位置放回屏幕内。
            // 25H2 起 SetWindowPlacement 忽略 rcNormalPosition，须走显示→落位→再最小化链路。
            return RestoreIconicPlacement(h, saved);
        }

        if (Native.IsZoomed(h))
        {
            Screen? screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == rec.ParkMonitor)
                             ?? Screen.PrimaryScreen;
            if (screen == null) return false;
            var wa = screen.WorkingArea;
            if (!Native.SetWindowPos(h, IntPtr.Zero, wa.X, wa.Y, wa.Width, wa.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                return false;
            if (hasPlacement)
            {
                pl.showCmd = Native.SW_SHOWMAXIMIZED;
                pl.rcNormalPosition = ToRECT(saved);
                Native.SetWindowPlacement(h, ref pl);
            }
            return !IsWindowOffScreen(h);
        }

        return Native.SetWindowPos(h, IntPtr.Zero, saved.X, saved.Y, saved.Width, saved.Height,
                   Native.SWP_NOZORDER | Native.SWP_NOACTIVATE) && !IsWindowOffScreen(h);
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
            // 25H2 起 SetWindowPlacement 忽略 rcNormalPosition，须走显示→落位→再最小化链路。
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
    /// 把最小化窗口的还原位置落回屏幕内（保持最小化）。Windows 11 25H2 (build 26200) 起
    /// SetWindowPlacement 会忽略 rcNormalPosition（原生 DefWindowProc 窗口亦已复现）；
    /// 可行写法：短暂还原（SW_SHOWNOACTIVATE，不激活、不抢焦点）→ SetWindowPos 落位 → 再次最小化，
    /// 还原位置会随正常矩形自动落位。
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
            // 落位失败时收回为最小化，避免把窗口留在屏外的可见状态。
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
            // 已经停靠的窗口保持原样（保留其原始屏幕位置记录）。
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

        // 批量退避重试：每轮全组只等一次 250 ms，失败窗口数量不放大总延迟。
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
            // 事务取消：本轮所有触碰过的窗口按逆序还原，恢复日志写回真实状态；
            // 桌面索引不提交，操作失败的窗口始终不进入 _unmanageable。
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

    /// <summary>处置一次非成功的停靠结果；返回 true 表示窗口仍可重试（存活且身份匹配）。</summary>
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

    /// <summary>把停靠/隐藏窗口恢复（隐藏模式遗留的记录照旧显示）。只处理记录本身。</summary>
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
            // 已经回到屏幕内（应用自行移动或还原）：确保可见后清理记录。
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
        _syncProcessIdentities.Clear();
        try
        {
            return SyncCore();
        }
        finally
        {
            _syncProcessIdentities.Clear();
            _syncInProgress = false;
        }
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

            // 共享任务栏模式：停靠在屏幕外的窗口保持其桌面归属；
            // 任务栏按钮是跳回它所在桌面的入口。
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
            // 不可管理窗口（如管理员窗口）始终跟随当前桌面，不能作为"桌面非空"
            // 的依据；仅剩这类窗口时视同空桌面，避免在末尾无限新建。未缓存的
            // 窗口现场探测完整性（纯查询，不登记——登记只发生在真正的隐藏/停靠时）。
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
        Sync();
        if (!Native.IsWindow(h)) return false;
        if (!_monitors.TryGetValue(dstDevice, out var dst)) return false;
        if (dst.Desktops.Count >= MaxDesktopsPerMonitor) return false;
        // Integrity-skipped windows remain visible by design. Do not create an
        // empty desktop and only pretend to move one in the model.
        if (!CanReassignWindow(h, nameof(CreateDesktopAndMoveWindow))) return false;
        if (!HideOrParkManagedWindows(new[] { h })) return false;

        AddDesktop(dst);
        int target = dst.Desktops.Count - 1;
        _retainedEmptyDesktops.Add(dst.Desktops[target]);

        string? srcDevice = null;
        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                if (set.Remove(h))
                    srcDevice = st.Device;

        if (srcDevice != null && srcDevice != dstDevice)
            RepositionWindow(h, srcDevice, dstDevice);

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
        try
        {
            return SwitchToCoreGuarded(st, target);
        }
        finally
        {
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
            if (ShowOrUnparkManagedWindow(h))
            {
                if (targetInitiallyManaged.Contains(h))
                    targetSuccessfullyRestored.Add(h);
            }
            else
            {
                // An identity mismatch removes its recovery record: the HWND was
                // reused and no longer represents the managed window. Prune that
                // stale membership instead of failing the whole transition.
                if (targetInitiallyManaged.Contains(h) && !_hidden.ContainsKey(h))
                    st.Desktops[target].Remove(h);
                else
                {
                    targetRestored = false;
                    targetRestoreFailure = h;
                }
            }
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

        // 共享模式下绝不让前台停留在屏幕外的窗口上（SetForegroundWindow 失败时的兜底）。
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
        foreach (IntPtr h in _internalMinimizeUntil
                     .Where(pair => now > pair.Value)
                     .Select(pair => pair.Key)
                     .ToList())
            _internalMinimizeUntil.Remove(h);
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

    /// <summary>前台窗口变化（任务栏/Alt-Tab 激活了停靠窗口）：跳转到它所在的桌面。</summary>
    public void HandleForegroundActivated(IntPtr h)
    {
        if (!SharedTaskbar || _switchInProgress) return;
        long now = Environment.TickCount64;
        ExpireMinimizeMarkers(now);
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

            // 记录存在但已不属于任何桌面（崩溃恢复遗留等）：并入其显示器的当前桌面并显示。
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

    /// <summary>切换到指定窗口所在的桌面并把焦点交给它（总览“转到此窗口”与任务栏跳转共用）。</summary>
    public void SwitchToWindow(IntPtr h)
    {
        if (!Native.IsWindow(h)) return;
        Sync();
        foreach (var st in _monitors.Values)
            for (int i = 0; i < st.Desktops.Count; i++)
                if (st.Desktops[i].Contains(h))
                {
                    st.LastActive[i] = h;
                    SwitchToCore(st, i);
                    return;
                }
    }

    /// <summary>事务化切换共享任务栏模式；窗口状态全部完成后才提交模式。</summary>
    public bool SetSharedTaskbarMode(bool enable)
    {
        if (enable == SharedTaskbar) return true;
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
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsEligible(fg)) return;
        // 活动窗口无法控制（如管理员窗口）时，移动没有意义：不新建桌面、
        // 不移动，只发通知（不重复登记日志；气泡由托盘层按会话去重）。
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
        Sync();
        if (!Native.IsWindow(h)) return;
        if (!_monitors.TryGetValue(dstDevice, out var dst)) return;
        if (dstLocal < 0 || dstLocal >= dst.Desktops.Count) return;
        if (!CanReassignWindow(h, nameof(MoveWindowToDesktop))) return;
        if (dst.Current != dstLocal && !HideOrParkManagedWindows(new[] { h })) return;

        string? srcDevice = null;
        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                if (set.Remove(h))
                    srcDevice = st.Device;

        if (srcDevice != null && srcDevice != dstDevice)
            RepositionWindow(h, srcDevice, dstDevice);

        AddWindow(dst.Desktops[dstLocal], h);
        dst.LastActive[dstLocal] = h;

        if (dst.Current == dstLocal)
        {
            ShowOrUnparkManagedWindow(h);
        }

        foreach (var st in _monitors.Values) PruneTrailingEmpty(st);
        PersistHidden();
    }

    /// <summary>Bir masaüstünü aynı veya başka monitörde belirtilen ekleme konumuna taşır.</summary>
    public bool MoveDesktop(string srcDevice, int srcLocal, string dstDevice, int dstInsertIndex)
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
        if (!HideOrParkManagedWindows(set)) return false;
        var last = src.LastActive[srcLocal];
        bool wasCurrent = src.Current == srcLocal;
        var dstCurrent = dst.Desktops[dst.Current];

        src.Desktops.RemoveAt(srcLocal);
        src.LastActive.RemoveAt(srcLocal);
        if (src.Desktops.Count == 0) AddDesktop(src);
        if (src.Current > srcLocal) src.Current--;
        if (src.Current >= src.Desktops.Count) src.Current = src.Desktops.Count - 1;

        // Taşınan pencereleri hedef monitöre konumlandır ve gizle (eklenen masaüstü aktif değil)
        foreach (var h in set.ToList())
        {
            if (!Native.IsWindow(h)) { set.Remove(h); continue; }
            RepositionWindow(h, srcDevice, dstDevice);
        }
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
    /// 停靠中的窗口：按 DPI 映射保存的矩形并转移到目标显示器的停靠区。</summary>
    private void RepositionWindow(IntPtr h, string srcDevice, string dstDevice)
    {
        if (_hidden.TryGetValue(h, out var rec) && rec.Parked)
        {
            RepositionParkedWindow(h, rec, srcDevice, dstDevice);
            return;
        }

        var srcScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == srcDevice);
        var dstScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == dstDevice);
        if (srcScreen == null || dstScreen == null) return;
        bool wasVisible = Native.IsWindowVisible(h);
        bool wasMaximized = Native.IsZoomed(h);

        var placement = new Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>()
        };
        bool hasPlacement = Native.GetWindowPlacement(h, ref placement);

        Native.RECT sourceRect;
        if (hasPlacement)
            sourceRect = placement.rcNormalPosition;
        else if (!Native.GetWindowRect(h, out sourceRect))
            return;

        Rectangle mapped = MapWindowRect(sourceRect, srcScreen, dstScreen);

        if (wasMaximized && hasPlacement)
        {
            placement.rcNormalPosition = new Native.RECT
            {
                Left = mapped.Left,
                Top = mapped.Top,
                Right = mapped.Right,
                Bottom = mapped.Bottom
            };
            placement.showCmd = (uint)(wasVisible ? Native.SW_MAXIMIZE : Native.SW_HIDE);
            if (!Native.SetWindowPlacement(h, ref placement))
                AppLog.Warning(nameof(RepositionWindow), $"SetWindowPlacement failed for HWND={h}.");

            // SetWindowPlacement also controls the show state. Enforce the original
            // hidden state in case a third-party window changes it while moving.
            if (!wasVisible && Native.IsWindowVisible(h))
            {
                Native.ShowWindow(h, Native.SW_HIDE);
                AppLog.Warning(nameof(RepositionWindow),
                    $"HWND={h} became visible while updating its hidden placement.");
            }
            return;
        }

        if (!Native.SetWindowPos(h, IntPtr.Zero, mapped.X, mapped.Y, mapped.Width, mapped.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
            AppLog.Warning(nameof(RepositionWindow), $"SetWindowPos failed for HWND={h}.");
    }

    /// <summary>跨显示器移动停靠窗口：按 DPI 映射其保存的屏幕内矩形，并把窗口转移到目标显示器的停靠区。</summary>
    private void RepositionParkedWindow(IntPtr h, HiddenWindowRecord rec, string srcDevice, string dstDevice)
    {
        var srcScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == srcDevice);
        var dstScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == dstDevice);
        if (srcScreen == null || dstScreen == null) return;

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
                return;
            }
        }
        else
        {
            park = GetParkRect(dstDevice, size);
        }

        bool applied = false;
        if (iconic)
        {
            var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(h, ref pl))
            {
                pl.showCmd = Native.SW_SHOWMINIMIZED;
                pl.rcNormalPosition = ToRECT(park);
                applied = Native.SetWindowPlacement(h, ref pl);
            }
        }
        else
        {
            applied = Native.SetWindowPos(h, IntPtr.Zero, park.X, park.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
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
        bool verified = iconic
            ? applied && IsManagedWindowParked(h, updated)
            : applied && IsWindowOffScreen(h);
        if (!verified)
        {
            AppLog.Warning(nameof(RepositionParkedWindow),
                $"Could not re-park HWND={h} on '{dstDevice}'; recovery record was left unchanged.");
            return;
        }

        SetHiddenRecord(h, updated);
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
