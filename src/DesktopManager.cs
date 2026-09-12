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
        string ClassName);

    private sealed record HiddenStateFile(
        int Version,
        int SessionId,
        int PointerSize,
        IReadOnlyList<HiddenWindowRecord> Windows);

    private const int HiddenStateVersion = 1;
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
            if (state == null || state.Version != HiddenStateVersion ||
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
                className);
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
                    ShowManagedWindow(h);
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
        if (!HideManagedWindows(new[] { h })) return false;

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
                ShowManagedWindow(h);
            }
        }

        if (!targetIsCurrent)
            HideManagedWindows(target);

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

        st.LastActive[st.Current] = Native.GetForegroundWindow();

        if (!HideManagedWindows(st.Desktops[st.Current]))
        {
            DesktopSwitched?.Invoke(BuildInfo(st));
            return false;
        }

        st.Current = target;

        foreach (var h in st.Desktops[target].ToList())
        {
            if (!Native.IsWindow(h)) { st.Desktops[target].Remove(h); continue; }
            ShowManagedWindow(h);
        }

        // Odağı hedef masaüstünde en son aktif olan görünür pencereye ver
        IntPtr focus = st.LastActive[target];
        if (!Native.IsWindow(focus) || !Native.IsWindowVisible(focus) ||
            !st.Desktops[target].Contains(focus))
            focus = st.Desktops[target].FirstOrDefault(h =>
                Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h));
        if (focus != IntPtr.Zero)
            Native.SetForegroundWindow(focus);

        PruneTrailingEmpty(st);
        PersistHidden();
        DesktopSwitched?.Invoke(BuildInfo(st));
        return true;
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

        if (!HideManagedWindows(st.Desktops[st.Current].Where(h => h != fg)))
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
        if (dst.Current != dstLocal && !HideManagedWindows(new[] { h })) return;

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
            ShowManagedWindow(h);
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
        if (!HideManagedWindows(set)) return false;
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
                ShowManagedWindow(h);

        PruneTrailingEmpty(src);
        PersistHidden();
        return true;
    }

    /// <summary>Pencereyi kaynak monitördeki göreli konumunu koruyarak hedef monitöre taşır.</summary>
    private static void RepositionWindow(IntPtr h, string srcDevice, string dstDevice)
    {
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

    /// <summary>Tüm gizli pencereleri geri getirir (çıkışta ve tray menüsünden çağrılır).</summary>
    public void RestoreAll()
    {
        foreach (var st in _monitors.Values)
            st.Current = 0;

        foreach (var h in _hidden.Keys.ToList())
            ShowManagedWindow(h);
        PersistHidden();
    }
}
