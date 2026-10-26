using System;
using System.Linq;
using DulukaEngine.Video.Backends.Ddagrab;

// Uses the production metadata collector; no duplicate selection algorithm in tests.
internal static class DxgiInventory
{
    internal static void Write(DxgiDisplayInventory.Snapshot snapshot, Action<string> write)
    {
        foreach (var adapter in snapshot.Adapters)
        {
            var id = adapter.Identity;
            write($"DXGI_ADAPTER index={adapter.Index} description={adapter.Description} " +
                $"vendor=0x{id.VendorId:x4} device=0x{id.DeviceId:x4} luid={id.High:x8}:{id.Low:x8} " +
                $"physical={id.IsPhysical} software={id.IsSoftware} pathAvailable={id.DevicePath.Length > 0} " +
                $"pnp={id.DeviceInstanceId} " +
                $"outputs={snapshot.Outputs.Count(t => t.Adapter.SameDevice(id))}");
        }
        foreach (var output in snapshot.Outputs)
            write($"DXGI_OUTPUT luid={output.Adapter.High:x8}:{output.Adapter.Low:x8} " +
                $"output={output.OutputIndex} attached={output.Attached} " +
                $"primaryOrigin={output.Left == 0 && output.Top == 0} width={output.Width} height={output.Height} " +
                $"technology={output.OutputTechnology} eligible={output.Eligible}");
    }
}
