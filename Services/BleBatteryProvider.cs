using UsagiBatteryMonitor.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace UsagiBatteryMonitor.Services;

public static class BleBatteryProvider
{
    public static async Task<List<BatteryDeviceInfo>> ScanAsync(CancellationToken ct)
    {
        var results = new List<BatteryDeviceInfo>();
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);
            if (devices.Count == 0)
                return results;

            var tasks = devices.Select(d => ReadSingleDeviceAsync(d, ct));
            var items = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (item != null) results.Add(item);
            }
        }
        catch { }
        return results;
    }

    private static async Task<BatteryDeviceInfo?> ReadSingleDeviceAsync(DeviceInformation devInfo, CancellationToken parentCt)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(2200));
        var ct = timeoutCts.Token;

        try
        {
            using var ble = await BluetoothLEDevice.FromIdAsync(devInfo.Id).AsTask(ct).ConfigureAwait(false);
            if (ble == null || ble.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var gatt = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);
            if (gatt.Status != GattCommunicationStatus.Success || gatt.Services.Count == 0)
                return null;

            using var service = gatt.Services[0];
            var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                return null;

            var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
            if (read.Status != GattCommunicationStatus.Success || read.Value == null || read.Value.Length == 0)
                return null;

            using var reader = DataReader.FromBuffer(read.Value);
            byte pct = reader.ReadByte();
            if (pct > 100) return null;

            string name = string.IsNullOrWhiteSpace(ble.Name) ? devInfo.Name : ble.Name;
            var cat = PnpBluetoothBatteryProvider.GuessCategory(name, name, devInfo.Id);

            return new BatteryDeviceInfo(
                Id: $"ble:{ble.BluetoothAddress:X12}",
                Name: name.Trim(),
                BatteryPercent: pct,
                Category: cat,
                Transport: "BLE",
                IsCharging: false,
                IsConnected: true,
                LastSeenUtc: DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }
}
