using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UsagiBatteryMonitor.Models;

namespace UsagiBatteryMonitor.Services;

public sealed class HidWirelessBatteryProvider
{
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr ppd);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr ppd);
    [DllImport("hid.dll", SetLastError = true)] private static extern int HidP_GetCaps(IntPtr ppd, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] data, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detailData, uint detailSize, out uint requiredSize, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    private readonly ConcurrentDictionary<string, BatteryDeviceInfo> _sleepCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record HidCandidate(string Path, ushort Vid, ushort Pid, string ProductName, HIDP_CAPS Caps);

    public async Task<List<BatteryDeviceInfo>> ScanAsync(CancellationToken ct)
    {
        var candidates = EnumerateSupportedInterfaces();
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<BatteryDeviceInfo>();

        var tasks = candidates.Select(c => QueryCandidateAsync(c, ct));
        var queried = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (cand, info) in queried)
        {
            string cacheKey = $"{cand.Vid:X4}:{cand.Pid:X4}";
            activeKeys.Add(cacheKey);
            if (info != null)
            {
                _sleepCache[cacheKey] = info;
                results.Add(info);
            }
            else if (_sleepCache.TryGetValue(cacheKey, out var cached))
            {
                // Receiver still plugged in, peripheral likely sleeping -> show last known %
                results.Add(cached with { IsCachedSleep = true });
            }
        }

        // Prune unplugged dongles from sleep cache
        foreach (var k in _sleepCache.Keys)
        {
            if (!activeKeys.Contains(k))
                _sleepCache.TryRemove(k, out _);
        }

        return results;
    }

    private static List<HidCandidate> EnumerateSupportedInterfaces()
    {
        var list = new List<HidCandidate>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hInfo == IntPtr.Zero || hInfo == new IntPtr(-1))
            return list;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            var seenVidPid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref hidGuid, i, ref did); i++)
            {
                SetupDiGetDeviceInterfaceDetail(hInfo, ref did, IntPtr.Zero, 0, out uint req, IntPtr.Zero);
                if (req == 0) continue;

                IntPtr buf = Marshal.AllocHGlobal((int)req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref did, buf, req, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (string.IsNullOrEmpty(path)) continue;

                    // Target vendor HID interfaces for Compx / Yizhita / VXE / AULA / ATK (0x3554, 0x373B)
                    bool isKnownVendor = path.Contains("vid_3554", StringComparison.OrdinalIgnoreCase)
                                      || path.Contains("vid_373b", StringComparison.OrdinalIgnoreCase);
                    if (!isKnownVendor) continue;

                    using var fh = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (fh.IsInvalid) continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(fh, ref attrs)) continue;

                    if (!HidD_GetPreparsedData(fh, out IntPtr ppd)) continue;
                    int capRes = HidP_GetCaps(ppd, out HIDP_CAPS caps);
                    HidD_FreePreparsedData(ppd);
                    if (capRes <= 0) continue;

                    // Vendor collection UsagePage 0xFF02, Usage 0x0002 with 17 or 20 byte output report
                    if (caps.UsagePage == 0xFF02 && caps.Usage == 0x0002 &&
                        (caps.OutputReportByteLength == 17 || caps.OutputReportByteLength == 20))
                    {
                        string key = $"{attrs.VendorID:X4}:{attrs.ProductID:X4}:{caps.OutputReportByteLength}";
                        if (!seenVidPid.Add(key)) continue;

                        byte[] pbuf = new byte[256];
                        string prod = HidD_GetProductString(fh, pbuf, pbuf.Length)
                            ? Encoding.Unicode.GetString(pbuf).TrimEnd('\0')
                            : $"Wireless Device ({attrs.VendorID:X4}:{attrs.ProductID:X4})";

                        list.Add(new HidCandidate(path, attrs.VendorID, attrs.ProductID, prod.Trim(), caps));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hInfo);
        }

        return list;
    }

    private static async Task<(HidCandidate, BatteryDeviceInfo?)> QueryCandidateAsync(HidCandidate cand, CancellationToken parentCt)
    {
        try
        {
            using var fh = CreateFile(
                cand.Path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (fh.IsInvalid)
                return (cand, null);

            if (cand.Caps.OutputReportByteLength == 17 && cand.Caps.InputReportByteLength >= 17)
            {
                var info = await QueryCompx17Async(fh, cand, parentCt).ConfigureAwait(false);
                return (cand, info);
            }

            if (cand.Caps.OutputReportByteLength == 20 && cand.Caps.InputReportByteLength >= 20)
            {
                var info = await QueryCompx20KeyboardAsync(fh, cand, parentCt).ConfigureAwait(false);
                return (cand, info);
            }
        }
        catch { }

        return (cand, null);
    }

    // 17-byte Compx/ATK/VXE mouse protocol: [0x08, 0x04, ..., 0x49]
    private static async Task<BatteryDeviceInfo?> QueryCompx17Async(SafeFileHandle fh, HidCandidate cand, CancellationToken parentCt)
    {
        var pkt = new byte[17];
        pkt[0] = 0x08;
        pkt[1] = 0x04;
        pkt[16] = 0x49;

        using var fs = new FileStream(fh, FileAccess.ReadWrite, 17, isAsync: true);
        await fs.WriteAsync(pkt, parentCt).ConfigureAwait(false);
        await fs.FlushAsync(parentCt).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(380);

        var resp = new byte[cand.Caps.InputReportByteLength];
        // Read up to 4 reports in case mouse movement reports arrive first
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int n = await fs.ReadAsync(resp, cts.Token).ConfigureAwait(false);
            if (n >= 10 && resp[0] == 0x08 && resp[1] == 0x04)
            {
                int pct = resp[6];
                if (pct is >= 0 and <= 100)
                {
                    bool charging = resp[7] != 0;
                    int mv = (resp[8] << 8) | resp[9];
                    string friendly = BeautifyHidName(cand.ProductName, DeviceCategory.Mouse);
                    return new BatteryDeviceInfo(
                        Id: $"hid:{cand.Vid:X4}:{cand.Pid:X4}",
                        Name: friendly,
                        BatteryPercent: pct,
                        Category: DeviceCategory.Mouse,
                        Transport: "2.4GHz",
                        IsCharging: charging,
                        IsConnected: true,
                        IsCachedSleep: false,
                        VoltageMv: mv > 2000 && mv < 5000 ? mv : null,
                        LastSeenUtc: DateTime.UtcNow);
                }
            }
        }
        return null;
    }

    // 20-byte Compx/AULA/LEOBOG keyboard protocol: [0x13, 0x4A, ..., sum(0..18)&0xFF]
    private static async Task<BatteryDeviceInfo?> QueryCompx20KeyboardAsync(SafeFileHandle fh, HidCandidate cand, CancellationToken parentCt)
    {
        var pkt = new byte[20];
        pkt[0] = 0x13;
        pkt[1] = 0x4A;
        int sum = 0;
        for (int i = 0; i < 19; i++) sum += pkt[i];
        pkt[19] = (byte)(sum & 0xFF);

        using var fs = new FileStream(fh, FileAccess.ReadWrite, 20, isAsync: true);
        if (!HidD_SetOutputReport(fh, pkt, 20))
        {
            await fs.WriteAsync(pkt, parentCt).ConfigureAwait(false);
            await fs.FlushAsync(parentCt).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(450);

        var resp = new byte[cand.Caps.InputReportByteLength];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int n = await fs.ReadAsync(resp, cts.Token).ConfigureAwait(false);
            if (n >= 7 && resp[0] == 0x13 && (resp[1] & 0x7F) == 0x4A)
            {
                int pct = resp[5];
                if (pct is >= 0 and <= 100)
                {
                    bool charging = resp[6] == 0x10 || resp[6] == 0x02;
                    string friendly = BeautifyHidName(cand.ProductName, DeviceCategory.Keyboard);
                    return new BatteryDeviceInfo(
                        Id: $"hid:{cand.Vid:X4}:{cand.Pid:X4}",
                        Name: friendly,
                        BatteryPercent: pct,
                        Category: DeviceCategory.Keyboard,
                        Transport: "2.4GHz",
                        IsCharging: charging,
                        IsConnected: true,
                        IsCachedSleep: false,
                        LastSeenUtc: DateTime.UtcNow);
                }
            }
        }
        return null;
    }

    private static string BeautifyHidName(string raw, DeviceCategory cat)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("2.4G Wireless Receiver", StringComparison.OrdinalIgnoreCase))
            return cat == DeviceCategory.Keyboard ? "2.4G Wireless Keyboard" : "2.4G Wireless Mouse";

        return raw
            .Replace(" 1K Dongle", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" 4K Dongle", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" 8K Dongle", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Dongle", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
