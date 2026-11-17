using System.Runtime.InteropServices;
using System.Text;
using UsagiBatteryMonitor.Models;

namespace UsagiBatteryMonitor.Services;

public static class PnpBluetoothBatteryProvider
{
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_BOOLEAN = 0x00000011;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[]? PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static readonly DEVPROPKEY PkeyBtBattery = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    private static readonly DEVPROPKEY PkeyFriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
    private static readonly DEVPROPKEY PkeyDeviceDesc = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 2 };
    private static readonly DEVPROPKEY PkeyName = new() { fmtid = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), pid = 10 };
    private static readonly DEVPROPKEY PkeyIsConnected = new() { fmtid = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), pid = 15 };
    private static readonly DEVPROPKEY PkeyContainerId = new() { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };
    private static readonly DEVPROPKEY PkeyInstanceId = new() { fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256 };

    public static List<BatteryDeviceInfo> Scan(bool includeDisconnected)
    {
        var byContainer = new Dictionary<string, BatteryDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        IntPtr hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            return [];

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var byteBuf = new byte[32];
            var strBuf = new byte[512];

            for (uint i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfo); i++)
            {
                var kBatt = PkeyBtBattery;
                if (!SetupDiGetDeviceProperty(hDevInfo, ref devInfo, ref kBatt, out uint pt, byteBuf, (uint)byteBuf.Length, out uint req, 0)
                    || pt != DEVPROP_TYPE_BYTE || req < 1)
                    continue;

                int battery = byteBuf[0];
                if (battery > 100) continue;

                bool isConnected = ReadBool(hDevInfo, ref devInfo, PkeyIsConnected, byteBuf) ?? false;
                if (!includeDisconnected && !isConnected)
                    continue;

                string rawName = ReadString(hDevInfo, ref devInfo, PkeyFriendlyName, strBuf)
                    ?? ReadString(hDevInfo, ref devInfo, PkeyName, strBuf)
                    ?? ReadString(hDevInfo, ref devInfo, PkeyDeviceDesc, strBuf)
                    ?? "Bluetooth Device";

                string cleanName = CleanBluetoothName(rawName);
                string instanceId = ReadString(hDevInfo, ref devInfo, PkeyInstanceId, strBuf) ?? cleanName;
                Guid? containerGuid = ReadGuid(hDevInfo, ref devInfo, PkeyContainerId, byteBuf);
                string key = containerGuid.HasValue && containerGuid.Value != Guid.Empty
                    ? containerGuid.Value.ToString("N")
                    : cleanName;

                string transport = instanceId.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) ? "BLE" : "Bluetooth";
                var category = GuessCategory(rawName, cleanName, instanceId);

                var info = new BatteryDeviceInfo(
                    Id: $"pnp:{key}",
                    Name: cleanName,
                    BatteryPercent: battery,
                    Category: category,
                    Transport: transport,
                    IsCharging: false,
                    IsConnected: isConnected,
                    LastSeenUtc: DateTime.UtcNow);

                // Prefer connected entry or entry with clearer name
                if (!byContainer.TryGetValue(key, out var existing) || (!existing.IsConnected && info.IsConnected))
                {
                    byContainer[key] = info;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return byContainer.Values.ToList();
    }

    private static string CleanBluetoothName(string name)
    {
        return name
            .Replace(" Hands-Free AG", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Hands-Free", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Avrcp Transport", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static DeviceCategory GuessCategory(string rawName, string cleanName, string instanceId)
    {
        string s = $"{rawName} {cleanName} {instanceId}".ToLowerInvariant();
        if (s.Contains("mouse") || s.Contains("vxe") || s.Contains("superlight") || s.Contains("viper") || s.Contains("deathadder") || s.Contains("g304") || s.Contains("g502") || s.Contains("mx master"))
            return DeviceCategory.Mouse;
        if (s.Contains("keyboard") || s.Contains("keychron") || s.Contains("aula") || s.Contains("leobog") || s.Contains("k380") || s.Contains("mx keys") || s.Contains("rk"))
            return DeviceCategory.Keyboard;
        if (s.Contains("xbox") || s.Contains("dualsense") || s.Contains("dualshock") || s.Contains("controller") || s.Contains("gamepad"))
            return DeviceCategory.Gamepad;
        if (s.Contains("hands-free") || s.Contains("bthhfenum") || s.Contains("airpods") || s.Contains("buds") || s.Contains("wh-") || s.Contains("wf-")
            || s.Contains("jbl") || s.Contains("sudio") || s.Contains("soundpeats") || s.Contains("baseus") || s.Contains("borofone")
            || s.Contains("headphone") || s.Contains("headset") || s.Contains("earphone") || s.Contains("speaker") || s.Contains("lg-pl"))
            return DeviceCategory.Audio;
        return DeviceCategory.Other;
    }

    private static string? ReadString(IntPtr h, ref SP_DEVINFO_DATA d, DEVPROPKEY k, byte[] buf)
    {
        if (SetupDiGetDeviceProperty(h, ref d, ref k, out uint pt, buf, (uint)buf.Length, out uint req, 0)
            && pt == DEVPROP_TYPE_STRING && req >= 2)
        {
            return Encoding.Unicode.GetString(buf, 0, (int)req - 2).TrimEnd('\0');
        }
        return null;
    }

    private static bool? ReadBool(IntPtr h, ref SP_DEVINFO_DATA d, DEVPROPKEY k, byte[] buf)
    {
        if (SetupDiGetDeviceProperty(h, ref d, ref k, out uint pt, buf, (uint)buf.Length, out uint req, 0)
            && pt == DEVPROP_TYPE_BOOLEAN && req >= 1)
        {
            return buf[0] != 0;
        }
        return null;
    }

    private static Guid? ReadGuid(IntPtr h, ref SP_DEVINFO_DATA d, DEVPROPKEY k, byte[] buf)
    {
        if (SetupDiGetDeviceProperty(h, ref d, ref k, out uint pt, buf, (uint)buf.Length, out uint req, 0)
            && pt == DEVPROP_TYPE_GUID && req == 16)
        {
            return new Guid(buf.AsSpan(0, 16));
        }
        return null;
    }
}
