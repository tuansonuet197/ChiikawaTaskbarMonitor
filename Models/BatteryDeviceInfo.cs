namespace UsagiBatteryMonitor.Models;

public enum DeviceCategory
{
    Mouse,
    Keyboard,
    Audio,
    Gamepad,
    System,
    Other
}

public sealed record BatteryDeviceInfo(
    string Id,
    string Name,
    int BatteryPercent,
    DeviceCategory Category,
    string Transport,
    bool IsCharging,
    bool IsConnected = true,
    bool IsCachedSleep = false,
    int? VoltageMv = null,
    DateTime LastSeenUtc = default)
{
    public string CategoryLabel => Category switch
    {
        DeviceCategory.Mouse => "Mouse",
        DeviceCategory.Keyboard => "Keyboard",
        DeviceCategory.Audio => "Audio",
        DeviceCategory.Gamepad => "Gamepad",
        DeviceCategory.System => "Laptop",
        _ => "Device"
    };
}
