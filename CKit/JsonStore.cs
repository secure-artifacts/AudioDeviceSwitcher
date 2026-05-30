using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher;

// Crash-safe JSON persistence shared by ProfileService / SettingsService.
//   • WriteAtomic — write to a temp file then swap in, keeping the previous good copy as .bak.
//     A crash/power-loss mid-write leaves either the old file or the new one, never a half file.
//   • Read — load the primary file; if it's corrupt, recover from .bak. An unrecoverable
//     primary is moved aside to <name>.corrupt-<timestamp> (never silently overwritten),
//     so the next write starts clean instead of clobbering whatever was there.
public static class JsonStore
{
    public static void WriteAtomic(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    // Returns default(T) (null for reference types) when nothing usable exists.
    public static T? Read<T>(string path, JsonSerializerOptions options)
    {
        if (TryReadFile<T>(path, options, out var value)) return value;

        bool primaryExists = File.Exists(path);
        var bak = path + ".bak";
        if (File.Exists(bak) && TryReadFile<T>(bak, options, out var bakValue))
        {
            if (primaryExists) PreserveCorrupt(path); // keep the bad copy, clear the slot
            try { File.Copy(bak, path); } catch { }   // restore the good content
            return bakValue;
        }

        // Primary unusable and no good backup — preserve it so it isn't silently overwritten.
        if (primaryExists) PreserveCorrupt(path);
        return default;
    }

    private static bool TryReadFile<T>(string path, JsonSerializerOptions options, out T? value)
    {
        value = default;
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            value = JsonSerializer.Deserialize<T>(json, options);
            return value != null;
        }
        catch { return false; }
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dest = $"{path}.corrupt-{stamp}";
            if (File.Exists(dest)) dest = $"{path}.corrupt-{stamp}-{Guid.NewGuid():N}";
            File.Move(path, dest); // moves the corrupt file aside (path no longer exists after)
        }
        catch { }
    }
}
