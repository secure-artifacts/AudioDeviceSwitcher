using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher;

public class AppSettings
{
    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }
    public double? MiniWindowLeft { get; set; }
    public double? MiniWindowTop { get; set; }
    public double MiniWindowOpacity { get; set; } = 1.0;
    public bool MiniWindowVisible { get; set; }
    public List<string> HiddenDeviceIds { get; set; } = [];
    public bool ShowHiddenDevices { get; set; }
    public bool ShowDisabledDevices { get; set; }
    public Dictionary<string, string> DeviceNicknames { get; set; } = [];

    public bool NotifyProfileApplied { get; set; } = true;
    public bool NotifyDeviceChanged { get; set; } = true;
    public bool NotifyBluetooth { get; set; } = true;
    public bool NotifyAppDrift { get; set; } = true;
    public bool EnableBlinkAnimation { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public Guid? LockedProfileId { get; set; }
    public bool VoicemeeterMuteLocked { get; set; }
    public List<bool> VoicemeeterStripMuteSnapshot { get; set; } = [];
    // Device-routing snapshots taken at lock time, by slot index. Empty string = the
    // slot had no device assigned (not enforced — we never force-clear a routing).
    public List<string> VoicemeeterStripDeviceSnapshot { get; set; } = [];
    public List<string> VoicemeeterBusDeviceSnapshot { get; set; } = [];
    // Last device the user picked per slot, so the select menu can check the right
    // driver after a restart (Voicemeeter's API can't report the active driver type).
    // Key "S<idx>"/"B<idx>", value "<driverType>|<deviceName>".
    public Dictionary<string, string> VoicemeeterDevicePicks { get; set; } = [];
    public bool VoicemeeterIntegrationEnabled { get; set; } = false;
}

public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioDeviceSwitcher");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static AppSettings? _cached;

    public static AppSettings Load()
    {
        if (_cached != null) return _cached;
        // Crash-safe load: a corrupt settings.json is recovered from .bak (or preserved as
        // .corrupt-* and reset), instead of being silently replaced with defaults (see JsonStore).
        _cached = JsonStore.Read<AppSettings>(SettingsPath, JsonOptions) ?? new AppSettings();
        return _cached;
    }

    public static void Save()
    {
        if (_cached == null) return;
        var json = JsonSerializer.Serialize(_cached, JsonOptions);
        JsonStore.WriteAtomic(SettingsPath, json);
    }
}
