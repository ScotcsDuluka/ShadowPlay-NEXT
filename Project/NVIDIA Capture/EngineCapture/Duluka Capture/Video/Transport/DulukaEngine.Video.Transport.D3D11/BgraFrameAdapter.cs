using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DulukaEngine.Video.Transport.D3D11;

/// <summary>
/// Source-side composition seam; never creates a device or starts acquisition.
/// The caller supplies a freshly resolved target and owns a stable, unshared
/// frame until Convert returns. One instance binds one device/source generation.
/// Source context protection must already be enabled and remain enabled; this
/// adapter does not change the source's threading policy. No live factory wiring.
/// </summary>
public sealed class BgraFrameAdapter : IDisposable
{
    readonly object gate = new();
    readonly CaptureTarget target;
    readonly long generation;
    readonly int width, height, timeoutMs;
    readonly VideoColorDescriptor? color;
    BgraStagingTransport? transport;
    bool disposed, faulted;

    public BgraFrameAdapter(CaptureTarget selectedTarget, long sourceGeneration,
        int surfaceWidth=0,int surfaceHeight=0,int copyTimeoutMs=5000)
        : this(selectedTarget,sourceGeneration,surfaceWidth,surfaceHeight,copyTimeoutMs,null) { }
    public BgraFrameAdapter(CaptureTarget selectedTarget, long sourceGeneration,
        int surfaceWidth, int surfaceHeight, int copyTimeoutMs, VideoColorDescriptor? color)
    {
        if (selectedTarget is null || !selectedTarget.Eligible || selectedTarget.Rotation != 1)
            throw new ArgumentException("frame_adapter_eligible_unrotated_target_required");
        width = surfaceWidth == 0 ? selectedTarget.Width : surfaceWidth;
        height = surfaceHeight == 0 ? selectedTarget.Height : surfaceHeight;
        if (width < 16 || width > 1920 || height < 16 || height > 1080 ||
            (width & 1) != 0 || (height & 1) != 0 || sourceGeneration <= 0 ||
            copyTimeoutMs < 1 || copyTimeoutMs > 30000)
            throw new ArgumentException("frame_adapter_invalid_bounded_plan");
        target = selectedTarget; generation = sourceGeneration; timeoutMs = copyTimeoutMs;
        this.color=color;
    }

    public bool IsFaulted { get { lock (gate) return faulted; } }
    public long TransportsCreated { get; private set; }
    public long TransportsDisposed { get; private set; }

    public OwnedNv12Frame Convert(ID3D11VideoFrame source, CaptureTarget sourceTarget,
        long sourceGeneration, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted) throw new InvalidOperationException("frame_adapter_faulted");
            cancellation.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(source);
            if (sourceTarget is null || !sourceTarget.Eligible || !target.SameOutput(sourceTarget) ||
                target.TopologyId != sourceTarget.TopologyId ||
                !string.Equals(target.Adapter.DevicePath, sourceTarget.Adapter.DevicePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("frame_adapter_source_target_mismatch");
            if (generation != sourceGeneration)
                throw new InvalidOperationException("frame_adapter_stale_source_generation");
            if (source.Origin != VideoFrameOrigin.GpuD3D11Texture || source.PixelFormat != VideoPixelFormat.Bgra8 ||
                source.Dimensions.Width != width || source.Dimensions.Height != height || source.SharedHandle != IntPtr.Zero ||
                source.NativeTexture is not ID3D11Texture2D texture || texture.NativePointer == IntPtr.Zero)
                throw new ArgumentException("frame_adapter_borrowed_bgra_texture_required");
            var desc = texture.Description;
            if (desc.Width != width || desc.Height != height || desc.Format != Format.B8G8R8A8_UNorm ||
                desc.MipLevels != 1 || desc.ArraySize != 1 || desc.SampleDescription.Count != 1 || desc.SampleDescription.Quality != 0 ||
                (desc.Usage != ResourceUsage.Default && desc.Usage != ResourceUsage.Immutable) || desc.MiscFlags != ResourceOptionFlags.None)
                throw new NotSupportedException("frame_adapter_texture_descriptor_not_supported");

            // Vortice caches Device on the texture. Own a separate COM reference
            // without disposing that borrowed wrapper. Never create a new device.
            using var owner = texture.Device.QueryInterface<ID3D11Device>();
            using var context = owner.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            using var multithread = context.QueryInterface<ID3D11Multithread>();
            if (!multithread.GetMultithreadProtected())
                throw new InvalidOperationException("frame_adapter_source_context_not_protected");

            // The source must never dispose/rewrite the borrowed texture during
            // this call. Context protection does not replace frame ownership.
            multithread.Enter();
            try
            {
                if (transport is null)
                {
                    ValidateOutput(owner);
                    transport = new BgraStagingTransport(owner, target, width, height, generation, timeoutMs,color);
                    ++TransportsCreated;
                }
                // The existing transport checks exact native device identity on
                // EVERY frame, including another device on the same adapter.
                return transport.Convert(source, sourceTarget, sourceGeneration, cancellation);
            }
            catch
            {
                // Keep a terminal transport terminal; never recreate it silently.
                if (transport?.IsFaulted == true) faulted = true;
                throw;
            }
            finally { multithread.Leave(); }
        }
    }

    void ValidateOutput(ID3D11Device owner)
    {
        using var dxgi = owner.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetParent<IDXGIAdapter1>();
        var d = adapter.Description1;
        var actual = new AdapterIdentity(d.Luid.LowPart, d.Luid.HighPart, d.VendorId, d.DeviceId,
            (d.Flags & AdapterFlags.Software) != 0, target.Adapter.DevicePath);
        if (!actual.IsPhysical || !target.Adapter.SameDevice(actual))
            throw new InvalidOperationException("frame_adapter_actual_device_luid_mismatch");
        // Read-only DXGI metadata. This does not DuplicateOutput or read pixels.
        var status = adapter.EnumOutputs((uint)target.OutputIndex, out IDXGIOutput output);
        using (output)
        {
            if (status.Failure || output is null)
                throw new InvalidOperationException("frame_adapter_actual_output_mismatch");
            var o = output.Description;
            var b = o.DesktopCoordinates;
            if (!o.AttachedToDesktop || o.Monitor != target.Monitor || o.DeviceName != target.DeviceName ||
                b.Left != target.Left || b.Top != target.Top || b.Right - b.Left != target.Width ||
                b.Bottom - b.Top != target.Height || (int)o.Rotation != target.Rotation)
                throw new InvalidOperationException("frame_adapter_actual_output_mismatch");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (transport is not null) { transport.Dispose(); ++TransportsDisposed; }
        }
    }
}
