using System;
using System.Collections.Generic;
using Vortice.DXGI;

// Read-only DXGI metadata inspection. Does NOT request a D3D11 device,
// DuplicateOutput, AcquireNextFrame, a texture, any screen pixels or audio.
internal static class DxgiInventory
{
    internal readonly record struct Adapter(int Index, string Description, uint VendorId,
                                            uint LuidLow, int LuidHigh,
                                            int OutputCount, int AttachedCount,
                                            int FirstAttachedIndex, bool FirstAttachedIsPrimary,
                                            int PrimaryAttachedCount);
    internal static Adapter[] Collect(Action<string> write)
    {
        var adapters = new List<Adapter>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; i < 32; ++i)
        {
            var rc = factory.EnumAdapters1(i, out IDXGIAdapter1 adapter);
            if (!rc.Success) break;
            using (adapter)
            {
                var desc = adapter.Description1;
                var connected = 0;
                var count = 0;
                var firstAttachedIndex = -1;
                var firstAttachedIsPrimary = false;
                var primaryAttachedCount = 0;
                for (uint j = 0; j < 32; ++j)
                {
                    var outRc = adapter.EnumOutputs(j, out IDXGIOutput output);
                    if (!outRc.Success) break;
                    using (output)
                    {
                        var outputDesc = output.Description;
                        if (outputDesc.AttachedToDesktop)
                        {
                            connected++;
                            bool primary = outputDesc.DesktopCoordinates.Left == 0 &&
                                           outputDesc.DesktopCoordinates.Top == 0;
                            if (primary) primaryAttachedCount++;
                            if (firstAttachedIndex < 0)
                            {
                                firstAttachedIndex = (int)j;
                                firstAttachedIsPrimary = primary;
                            }
                        }
                        count++;
                        // Do not print user-specific monitor/device names;
                        // indexes, geometry and connection flags are enough.
                        var area = outputDesc.DesktopCoordinates;
                        write($"DXGI_OUTPUT adapter={i} output={j} attached={outputDesc.AttachedToDesktop} " +
                              $"primaryOrigin={area.Left == 0 && area.Top == 0} " +
                              $"width={area.Right - area.Left} height={area.Bottom - area.Top}");
                    }
                }
                write($"DXGI_ADAPTER index={i} vendor=0x{desc.VendorId:x4} " +
                      $"luid={desc.Luid.HighPart:x8}:{desc.Luid.LowPart:x8} " +
                      $"outputs={count} attached={connected}");
                adapters.Add(new Adapter((int)i, desc.Description, desc.VendorId,
                                         desc.Luid.LowPart, desc.Luid.HighPart,
                                         count, connected, firstAttachedIndex,
                                         firstAttachedIsPrimary, primaryAttachedCount));
            }
        }
        if (adapters.Count == 0)
            throw new InvalidOperationException("DXGI read-only inventory returned no adapters");
        return adapters.ToArray();
    }
}
