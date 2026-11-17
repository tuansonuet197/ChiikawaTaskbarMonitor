using System.Text.Json;

namespace UsagiBatteryMonitor.Services;

public sealed class AppSettings
{
    public bool ShowTaskbarRunner { get; set; } = true;
    public bool ShowDisconnectedBluetooth { get; set; } = false;
    public bool ShowSystemBattery { get; set; } = true;
    public bool LowBatteryAlertEnabled { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 30;
    public bool HealthRemindersEnabled { get; set; } = true;
    public int EyeBreakMinutes { get; set; } = 20;
    public int HydrationBreakMinutes { get; set; } = 40;
    public int StretchBreakMinutes { get; set; } = 60;
}

public static class SettingsStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsagiBatteryMonitor");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static bool Exists() => File.Exists(ConfigPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { }
    }
}
