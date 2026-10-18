namespace NvGraphics.Models;

/// <summary>Discrete state of a game profile's Windows GPU preference.</summary>
public enum PreferenceStatus
{
    /// <summary>No preference has been configured for this profile yet.</summary>
    NotConfigured,
    /// <summary>A GPU selection is stored in the profile but not written to Windows.</summary>
    Configured,
    /// <summary>The preference value was written to HKCU and verified by reading it back.</summary>
    Applied,
    /// <summary>The game process is/was running but the actual GPU in use was not verified.</summary>
    RuntimeNotVerified,
    /// <summary>The game process was observed running on the configured GPU.</summary>
    RuntimeVerified,
    /// <summary>An operation failed; see the accompanying message.</summary>
    Error
}

/// <summary>One display adapter as discovered from Windows device information.</summary>
public sealed class GpuAdapterInfo
{
    /// <summary>Human-readable name from Win32_VideoController (may be customized per card).</summary>
    public string DisplayName { get; set; }

    /// <summary>Vendor/compatibility string, e.g. "NVIDIA".</summary>
    public string Vendor { get; set; }

    /// <summary>Stable PnP identity "VEN_xxxx&amp;DEV_xxxx&amp;SUBSYS_yyyyyyyy" used by
    /// UserGpuPreferences SpecificAdapter. Null when the adapter is not a PCI device.</summary>
    public string AdapterId { get; set; }

    /// <summary>Full PNPDeviceID, e.g. PCI\VEN_10DE&amp;DEV_1B06&amp;SUBSYS_85F11043&amp;REV_A1\4&amp;...&amp;0&amp;0008.</summary>
    public string PnpDeviceId { get; set; }

    /// <summary>PCI bus location when available, e.g. "PCI bus 1, device 0, function 0".</summary>
    public string PciLocation { get; set; }

    /// <summary>PCI bus id in nvidia-smi form "00000000:01:00.0" when it could be derived.</summary>
    public string PciBusId { get; set; }

    /// <summary>Canonical DXGI adapter LUID ("0xHHHHHHHH_0xLLLLLLLL") when resolved; the join key for GPU Engine counters.</summary>
    public string DxgiLuid { get; set; }
    public string DriverVersion { get; set; }
    public string Status { get; set; }
    public bool IsSystemDefaultDisplayAdapter { get; set; }

    public override string ToString() => AdapterId is null
        ? $"{DisplayName} (no PCI adapter id)"
        : $"{DisplayName} [{AdapterId}]";
}
