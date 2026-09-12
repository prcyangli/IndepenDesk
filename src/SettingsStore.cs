using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace IndepenDesk;

/// <summary>
/// settings.json okuma/yazma. Yazarken dosyadaki diğer anahtarları korur
/// (dil tercihi ile başlangıç tercihi aynı dosyayı paylaşır).
/// </summary>
internal static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndepenDesk", "settings.json");

    public static string? GetString(string key)
    {
        try
        {
            if (File.Exists(File_) &&
                JsonNode.Parse(File.ReadAllText(File_)) is JsonObject obj &&
                obj[key] is JsonValue v)
                return v.GetValue<string>();
        }
        catch (Exception ex)
        {
            AppLog.Error($"{nameof(GetString)}({key})", ex);
        }
        return null;
    }

    public static bool GetBool(string key, bool defaultValue)
    {
        try
        {
            if (File.Exists(File_) &&
                JsonNode.Parse(File.ReadAllText(File_)) is JsonObject obj &&
                obj[key] is JsonValue v)
                return v.GetValue<bool>();
        }
        catch (Exception ex)
        {
            AppLog.Error($"{nameof(GetBool)}({key})", ex);
        }
        return defaultValue;
    }

    public static bool SetString(string key, string value) => Set(key, JsonValue.Create(value));

    public static bool SetBool(string key, bool value) => Set(key, JsonValue.Create(value));

    private static bool Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            string tmp = File_ + $".{Environment.ProcessId}.tmp";
            try
            {
                JsonObject obj;
                try
                {
                    obj = (File.Exists(File_) ? JsonNode.Parse(File.ReadAllText(File_)) as JsonObject : null)
                          ?? new JsonObject();
                }
                catch (Exception ex)
                {
                    AppLog.Error(nameof(SettingsStore) + ".Parse", ex);
                    obj = new JsonObject();
                }

                obj[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
                string json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(File_))
                    File.Replace(tmp, File_, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, File_);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error($"{nameof(SettingsStore)}.Set({key})", ex);
                try { File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
