using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DulukaEngine.Video.Transport.D3D11;

// Source-side transport only: no acquisition, encoder, clock, audio or mux.
// Caller must serialize all other use of the device's immediate context, keep
// the borrowed frame stable through Convert, and own the returned CPU frame.
public sealed class BgraStagingTransport : IDisposable
{
    readonly object gate = new();
    readonly CaptureTarget target;
    readonly int width, height, timeoutMs;
    readonly long generation;
    readonly VideoColorDescriptor color;
    ID3D11Device? device;
    ID3D11DeviceContext? context;
    ID3D11Texture2D? staging;
    bool disposed, faulted;
    long readbacks, maps, unmaps, stagingCreated, stagingDisposed, busyPolls;
    public AdapterIdentity ActualAdapter { get; }
    public uint LastRowPitch { get; private set; }
    public long Readbacks => Interlocked.Read(ref readbacks);
    public long Maps => Interlocked.Read(ref maps);
    public long Unmaps => Interlocked.Read(ref unmaps);
    public long StagingCreated => Interlocked.Read(ref stagingCreated);
    public long StagingDisposed => Interlocked.Read(ref stagingDisposed);
    public long BusyPolls => Interlocked.Read(ref busyPolls);
    public bool IsFaulted { get { lock(gate) return faulted; } }

    public BgraStagingTransport(ID3D11Device sourceDevice, CaptureTarget selectedTarget,
        int surfaceWidth,int surfaceHeight,long sourceGeneration,int copyTimeoutMs=5000)
        : this(sourceDevice,selectedTarget,surfaceWidth,surfaceHeight,sourceGeneration,copyTimeoutMs,null) { }
    public BgraStagingTransport(ID3D11Device sourceDevice, CaptureTarget selectedTarget,
        int surfaceWidth, int surfaceHeight, long sourceGeneration, int copyTimeoutMs, VideoColorDescriptor? color)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);
        if(selectedTarget is null || !selectedTarget.Eligible || selectedTarget.Rotation != 1)
            throw new ArgumentException("staging_eligible_unrotated_target_required");
        if(surfaceWidth < 16 || surfaceWidth > 1920 || surfaceHeight < 16 || surfaceHeight > 1080 ||
            (surfaceWidth & 1)!=0 || (surfaceHeight & 1)!=0 || sourceGeneration<=0 || copyTimeoutMs<1 || copyTimeoutMs>30000)
            throw new ArgumentException("staging_invalid_bounded_plan");
        target=selectedTarget; width=surfaceWidth; height=surfaceHeight; generation=sourceGeneration; timeoutMs=copyTimeoutMs;
        this.color=color ?? new VideoColorDescriptor(false);
        try {
            // Own distinct COM references, not the caller's managed wrappers.
            device=sourceDevice.QueryInterface<ID3D11Device>();
            ActualAdapter=ReadAdapter(device,target.Adapter.DevicePath);
            if(!target.Adapter.SameDevice(ActualAdapter) || !ActualAdapter.IsPhysical)
                throw new InvalidOperationException("staging_actual_device_luid_mismatch");
            context=device.ImmediateContext;
            staging=device.CreateTexture2D(new Texture2DDescription {
                Width=(uint)width, Height=(uint)height, MipLevels=1, ArraySize=1,
                Format=Format.B8G8R8A8_UNorm, SampleDescription=new SampleDescription(1,0),
                Usage=ResourceUsage.Staging, BindFlags=BindFlags.None, CPUAccessFlags=CpuAccessFlags.Read,
                MiscFlags=ResourceOptionFlags.None });
            stagingCreated=1;
        } catch { Dispose(); throw; }
    }

    static AdapterIdentity ReadAdapter(ID3D11Device source, string path)
    {
        using var dxgi=source.QueryInterface<IDXGIDevice>();
        using var parent=dxgi.GetParent<IDXGIAdapter1>(); var desc=parent.Description1;
        return new(desc.Luid.LowPart,desc.Luid.HighPart,desc.VendorId,desc.DeviceId,(desc.Flags & AdapterFlags.Software)!=0,path);
    }

    public OwnedNv12Frame Convert(ID3D11VideoFrame source, CaptureTarget sourceTarget,
        long sourceGeneration, CancellationToken cancellation = default)
    {
        lock(gate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(faulted) throw new InvalidOperationException("staging_transport_faulted");
            cancellation.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(source);
            if(sourceTarget is null || !sourceTarget.Eligible || !target.SameOutput(sourceTarget))
                throw new InvalidOperationException("staging_source_target_mismatch");
            if(sourceGeneration!=generation) throw new InvalidOperationException("staging_stale_source_generation");
            if(source.Origin!=VideoFrameOrigin.GpuD3D11Texture || source.PixelFormat!=VideoPixelFormat.Bgra8 ||
                source.Dimensions.Width!=width || source.Dimensions.Height!=height ||
                source.SharedHandle!=IntPtr.Zero || source.NativeTexture is not ID3D11Texture2D texture || texture.NativePointer==IntPtr.Zero)
                throw new ArgumentException("staging_borrowed_bgra_texture_required");
            // Device caches a wrapper on the borrowed texture. Dispose only our
            // independent COM reference, so repeated borrows remain valid.
            using(var owner=texture.Device.QueryInterface<ID3D11Device>()) {
                if(!ReadAdapter(owner,target.Adapter.DevicePath).SameDevice(ActualAdapter))
                    throw new InvalidOperationException("staging_texture_adapter_mismatch");
                // Same LUID is insufficient for CopyResource across devices.
                // This transport has no shared-resource/keyed-mutex protocol.
                if(owner.NativePointer!=device!.NativePointer)
                    throw new InvalidOperationException("staging_foreign_device_texture_not_supported");
            }
            var desc=texture.Description;
            if(desc.Width!=width || desc.Height!=height || desc.Format!=Format.B8G8R8A8_UNorm ||
                desc.MipLevels!=1 || desc.ArraySize!=1 || desc.SampleDescription.Count!=1 || desc.SampleDescription.Quality!=0 ||
                (desc.Usage!=ResourceUsage.Default && desc.Usage!=ResourceUsage.Immutable) || desc.MiscFlags!=ResourceOptionFlags.None)
                throw new NotSupportedException("staging_texture_descriptor_not_supported");
            if(source.Diagnostics.PresentationTimestampTicks<0 || source.Diagnostics.Sequence<0)
                throw new ArgumentException("staging_invalid_frame_timeline");

            bool mapped=false;
            try {
                context!.CopyResource(staging!,texture);
                context.Flush();
                var timer=Stopwatch.StartNew();
                MappedSubresource rows;
                while(true) {
                    cancellation.ThrowIfCancellationRequested();
                    if(timer.ElapsedMilliseconds>=timeoutMs) throw new TimeoutException("staging_gpu_copy_deadline");
                    var status=context.Map(staging!,0,MapMode.Read,Vortice.Direct3D11.MapFlags.DoNotWait,out rows);
                    if(status==Vortice.DXGI.ResultCode.WasStillDrawing) {
                        Interlocked.Increment(ref busyPolls); Thread.Sleep(1); continue;
                    }
                    status.CheckError(); mapped=true; Interlocked.Increment(ref maps); break;
                }
                LastRowPitch=rows.RowPitch;
                int pitch=checked((int)rows.RowPitch);
                long count=(long)pitch*height;
                if(rows.DataPointer==IntPtr.Zero || pitch<width*4 || count>64*1024*1024)
                    throw new InvalidDataException("staging_invalid_mapped_pitch");
                var bgra=new byte[checked((int)count)];
                // Copy only meaningful bytes. Padding stays zero; never expose
                // unrelated driver padding. Preserve mapped pitch for converter.
                for(int y=0;y<height;++y) {
                    cancellation.ThrowIfCancellationRequested();
                    Marshal.Copy(IntPtr.Add(rows.DataPointer,checked(y*pitch)),bgra,y*pitch,width*4);
                }
                var bytes=BgraToNv12Converter.ConvertTopDown(bgra,width,height,pitch,this.color.FullRange);
                var result=new OwnedNv12Frame(bytes,width,height,source.Diagnostics,this.color);
                Interlocked.Increment(ref readbacks); return result;
            } catch { faulted=true; throw; }
            finally {
                if(mapped) { context!.Unmap(staging!,0); Interlocked.Increment(ref unmaps); }
            }
        }
    }

    public void Dispose()
    {
        lock(gate) {
            if(disposed) return;
            disposed=true;
            if(staging is not null) { staging.Dispose(); staging=null; Interlocked.Increment(ref stagingDisposed); }
            context?.Dispose(); context=null; device?.Dispose(); device=null;
        }
    }
}

public sealed class OwnedNv12Frame : INv12VideoFrame, IColorVideoFrame
{
    readonly byte[] bytes;
    internal OwnedNv12Frame(byte[] owned, int width, int height, FrameDiagnostics diagnostics, VideoColorDescriptor? color=null)
    { bytes=owned; Dimensions=new(width,height); Stride=width; Diagnostics=diagnostics; Color=color ?? new(false); }
    public VideoColorDescriptor Color { get; }
    public VideoFrameOrigin Origin=>VideoFrameOrigin.CpuMemory;
    public VideoPixelFormat PixelFormat=>VideoPixelFormat.Nv12;
    public VideoFrameDimensions Dimensions { get; }
    public FrameDiagnostics Diagnostics { get; }
    public int Stride { get; }
    public bool IsDisposed { get; private set; }
    public int DisposeCount { get; private set; }
    public ReadOnlyMemory<byte> Data => !IsDisposed ? bytes : throw new ObjectDisposedException(nameof(OwnedNv12Frame));
    public void Dispose() { ++DisposeCount; if(!IsDisposed) { IsDisposed=true; Array.Clear(bytes); } }
}
