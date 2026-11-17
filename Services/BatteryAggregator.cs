using UsagiBatteryMonitor.Models;

namespace UsagiBatteryMonitor.Services;

public sealed class BatteryAggregator
{
    private readonly HidWirelessBatteryProvider _hidProvider = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<BatteryDeviceInfo>> RefreshAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pnpTask = Task.Run(() => PnpBluetoothBatteryProvider.Scan(settings.ShowDisconnectedBluetooth), ct);
            var bleTask = BleBatteryProvider.ScanAsync(ct);
            var hidTask = _hidProvider.ScanAsync(ct);

            await Task.WhenAll(pnpTask, bleTask, hidTask).ConfigureAwait(false);

            var merged = new Dictionary<string, BatteryDeviceInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. Live 2.4GHz HID devices first (highest accuracy & voltage info)
            foreach (var d in hidTask.Result)
                merged[NormalizeNameKey(d.Name)] = d;

            // 2. Live BLE GATT devices
            foreach (var d in bleTask.Result)
            {
                string key = NormalizeNameKey(d.Name);
                if (!merged.ContainsKey(key))
                    merged[key] = d;
            }

            // 3. PnP Bluetooth battery devices
            foreach (var d in pnpTask.Result)
            {
                string key = NormalizeNameKey(d.Name);
                if (!merged.ContainsKey(key))
                    merged[key] = d;
            }

            var list = merged.Values
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.Category)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 4. Optional Laptop / System Battery
            if (settings.ShowSystemBattery)
            {
                var ps = SystemInformation.PowerStatus;
                if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f)
                {
                    int pct = Math.Clamp((int)Math.Round(ps.BatteryLifePercent * 100f), 0, 100);
                    bool charging = ps.PowerLineStatus == PowerLineStatus.Online &&
                                    (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                    list.Add(new BatteryDeviceInfo(
                        Id: "sys:battery",
                        Name: "This PC (Battery)",
                        BatteryPercent: pct,
                        Category: DeviceCategory.System,
                        Transport: ps.PowerLineStatus == PowerLineStatus.Online ? "AC" : "Internal",
                        IsCharging: charging,
                        IsConnected: true,
                        LastSeenUtc: DateTime.UtcNow));
                }
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeNameKey(string name)
    {
        return name
            .Replace(" mouse", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" keyboard", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
