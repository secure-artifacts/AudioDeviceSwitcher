using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher;

public record DeviceProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? PlaybackDeviceId { get; set; }
    public string? PlaybackDeviceName { get; set; }
    public string? RecordingDeviceId { get; set; }
    public string? RecordingDeviceName { get; set; }
    public int HotkeyModifiers { get; set; }
    public int HotkeyKey { get; set; }
    public List<AppOverride> AppOverrides { get; set; } = [];
    public bool RestartVoicemeeterAfterApply { get; set; }
    public bool ShowInMiniWindow { get; set; } = true;
    public int Order { get; set; }
    public string? Color { get; set; }
}

public record AppOverride
{
    public string ExePath { get; set; } = "";
    public Guid AppProfileId { get; set; }
}

public static class ProfileService
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioDeviceSwitcher");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<DeviceProfile> GetAll()
    {
        // Crash-safe load: recovers from .bak if profiles.json is corrupt (see JsonStore).
        var list = JsonStore.Read<List<DeviceProfile>>(ProfilePath, JsonOptions) ?? [];
        // Stable sort: profiles without an explicit Order keep their JSON order.
        return list.OrderBy(p => p.Order).ToList();
    }

    public static void Save(DeviceProfile profile)
    {
        var profiles = GetAll();
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            profiles[index] = profile;
        else
        {
            profile.Order = profiles.Count == 0 ? 1 : profiles.Max(p => p.Order) + 1;
            profiles.Add(profile);
        }

        WriteAll(profiles);
    }

    public static void SaveAll(List<DeviceProfile> profiles)
    {
        WriteAll(profiles);
    }

    public static void Delete(Guid id)
    {
        var profiles = GetAll();
        profiles.RemoveAll(p => p.Id == id);
        WriteAll(profiles);
    }

    private static void WriteAll(List<DeviceProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        JsonStore.WriteAtomic(ProfilePath, json);
    }
}
