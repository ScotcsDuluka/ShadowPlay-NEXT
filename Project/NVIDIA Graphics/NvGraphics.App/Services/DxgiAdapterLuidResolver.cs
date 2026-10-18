using System.Runtime.InteropServices;
using NvGraphics.Abstractions;
using NvGraphics.Models;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;

namespace NvGraphics.Services;

/// <summary>One DXGI adapter descriptor, before correlation with discovery.</summary>
public sealed record DxgiAdapterRecord(
    string CanonicalLuid,
    string Description,
    uint VendorId,
    uint DeviceId,
    uint SubSysId,
    ulong DedicatedVideoMemoryBytes);

/// <summary>
/// Enumerates DXGI adapters to obtain each adapter's LUID and hardware identifiers,
/// then correlates LUIDs with adapters discovered by GpuDiscoveryService using
/// hardware identity (vendor/device/subsystem), never adapter indexes or names alone.
///
/// One physical adapter may appear under several DXGI LUIDs (observed on the dev
/// machine: one card exposed three LUIDs), so correlation maps each hardware-identity
/// GROUP of DXGI records to the discovered adapter with that identity; if two different
/// discovered adapters shared one identity, the group is ambiguous and stays unresolved.
/// </summary>
public sealed class DxgiAdapterLuidResolver : ILuidAdapterResolver
{
    private readonly Dictionary<string, GpuAdapterInfo> _byLuid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DxgiAdapterRecord> _dxgiRecords = new();

    /// <summary>DXGI enumeration errors encountered by the last Refresh, for diagnostics.</summary>
    public string LastError { get; private set; } = "";
    public IReadOnlyList<DxgiAdapterRecord> DxgiRecords => _dxgiRecords;
    public int ResolvedCount => _byLuid.Count;
    /// <summary>Resolved LUID → adapter mapping, for stamping DxgiLuid back onto adapters.</summary>
    public IReadOnlyDictionary<string, GpuAdapterInfo> ResolvedByLuid => _byLuid;

    /// <summary>Builds a resolver over the given discovered adapters (enumeration + correlation).</summary>
    public static DxgiAdapterLuidResolver Create(IEnumerable<GpuAdapterInfo> adapters, out string error)
    {
        var r = new DxgiAdapterLuidResolver();
        r.Refresh(adapters);
        error = r.LastError;
        return r;
    }

    public void Refresh(IEnumerable<GpuAdapterInfo> adapters)
    {
        _byLuid.Clear();
        _dxgiRecords.Clear();
        LastError = "";
        var records = EnumerateDxgiAdapters();
        if (records is null) return;
        _dxgiRecords.AddRange(records);

        // Group DXGI records by full hardware identity; each group maps to the single
        // discovered adapter carrying the same identity, if exactly one.
        foreach (var group in records.GroupBy(r => (r.VendorId, r.DeviceId, r.SubSysId)))
        {
            var identity = $"VEN_{group.Key.VendorId:x4}&DEV_{group.Key.DeviceId:x4}" +
                (group.Key.SubSysId != 0 ? $"&SUBSYS_{group.Key.SubSysId:x8}" : "");
            var tokens = LuidUtility.HardwareTokens(identity);
            var matches = adapters.Where(a =>
            {
                if (a?.AdapterId is null) return false;
                var t = LuidUtility.HardwareTokens(a.AdapterId);
                return t.Count == tokens.Count && !t.Except(tokens).Any();
            }).ToList();
            if (matches.Count == 1)
                foreach (var rec in group)
                    _byLuid[rec.CanonicalLuid] = matches[0];
            // 0 matches (virtual/unknown adapters, e.g. Basic Render Driver) or 2+
            // (indistinguishable cards) stay unresolved rather than guessed.
        }
    }

    public GpuAdapterInfo Resolve(string luid) =>
        luid is null ? null : _byLuid.GetValueOrDefault(LuidUtility.NormalizeCounterLuid(luid) ?? luid);

    /// <summary>
    /// Raw DXGI enumeration (IDXGIFactory1::EnumAdapters1) via CsWin32-generated
    /// interop. Returns null on failure with LastError set.
    /// </summary>
    private List<DxgiAdapterRecord> EnumerateDxgiAdapters()
    {
        var result = new List<DxgiAdapterRecord>();
        Guid iid = typeof(IDXGIFactory1).GUID;
        int hr = PInvoke.CreateDXGIFactory1(iid, out object factoryObj);
        if (hr != 0)
        {
            LastError = $"CreateDXGIFactory1 failed: 0x{hr:x8}";
            return null;
        }
        var factory = (IDXGIFactory1)factoryObj;
        try
        {
            for (uint i = 0; ; i++)
            {
                hr = factory.EnumAdapters1(i, out IDXGIAdapter1 adapter);
                if (hr == unchecked((int)0x887a0002)) break;   // DXGI_ERROR_NOT_FOUND
                if (hr != 0)
                {
                    LastError = $"EnumAdapters1({i}) failed: 0x{hr:x8}";
                    return null;
                }
                try
                {
                    var desc = adapter.GetDesc();
                    result.Add(new DxgiAdapterRecord(
                        LuidUtility.FromParts(desc.AdapterLuid.LowPart, (uint)desc.AdapterLuid.HighPart),
                        desc.Description.ToString(),
                        desc.VendorId, desc.DeviceId, desc.SubSysId,
                        desc.DedicatedVideoMemory));
                }
                finally
                {
                    _ = Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(factory);
        }
        return result;
    }
}
