using System.Runtime.InteropServices;

namespace IndepenDesk;

/// <summary>A display identity and its last known geometry for this process lifetime.</summary>
internal sealed record DisplaySnapshot(
    string StableId,
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkingArea,
    uint Dpi,
    bool IsInternal,
    bool IsPrimary);

/// <summary>
/// Reads the active Windows display paths.  Screen.DeviceName is only a live routing
/// address and can be renumbered across a dock/sleep transition; monitorDevicePath is
/// used as the runtime-stable identity whenever DisplayConfig exposes it.
/// </summary>
internal static class DisplayTopology
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int DisplayConfigDeviceInfoGetSourceName = 1;
    private const int DisplayConfigDeviceInfoGetTargetName = 2;

    internal static Dictionary<string, DisplaySnapshot> Capture()
    {
        var paths = QueryActivePathIdentities();
        var result = new Dictionary<string, DisplaySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (Screen screen in Screen.AllScreens)
        {
            paths.TryGetValue(screen.DeviceName, out PathIdentity? identity);
            string stableId = !string.IsNullOrWhiteSpace(identity?.MonitorDevicePath)
                ? identity.MonitorDevicePath
                : GetLegacyStableId(screen.DeviceName) ?? screen.DeviceName;
            bool isInternal = identity?.OutputTechnology is
                DisplayConfigVideoOutputTechnology.Internal or
                DisplayConfigVideoOutputTechnology.Lvds or
                DisplayConfigVideoOutputTechnology.DisplayPortEmbedded or
                DisplayConfigVideoOutputTechnology.UdiEmbedded;
            var center = new Native.POINT
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };
            uint dpi = Native.GetEffectiveMonitorDpi(
                Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST));
            result[screen.DeviceName] = new DisplaySnapshot(
                stableId, screen.DeviceName, screen.Bounds, screen.WorkingArea,
                dpi, isInternal, screen.Primary);
        }
        return result;
    }

    private sealed record PathIdentity(
        string MonitorDevicePath,
        DisplayConfigVideoOutputTechnology OutputTechnology);

    private static Dictionary<string, PathIdentity> QueryActivePathIdentities()
    {
        var result = new Dictionary<string, PathIdentity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int retry = 0; retry < 3; retry++)
            {
                int error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths,
                    out uint pathCount, out uint modeCount);
                if (error != ErrorSuccess) return result;

                var paths = new DisplayConfigPathInfo[pathCount];
                var modes = new DisplayConfigModeInfo[modeCount];
                error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths,
                    ref modeCount, modes, IntPtr.Zero);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != ErrorSuccess) return result;

                for (int i = 0; i < pathCount; i++)
                {
                    DisplayConfigPathInfo path = paths[i];
                    var source = new DisplayConfigSourceDeviceName
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = DisplayConfigDeviceInfoGetSourceName,
                            Size = Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                            AdapterId = path.SourceInfo.AdapterId,
                            Id = path.SourceInfo.Id
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref source) != ErrorSuccess ||
                        string.IsNullOrWhiteSpace(source.ViewGdiDeviceName))
                        continue;

                    var target = new DisplayConfigTargetDeviceName
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = DisplayConfigDeviceInfoGetTargetName,
                            Size = Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                            AdapterId = path.TargetInfo.AdapterId,
                            Id = path.TargetInfo.Id
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref target) != ErrorSuccess) continue;
                    result[source.ViewGdiDeviceName] = new PathIdentity(
                        target.MonitorDevicePath ?? string.Empty,
                        target.OutputTechnology);
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(nameof(DisplayTopology),
                $"DisplayConfig identity lookup failed; using GDI fallback identities: {ex.Message}");
        }
        return result;
    }

    private static string? GetLegacyStableId(string deviceName)
    {
        var adapter = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(null, index, ref adapter, 0); index++)
        {
            if (!string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                adapter = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
                continue;
            }

            var monitor = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0))
                return !string.IsNullOrWhiteSpace(monitor.DeviceKey)
                    ? monitor.DeviceKey
                    : monitor.DeviceId;
            return adapter.DeviceKey;
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags,
        out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    private enum DisplayConfigVideoOutputTechnology : uint
    {
        Other = 0xffffffff,
        Hd15 = 0,
        SVideo = 1,
        CompositeVideo = 2,
        ComponentVideo = 3,
        Dvi = 4,
        Hdmi = 5,
        Lvds = 6,
        DJack = 8,
        Sdi = 9,
        DisplayPortExternal = 10,
        DisplayPortEmbedded = 11,
        UdiExternal = 12,
        UdiEmbedded = 13,
        SdtvDongle = 14,
        Miracast = 15,
        Internal = 0x80000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public DisplayConfigVideoOutputTechnology OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigModeInfoUnion ModeInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DisplayConfigModeInfoUnion
    {
        [FieldOffset(0)] public ulong Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public int Type;
        public int Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public DisplayConfigVideoOutputTechnology OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
}
