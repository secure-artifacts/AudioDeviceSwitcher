using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher;

// Import/export of user configuration as a single JSON backup file.
// Bundles device profiles + app presets + device nicknames, because a profile's
// AppOverrides reference app presets by id — they must travel together to restore intact.
public static class BackupService
{
    public class Backup
    {
        public int FormatVersion { get; set; } = 1;
        public string? AppVersion { get; set; }
        public string? ExportedAt { get; set; }
        public List<DeviceProfile> Profiles { get; set; } = [];
        public List<AppProfile> AppProfiles { get; set; } = [];
        public Dictionary<string, string> DeviceNicknames { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Export(string path)
    {
        var backup = new Backup
        {
            AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(),
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Profiles = ProfileService.GetAll(),
            AppProfiles = AppProfileService.GetAll(),
            DeviceNicknames = new Dictionary<string, string>(SettingsService.Load().DeviceNicknames),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(backup, JsonOptions));
    }

    // Merge import: same-id entries are overwritten, new ones appended; nicknames merged by key.
    // Returns counts for a summary message. Throws on an unreadable / wrong-format file.
    public static (int Profiles, int AppProfiles, int Nicknames) ImportMerge(string path)
    {
        var json = File.ReadAllText(path);
        var backup = JsonSerializer.Deserialize<Backup>(json, JsonOptions)
            ?? throw new InvalidDataException("文件内容为空或格式不正确");
        if (backup.FormatVersion <= 0)
            throw new InvalidDataException("无法识别的备份文件版本");

        // App presets first, so profiles referencing them resolve after import.
        foreach (var ap in backup.AppProfiles)
            AppProfileService.Save(ap);

        var profiles = ProfileService.GetAll();
        foreach (var p in backup.Profiles)
        {
            var i = profiles.FindIndex(x => x.Id == p.Id);
            if (i >= 0) profiles[i] = p;
            else profiles.Add(p);
        }
        ProfileService.SaveAll(profiles);

        if (backup.DeviceNicknames.Count > 0)
        {
            var settings = SettingsService.Load();
            foreach (var kv in backup.DeviceNicknames)
                settings.DeviceNicknames[kv.Key] = kv.Value;
            SettingsService.Save();
        }

        return (backup.Profiles.Count, backup.AppProfiles.Count, backup.DeviceNicknames.Count);
    }
}
