using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher;

public record AppProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }
    public string? InputDeviceId { get; set; }
    public string? InputDeviceName { get; set; }
}

public static class AppProfileService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioDeviceSwitcher");

    private static readonly string FilePath = Path.Combine(Dir, "app-profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<AppProfile> GetAll()
    {
        // Crash-safe load: recovers from .bak if app-profiles.json is corrupt (see JsonStore).
        return JsonStore.Read<List<AppProfile>>(FilePath, JsonOptions) ?? [];
    }

    public static AppProfile? Get(Guid id) => GetAll().Find(p => p.Id == id);

    public static void Save(AppProfile profile)
    {
        var all = GetAll();
        var i = all.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) all[i] = profile;
        else all.Add(profile);
        WriteAll(all);
    }

    public static void Delete(Guid id)
    {
        var all = GetAll();
        all.RemoveAll(p => p.Id == id);
        WriteAll(all);
    }

    private static void WriteAll(List<AppProfile> profiles)
    {
        JsonStore.WriteAtomic(FilePath, JsonSerializer.Serialize(profiles, JsonOptions));
    }
}
