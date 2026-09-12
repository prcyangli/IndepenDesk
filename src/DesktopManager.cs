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

    private sealed record HiddenStateFile(
        int Version,
        int SessionId,
        int PointerSize,
        IReadOnlyList<HiddenWindowRecord> Windows);

    private const int HiddenStateVersion = 2;
    private const string EmptyPersistedState = "<empty>";

    private readonly Dictionary<string, MonitorState> _monitors = new();
    private readonly Dictionary<IntPtr, HiddenWindowRecord> _hidden = new();
    private readonly HashSet<HashSet<IntPtr>> _retainedEmptyDesktops = new(ReferenceEqualityComparer.Instance);
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly int _sessionId = GetCurrentSessionId();
    private readonly string _stateFile;
    private string? _lastPersisted;
    private bool _windowControlWarningRaised;
    private bool _stateFileBlocked;
    private bool _switchInProgress;

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

    /// <summary>Bir pencere güvenli biçimde gizlenemediğinde oturumda bir kez tetiklenir.</summary>
    public event Action? WindowControlFailed;

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
                return;
            }

            var state = JsonSerializer.Deserialize<HiddenStateFile>(File.ReadAllText(_stateFile));
            if (state == null || (state.Version != HiddenStateVersion && state.Version != 1) ||
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
                if (record.Parked)
                {
                    // Shared-taskbar mode parked this window off-screen; put it back.
                    if (!RestoreParkedWindow(hwnd, record))
                    {
                        _hidden[hwnd] = record;
                        AppLog.Warning(nameof(RecoverPreviousSession),
                            $"Could not restore parked HWND={hwnd}; keeping it in the recovery journal.");
                    }
                    continue;
                }
                if (!Native.IsWindowVisible(hwnd))
                {
                    Native.ShowWindow(hwnd, Native.SW_SHOWNA);
                    if (!Native.IsWindowVisible(hwnd))
                    {
                        _hidden[hwnd] = record;
                        AppLog.Warning(nameof(RecoverPreviousSession),
                            $"Could not restore HWND={hwnd}; keeping it in the recovery journal.");
                    }
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

    private bool PersistHidden() => PersistHiddenSnapshot(_hidden.Values);

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

            record = new HiddenWindowRecord(
                h.ToInt64(),
                IntPtr.Size,
                pid,
                process.StartTime.ToUniversalTime().Ticks,
                process.SessionId,
                className,
                Parked: false,
                NormalLeft: 0,
                NormalTop: 0,
                NormalRight: 0,
                NormalBottom: 0,
                SavedShowCmd: 0,
                ParkMonitor: null);
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

    private static bool MatchesWindowIdentity(IntPtr h, HiddenWindowRecord record)
    {
        try
        {
            if (!Native.IsWindow(h) || Native.GetWindowClass(h) != record.ClassName)
                return false;
            if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 ||
                pid != record.ProcessId) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            return process.SessionId == record.SessionId &&
                   process.StartTime.ToUniversalTime().Ticks == record.ProcessStartTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the intended hidden set before changing any window visibility. If the
    /// journal cannot be committed, no new window is hidden.
    /// </summary>
    private bool HideManagedWindows(IEnumerable<IntPtr> handles)
    {
        var candidates = new List<(IntPtr Handle, HiddenWindowRecord Record)>();
        bool identityCaptureFailed = false;
        foreach (IntPtr h in handles.Distinct())
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) continue;
            if (!TryCaptureWindowIdentity(h, out var record))
            {
                AppLog.Warning(nameof(HideManagedWindows),
                    $"Skipped HWND={h} because its identity could not be captured safely.");
                if (Native.IsWindow(h) && Native.IsWindowVisible(h))
                    RaiseWindowControlWarning();
                identityCaptureFailed = true;
                continue;
            }
            candidates.Add((h, record));
        }

        // Switching with only part of the current desktop hidden would violate the
        // manager's core invariant. Leave every window untouched in that case.
        if (identityCaptureFailed) return false;
        if (candidates.Count == 0) return true;

        var planned = _hidden.Values.ToDictionary(r => r.Handle);
        foreach (var candidate in candidates)
            planned[candidate.Record.Handle] = candidate.Record;
        if (!PersistHiddenSnapshot(planned.Values))
        {
            RaiseWindowControlWarning();
            return false;
        }

        bool hideFailed = false;
        foreach (var candidate in candidates)
        {
            Native.ShowWindow(candidate.Handle, Native.SW_HIDE);
            if (!Native.IsWindowVisible(candidate.Handle) &&
                MatchesWindowIdentity(candidate.Handle, candidate.Record))
            {
                _hidden[candidate.Handle] = candidate.Record;
            }
            else
            {
                hideFailed = true;
                AppLog.Warning(nameof(HideManagedWindows),
                    $"Could not verify that HWND={candidate.Handle} was hidden.");
                if (Native.IsWindow(candidate.Handle) && Native.IsWindowVisible(candidate.Handle))
                    RaiseWindowControlWarning();
            }
        }

        // Reconcile failed or raced hides with the write-ahead snapshot.
        PersistHidden();
        if (!hideFailed) return true;

        // Do not continue a desktop transition with a half-hidden source set.
        // Restore every window hidden by this attempt and leave recovery records
        // behind for any window that cannot be shown again.
        foreach (var candidate in candidates)
            ShowManagedWindow(candidate.Handle);
        PersistHidden();
        return false;
    }

    private void RaiseWindowControlWarning()
    {
        if (_windowControlWarningRaised) return;
        _windowControlWarningRaised = true;
        WindowControlFailed?.Invoke();
    }

    private bool ShowManagedWindow(IntPtr h)
    {
        if (!_hidden.TryGetValue(h, out var record)) return false;
        if (!MatchesWindowIdentity(h, record))
        {
            _hidden.Remove(h);
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
        _hidden.Remove(h);
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

    private sealed record ParkCandidate(IntPtr Handle, HiddenWindowRecord Record, Rectangle ParkRect);

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

        Rectangle park = GetParkRect(dev, size);
        if (iconic)
            park = new Rectangle(park.X, park.Y, normal.Width, normal.Height);

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
        candidate = new ParkCandidate(h, record, park);
        return true;
    }

    private static void ApplyPark(IntPtr h, ParkCandidate c)
    {
        if (Native.IsIconic(h))
        {
            var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(h, ref pl)) return;
            pl.showCmd = Native.SW_SHOWMINIMIZED;
            pl.rcNormalPosition = ToRECT(c.ParkRect);
            Native.SetWindowPlacement(h, ref pl);
        }
        else
        {
            Native.SetWindowPos(h, IntPtr.Zero, c.ParkRect.X, c.ParkRect.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>把停靠窗口按记录还原到屏幕内；不动 _hidden 和日志（由调用方负责）。</summary>
    private static bool RestoreParkedWindow(IntPtr h, HiddenWindowRecord rec)
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
            if (!hasPlacement) return false;
            pl.showCmd = Native.SW_SHOWMINIMIZED;
            pl.rcNormalPosition = ToRECT(saved);
            Native.SetWindowPlacement(h, ref pl);
            return !IsWindowOffScreen(h);
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

    /// <summary>停靠事务：与 HideManagedWindows 相同的写前日志 + 全有或全无契约。</summary>
    private bool ParkManagedWindows(IEnumerable<IntPtr> handles)
    {
        var candidates = new List<ParkCandidate>();
        bool captureFailed = false;
        foreach (IntPtr h in handles.Distinct())
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) continue;
            // 已经停靠的窗口保持原样（保留其原始屏幕位置记录）。
            if (_hidden.TryGetValue(h, out var existing) && existing.Parked &&
                MatchesWindowIdentity(h, existing) && IsWindowOffScreen(h))
                continue;
            if (!TryCaptureParkCandidate(h, out var candidate))
            {
                AppLog.Warning(nameof(ParkManagedWindows),
                    $"Skipped HWND={h} because its park placement could not be captured safely.");
                if (Native.IsWindow(h) && Native.IsWindowVisible(h))
                    RaiseWindowControlWarning();
                captureFailed = true;
                continue;
            }
            candidates.Add(candidate);
        }

        if (captureFailed) return false;
        if (candidates.Count == 0) return true;

        var planned = _hidden.Values.ToDictionary(r => r.Handle);
        foreach (var c in candidates)
            planned[c.Record.Handle] = c.Record;
        if (!PersistHiddenSnapshot(planned.Values))
        {
            RaiseWindowControlWarning();
            return false;
        }

        bool parkFailed = false;
        foreach (var c in candidates)
        {
            ApplyPark(c.Handle, c);
            if (IsWindowOffScreen(c.Handle) && MatchesWindowIdentity(c.Handle, c.Record))
            {
                _hidden[c.Handle] = c.Record;
            }
            else
            {
                parkFailed = true;
                AppLog.Warning(nameof(ParkManagedWindows),
                    $"Could not verify that HWND={c.Handle} was parked.");
                if (Native.IsWindow(c.Handle) && Native.IsWindowVisible(c.Handle))
                    RaiseWindowControlWarning();
            }
        }

        PersistHidden();
        if (!parkFailed) return true;

        // 不带着只停靠一半的源桌面继续切换：还原本轮停靠的窗口。
        foreach (var c in candidates)
            RestoreParkedWindow(c.Handle, c.Record);
        PersistHidden();
        return false;
    }

    /// <summary>把停靠/隐藏窗口恢复（隐藏模式遗留的记录照旧显示）。只处理记录本身。</summary>
    private bool UnparkManagedWindow(IntPtr h)
    {
        if (!_hidden.TryGetValue(h, out var record)) return false;
        if (!record.Parked) return ShowManagedWindow(h);
        if (!MatchesWindowIdentity(h, record))
        {
            _hidden.Remove(h);
            return false;
        }
        if (!IsWindowOffScreen(h))
        {
            // 已经回到屏幕内（应用自行移动或还原）：只清理记录。
            _hidden.Remove(h);
            return true;
        }
        if (!RestoreParkedWindow(h, record))
        {
            AppLog.Warning(nameof(UnparkManagedWindow), $"Could not restore parked HWND={h}.");
            return false;
        }
        _hidden.Remove(h);
        return true;
    }

    private bool HideOrParkManagedWindows(IEnumerable<IntPtr> handles) =>
        SharedTaskbar ? ParkManagedWindows(handles) : HideManagedWindows(handles);

    private bool ShowOrUnparkManagedWindow(IntPtr h) =>
        SharedTaskbar ? UnparkManagedWindow(h) : ShowManagedWindow(h);

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

    private void AddWindow(HashSet<IntPtr> desktop, IntPtr h)
    {
        if (desktop.Add(h))
            _retainedEmptyDesktops.Remove(desktop);
    }

    /// <summary>Aktif masaüstünün gerisindeki boş son masaüstlerini kaldırır.</summary>
    private void PruneTrailingEmpty(MonitorState st)
    {
        while (st.Desktops.Count - 1 > st.Current &&
               st.Desktops[^1].Count == 0 &&
               !_retainedEmptyDesktops.Contains(st.Desktops[^1]))
        {
            st.Desktops.RemoveAt(st.Desktops.Count - 1);
            st.LastActive.RemoveAt(st.LastActive.Count - 1);
        }
    }

    /// <summary>
    /// Durumu gerçekle senkronlar: yeni pencereleri sahiplen, kapananları temizle,
    /// monitör değiştirenleri taşı, kaybolan monitörlerin gizli pencerelerini kurtar.
    /// Invariant: görünür bir pencere her zaman bulunduğu monitörün aktif masaüstü setindedir.
    /// </summary>
    public void Sync()
    {
        var currentDevices = Screen.AllScreens.Select(s => s.DeviceName).ToHashSet();

        foreach (string dev in currentDevices)
            if (!_monitors.ContainsKey(dev))
                _monitors[dev] = new MonitorState { Device = dev };

        foreach (string dev in _monitors.Keys.Where(d => !currentDevices.Contains(d)).ToList())
        {
            foreach (var set in _monitors[dev].Desktops)
            {
                _retainedEmptyDesktops.Remove(set);
                foreach (var h in set)
                    ShowOrUnparkManagedWindow(h);
            }
            _monitors.Remove(dev);
        }

        foreach (var st in _monitors.Values)
            foreach (var set in st.Desktops)
                set.RemoveWhere(h =>
                {
                    if (!Native.IsWindow(h))
                    {
                        _hidden.Remove(h);
                        return true;
                    }
                    if (_hidden.TryGetValue(h, out var record) && !MatchesWindowIdentity(h, record))
                    {
                        _hidden.Remove(h);
                        return true;
                    }
                    if (Native.IsWindowVisible(h) && !IsEligible(h))
                    {
                        _hidden.Remove(h);
                        return true;
                    }
                    return false;
                });

        foreach (var (h, record) in _hidden.ToList())
            if (!MatchesWindowIdentity(h, record))
                _hidden.Remove(h);

        foreach (var h in EnumerateTopLevelWindows())
        {
            if (!IsEligible(h)) continue;

            // 共享任务栏模式：停靠在屏幕外的窗口保持其桌面归属；
            // 任务栏按钮是跳回它所在桌面的入口。
            if (_hidden.TryGetValue(h, out var parkedRecord) && parkedRecord.Parked &&
                MatchesWindowIdentity(h, parkedRecord) && IsWindowOffScreen(h))
                continue;

            string? dev = Native.GetMonitorDeviceOfWindow(h);
            if (dev == null || !_monitors.TryGetValue(dev, out var st)) continue;

            // An application or the user may have shown one of our hidden windows.
            // Visible windows belong to the current desktop and must not remain in
            // the crash-recovery journal, even if already present in that set.
            _hidden.Remove(h);

            if (!st.Desktops[st.Current].Contains(h))
            {
                // Başka bir set'te kayıtlıysa oradan çıkar (monitör değiştirmiş
                // veya gizliyken uygulama tarafından tekrar gösterilmiş olabilir)
                foreach (var other in _monitors.Values)
                    foreach (var set in other.Desktops)
                        set.Remove(h);
                AddWindow(st.Desktops[st.Current], h);
            }
        }

        foreach (var st in _monitors.Values)
            PruneTrailingEmpty(st);

        PersistHidden();
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
        if (dev == null) return;
        Sync();
        if (!_monitors.TryGetValue(dev, out var st)) return;

        int target = st.Current + delta;
        if (target < 0)
        {
            DesktopSwitched?.Invoke(BuildInfo(st)); // uçta: sadece OSD göster
            return;
        }
        bool createdDesktop = false;
        if (target >= st.Desktops.Count)
        {
            bool canGrow = delta > 0
                && st.Desktops.Count < MaxDesktopsPerMonitor
                && st.Desktops[st.Current].Count > 0; // boş masaüstünden yenisi açılmaz
            if (!canGrow)
            {
                DesktopSwitched?.Invoke(BuildInfo(st));
                return;
            }
            AddDesktop(st);
            createdDesktop = true;
            target = st.Desktops.Count - 1;
        }
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
                _hidden.Remove(h);
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
        if (st.Current == target)
        {
            DesktopSwitched?.Invoke(BuildInfo(st));
            return true;
        }

        SwitchStarting?.Invoke(st.Device, st.Current, target);

        IntPtr fg = Native.GetForegroundWindow();
        if (st.Desktops[st.Current].Contains(fg))
            st.LastActive[st.Current] = fg;

        if (!HideOrParkManagedWindows(st.Desktops[st.Current]))
        {
            DesktopSwitched?.Invoke(BuildInfo(st));
            return false;
        }

        st.Current = target;

        foreach (var h in st.Desktops[target].ToList())
        {
            if (!Native.IsWindow(h)) { st.Desktops[target].Remove(h); continue; }
            ShowOrUnparkManagedWindow(h);
        }

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

    /// <summary>前台窗口变化（任务栏/Alt-Tab 激活了停靠窗口）：跳转到它所在的桌面。</summary>
    public void HandleForegroundActivated(IntPtr h)
    {
        if (!SharedTaskbar || _switchInProgress) return;
        if (h == IntPtr.Zero || !_hidden.TryGetValue(h, out var record) || !record.Parked) return;
        if (!MatchesWindowIdentity(h, record)) return;

        _switchInProgress = true;
        try
        {
            Sync();
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (st.Desktops[i].Contains(h))
                    {
                        st.LastActive[i] = h;
                        SwitchToCore(st, i);
                        return;
                    }

            // 记录存在但已不属于任何桌面（崩溃恢复遗留等）：并入其显示器的当前桌面并显示。
            string? dev = record.ParkMonitor != null && _monitors.ContainsKey(record.ParkMonitor)
                ? record.ParkMonitor
                : Native.GetMonitorDeviceUnderCursor();
            if (dev != null && _monitors.TryGetValue(dev, out var adopt))
            {
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

    /// <summary>切换共享任务栏模式。启用失败时完整回滚并返回 false；停用总是成功（可能提示提权窗口泄漏）。</summary>
    public bool SetSharedTaskbarMode(bool enable)
    {
        if (enable == SharedTaskbar) return true;
        Sync();

        if (enable)
        {
            // 先把所有由本程序隐藏的窗口恢复到屏幕上（保持原位置）。
            foreach (var h in _hidden.Where(kv => !kv.Value.Parked).Select(kv => kv.Key).ToList())
                ShowManagedWindow(h);
            PersistHidden();

            SharedTaskbar = true;
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
                // 回滚：先把已停靠的窗口还原到屏幕内（否则隐藏后会在停靠区被“显示”），
                // 再恢复隐藏模式，把非当前桌面重新隐藏。
                SharedTaskbar = false;
                foreach (var h in _hidden.Where(kv => kv.Value.Parked).Select(kv => kv.Key).ToList())
                    UnparkManagedWindow(h);
                foreach (var m in _monitors.Values)
                    for (int i = 0; i < m.Desktops.Count; i++)
                        if (i != m.Current)
                            HideManagedWindows(m.Desktops[i]);
                PersistHidden();
                RaiseWindowControlWarning();
                return false;
            }

            PersistHidden();
            return true;
        }
        else
        {
            SharedTaskbar = false;
            foreach (var h in _hidden.Where(kv => kv.Value.Parked).Select(kv => kv.Key).ToList())
                UnparkManagedWindow(h);
            bool failed = false;
            foreach (var st in _monitors.Values)
                for (int i = 0; i < st.Desktops.Count; i++)
                    if (i != st.Current && st.Desktops[i].Count > 0 && !HideManagedWindows(st.Desktops[i]))
                        failed = true;
            PersistHidden();
            if (failed)
                RaiseWindowControlWarning();
            return true;
        }
    }

    // ---------- pencere ve masaüstü taşıma ----------

    /// <summary>Aktif pencereyi kendi monitöründe bitişik masaüstüne taşır ve oraya geçer.
    /// Son masaüstünden ileri taşıma yeni masaüstü oluşturur.</summary>
    public void MoveActiveWindow(int delta)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsEligible(fg)) return;
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

        Rectangle park = GetParkRect(dstDevice, size);
        if (iconic)
            park = new Rectangle(park.X, park.Y, mapped.Width, mapped.Height);

        if (iconic)
        {
            var pl = new Native.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(h, ref pl))
            {
                pl.showCmd = Native.SW_SHOWMINIMIZED;
                pl.rcNormalPosition = ToRECT(park);
                Native.SetWindowPlacement(h, ref pl);
            }
        }
        else
        {
            Native.SetWindowPos(h, IntPtr.Zero, park.X, park.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        _hidden[h] = rec with
        {
            NormalLeft = mapped.Left,
            NormalTop = mapped.Top,
            NormalRight = mapped.Right,
            NormalBottom = mapped.Bottom,
            ParkMonitor = dstDevice
        };
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
