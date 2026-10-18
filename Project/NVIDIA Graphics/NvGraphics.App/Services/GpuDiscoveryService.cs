using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>
/// Enumerates display adapters from Windows device information (WMI Win32_VideoController,
/// PCI bus location via SetupAPI SPDRP_LOCATION_INFORMATION with a WMI fallback).
/// NVIDIA GPU indexes are NOT used for identity — the stable PnP adapter id is.
/// </summary>
public sealed class GpuDiscoveryService
{
    /// <summary>
    /// Extracts the SpecificAdapter identity "VEN_xxxx&amp;DEV_xxxx&amp;SUBSYS_yyyyyyyy" from a
    /// PNPDeviceID such as PCI\VEN_10DE&amp;DEV_1B06&amp;SUBSYS_85F11043&amp;REV_A1\4&amp;...&amp;0&amp;0008.
    /// Returns null for non-PCI adapters (ROOT\..., SWD\...).
    /// </summary>
    public static string ParseAdapterId(string pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) ||
            !pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            return null;

        var seg = pnpDeviceId.Substring(4);
        var cut = seg.IndexOf('\\');
        if (cut >= 0) seg = seg.Substring(0, cut);
        var parts = seg.Split('&');
        string ven = null, dev = null, subsys = null;
        foreach (var p in parts)
        {
            if (p.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) && ven is null) ven = p;
            else if (p.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) && dev is null) dev = p;
            else if (p.StartsWith("SUBSYS_", StringComparison.OrdinalIgnoreCase) && subsys is null) subsys = p;
        }
        if (ven is null || dev is null) return null;
        return subsys is null ? $"{ven}&{dev}" : $"{ven}&{dev}&{subsys}";
    }

    /// <summary>Converts "PCI bus 1, device 0, function 0" to the nvidia-smi form 00000000:01:00.0.</summary>
    public static string PciLocationToBusId(string pciLocation)
    {
        if (string.IsNullOrWhiteSpace(pciLocation)) return null;
        var digits = new List<int>();
        foreach (var part in pciLocation.Split(','))
        {
            var s = part.Trim();
            int sp = s.LastIndexOf(' ');
            if (sp < 0 || !int.TryParse(s.Substring(sp + 1), out var n)) return null;
            digits.Add(n);
        }
        if (digits.Count != 3) return null;
        return $"00000000:{digits[0]:X2}:{digits[1]:X2}.{digits[2]:X}";
    }

    public List<GpuAdapterInfo> DiscoverAdapters()
    {
        var locationByPnpId = QuerySetupApiLocations();
        foreach (var kv in QueryWmiLocations())
            locationByPnpId.TryAdd(kv.Key, kv.Value);
        var primaryDisplayAdapters = QueryPrimaryDisplayAdapterIds();

        var result = new List<GpuAdapterInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name,PNPDeviceID,DriverVersion,Status,AdapterCompatibility FROM Win32_VideoController");
        foreach (ManagementObject mo in searcher.Get())
        {
            var pnpId = mo["PNPDeviceID"] as string;
            var info = new GpuAdapterInfo
            {
                DisplayName = mo["Name"] as string ?? "(unknown adapter)",
                Vendor = mo["AdapterCompatibility"] as string ?? "",
                PnpDeviceId = pnpId,
                AdapterId = ParseAdapterId(pnpId),
                DriverVersion = mo["DriverVersion"] as string ?? "",
                Status = mo["Status"] as string ?? "",
                IsSystemDefaultDisplayAdapter = pnpId is not null &&
                    primaryDisplayAdapters.Contains(GetDeviceHardwareId(pnpId)),
                PciLocation = pnpId is null ? null : locationByPnpId.GetValueOrDefault(pnpId)
            };
            info.PciBusId = PciLocationToBusId(info.PciLocation);
            result.Add(info);
        }
        return result;
    }

    private static HashSet<string> QueryPrimaryDisplayAdapterIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
                if (!Native.EnumDisplayDevices(null, index, ref device, 0)) break;
                if ((device.StateFlags & 0x4) != 0 && !string.IsNullOrWhiteSpace(device.DeviceId))
                    ids.Add(GetDeviceHardwareId(device.DeviceId));
            }
        }
        catch (DllNotFoundException)
        {
            // Windows display APIs are unavailable; no adapter is reported as the default.
        }
        return ids;
    }

    private static string GetDeviceHardwareId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "";
        var firstSeparator = deviceId.IndexOf('\\');
        var instanceSeparator = deviceId.LastIndexOf('\\');
        return instanceSeparator > firstSeparator
            ? deviceId.Substring(0, instanceSeparator)
            : deviceId;
    }

    /// <summary>
    /// Primary PCI-location source: SetupAPI over the present Display class devices.
    /// SPDRP_LOCATION_INFORMATION (13) — the same string shown in Device Manager.
    /// </summary>
    private static Dictionary<string, string> QuerySetupApiLocations()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Guid display = new("4d36e968-e325-11ce-bfc1-08002be10318");
            IntPtr set = Native.SetupDiGetClassDevs(ref display, IntPtr.Zero, IntPtr.Zero, 0x2 /*DIGCF_PRESENT*/);
            if (set == new IntPtr(-1)) return map;
            try
            {
                var data = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var idBuf = new StringBuilder(512);
                    if (Native.SetupDiGetDeviceInstanceId(set, ref data, idBuf, 512, out _) == 0) continue;
                    var buf = new byte[512];
                    if (Native.SetupDiGetDeviceRegistryProperty(set, ref data, 13,
                            out _, buf, (uint)buf.Length, out uint needed) && needed >= 2)
                    {
                        string loc = Encoding.Unicode.GetString(buf, 0, (int)needed - 2);
                        if (loc.StartsWith("PCI bus", StringComparison.OrdinalIgnoreCase))
                            map[idBuf.ToString()] = loc;
                    }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }
        catch
        {
            // LocationInformation is best-effort; adapters appear without PCI bus info.
        }
        return map;
    }

    private static Dictionary<string, string> QueryWmiLocations()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID,LocationInformation FROM Win32_PnPEntity");
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["PNPDeviceID"] as string;
                var loc = mo["LocationInformation"] as string;
                if (id != null && loc != null && loc.StartsWith("PCI bus", StringComparison.OrdinalIgnoreCase))
                    map[id] = loc;
            }
        }
        catch (ManagementException)
        {
            // Fallback only; ignore.
        }
        return map;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid classGuid;
            public uint devInst;
            public IntPtr reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceId, uint deviceIdSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, uint property_, out uint propertyRegDataType,
            byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(string deviceName, uint deviceNumber,
            ref DISPLAY_DEVICE displayDevice, uint flags);
    }
}
