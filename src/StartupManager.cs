using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IndepenDesk;

/// <summary>
/// Windows ile otomatik başlatma. Varsayılan açıktır; tray menüsünden kapatılabilir.
/// Normal kurulumlarda HKCU\...\Run anahtarı kullanılır. MSIX paketinde bu anahtar
/// sanallaştırıldığı için kayıt defterine dokunulmaz; başlangıç, manifest'teki
/// StartupTask ile gelir ve Windows Ayarları > Uygulamalar > Başlangıç'tan yönetilir.
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IndepenDesk";

    public static readonly bool IsPackaged = DetectPackaged();

    public static bool Enabled { get; private set; } = SettingsStore.GetBool("startup", true);

    /// <summary>Uygulama açılışında çağrılır: tercih neyse kayıt defterini ona eşitler
    /// (exe taşınmışsa yol da tazelenir).</summary>
    public static bool ApplyOnLaunch()
    {
        return IsPackaged || Apply(Enabled);
    }

    public static bool SetEnabled(bool enabled)
    {
        if (IsPackaged) return false;

        bool wasRegistered = IsRegistered();
        if (!Apply(enabled)) return false;
        if (!SettingsStore.SetBool("startup", enabled))
        {
            Apply(wasRegistered);
            return false;
        }

        Enabled = enabled;
        return true;
    }

    public static bool IsRegistered()
    {
        if (IsPackaged) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            string? actual = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string;
            return Environment.ProcessPath is { } exe &&
                   string.Equals(actual, $"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(IsRegistered), ex);
            return false;
        }
    }

    private static bool Apply(bool enabled)
    {
        try
        {
            if (enabled && Environment.ProcessPath == null)
            {
                AppLog.Warning(nameof(Apply), "The executable path is unavailable.");
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null)
            {
                AppLog.Warning(nameof(Apply), "Could not open the current-user Run key.");
                return false;
            }
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return IsRegistered() == enabled;
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(Apply), ex);
            return false;
        }
    }

    /// <summary>MSIX'te geçiş Windows Ayarları'ndan yapılır; Başlangıç sayfasını açar.</summary>
    public static void OpenWindowsStartupSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:startupapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(OpenWindowsStartupSettings), ex);
        }
    }

    private static bool DetectPackaged()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
        }
        catch
        {
            return false; // Windows 7 vb.: API yok => paketsiz
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
