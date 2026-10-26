using System.Runtime.InteropServices;
using System.Text.Json;
using DulukaEngine.Diagnostics;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Transport.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

static partial class Program
{
    const int TextureWidth=322, TextureHeight=180;
    static EngineLogger I3Log()=>new("I3-owned-texture",EngineLogger.LogLevel.Warning,
        message=>File.AppendAllText(Path.Combine(I3Evidence,"texture-session-runtime.log"),message+Environment.NewLine));

    static void I3D3D11Transport(CaptureTarget target)
    {
        foreach(var rc in new[]{"vbr","cqp"}) TextureSession(target,rc);
        TextureValidation(target);
    }
    static void TextureSession(CaptureTarget target,string rc)
    {
        using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
        using var engine=new RecordingEngine(I3Log(),factory);
        engine.Initialize(Startup(target,rc));
        using var transport=new BgraStagingTransport(factory.Source!.OwnedDevice,target,TextureWidth,TextureHeight,1);
        Check(transport.ActualAdapter.SameDevice(factory.Source.DeviceIdentity) &&
            transport.ActualAdapter.SameDevice(factory.Encoders[0].ActualAdapter),"I3 actual texture/source/encoder device LUID attested: "+transport.ActualAdapter);
        var sourceFrames=new List<OwnedTextureFrame>(); var cpuFrames=new List<OwnedNv12Frame>();
        var conf=SessionConfig("I3-unused"); conf.OutputPath=Path.Combine(I3Evidence,"texture-staging-"+rc+".mp4");
        var result=engine.StartOwnedFrameSession(conf,TextureInputs(target,factory.Source.OwnedDevice,transport,sourceFrames,cpuFrames));
        File.WriteAllText(Path.Combine(I3Evidence,"texture-staging-"+rc+"-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Check(result.Pass,"I3 synthetic texture through original RecordingSession/QSV/mux/full decode: "+result.ErrorMessage);
        Check(result.FramesSubmitted==60 && result.EncoderPacketCount==60 && result.MuxedPacketCount==60 && result.DecodedFrameCount==60,
            "I3 texture submitted/encoded/muxed/decoded conservation 60/60/60/60");
        Check(result.FlushedPackets>0 && result.FinalizationState=="Saved" && result.FullDecodeVerified,"I3 texture delayed packets reach verified Saved result");
        Check(result.FramesCaptured==0 && result.SyntheticFramesConsumed==60 && !factory.Source.Started,"I3 texture fixture never starts Desktop or counts real captured frames");
        Check(sourceFrames.Count==60 && sourceFrames.All(f=>f.IsDisposed && f.DisposeCount==1),"I3 borrowed GPU texture frames disposed exactly once by source after readback");
        Check(cpuFrames.Count==60 && cpuFrames.All(f=>f.IsDisposed && f.DisposeCount==1),"I3 converted CPU frame ownership transferred once to session");
        Check(transport.Readbacks==60 && transport.Maps==60 && transport.Unmaps==60 && transport.StagingCreated==1,
            "I3 one bounded staging surface reused with 60 paired maps/unmaps");
        Check(transport.LastRowPitch>TextureWidth*4,"I3 real GPU padded row pitch exercised: "+transport.LastRowPitch);
        Check(factory.Encoders[0].NativeLockedSurfaces==0 && factory.Encoders[0].NativeOutstandingPackets==0,"I3 flush retains no native locked surface or pending sync job");
        var identity=result.VideoIdentity;
        Check(identity.CaptureLuidLow==target.Adapter.Low && identity.CaptureLuidHigh==target.Adapter.High &&
            identity.EncoderLuidLow==target.Adapter.Low && identity.EncoderLuidHigh==target.Adapter.High && identity.OutputIndex==target.OutputIndex,
            "I3 exact LUID high/low and output preserved in session evidence");
        VerifyI3MovingMarker(result.OutputPath,TextureWidth,TextureHeight);
        transport.Dispose();
        Check(transport.StagingCreated==transport.StagingDisposed,"I3 staging surface and owned COM references disposed before source device");
        File.WriteAllText(Path.Combine(I3Evidence,"texture-staging-"+rc+"-transport.json"),JsonSerializer.Serialize(new {
            SourceAdapter=factory.Source.DeviceIdentity.ToString(), StagingAdapter=transport.ActualAdapter.ToString(),
            EncoderAdapter=factory.Encoders[0].ActualAdapter.ToString(), Runtime=factory.Encoders[0].RuntimeVersion,
            Implementation=factory.Encoders[0].HardwareImplementation, target.OutputIndex, Width=TextureWidth, Height=TextureHeight,
            transport.LastRowPitch, transport.Readbacks, transport.Maps, transport.Unmaps, transport.BusyPolls,
            transport.StagingCreated, transport.StagingDisposed, GpuFramesDisposed=sourceFrames.Sum(f=>f.DisposeCount),
            CpuFramesDisposed=cpuFrames.Sum(f=>f.DisposeCount), result.FlushedPackets, result.SessionId,
            ScreenRead=false, AudioOpened=false, RateControl=rc, Input="synthetic owned D3D11 BGRA8", Conversion="CPU BT.709 limited NV12"
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"I3_TEXTURE_SESSION_PASS rc={rc} frames={result.DecodedFrameCount} flushed={result.FlushedPackets} rowPitch={transport.LastRowPitch} luid={transport.ActualAdapter}");
    }
    static IEnumerable<SessionFrameInput> TextureInputs(CaptureTarget target,ID3D11Device device,BgraStagingTransport transport,
        List<OwnedTextureFrame> sourceFrames,List<OwnedNv12Frame> cpuFrames)
    {
        for(int i=0;i<60;++i) {
            OwnedNv12Frame cpu;
            using(var source=new OwnedTextureFrame(device,i)) {
                sourceFrames.Add(source); cpu=transport.Convert(source,target,1);
                Check(!source.IsDisposed && source.DisposeCount==0,"I3 converter borrows GPU frame until synchronized readback "+i);
            } // GPU texture released BEFORE the session submits owned CPU NV12.
            cpuFrames.Add(cpu);
            yield return new SessionFrameInput(cpu,target,1);
        }
    }
    static void TextureValidation(CaptureTarget target)
    {
        using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
        using var engine=new RecordingEngine(I3Log(),factory); engine.Initialize(Startup(target,"vbr"));
        var device=factory.Source!.OwnedDevice;
        using var transport=new BgraStagingTransport(device,target,TextureWidth,TextureHeight,1);
        using var good=new OwnedTextureFrame(device,0);
        Reject(()=>transport.Convert(good,target,2),"stale_source_generation","I3 rejects stale texture source generation before GPU copy");
        var other=new CaptureTarget(target.Adapter,target.OutputIndex+1,target.DeviceName,target.Monitor,target.Width,target.Height,
            target.Left,target.Top,target.Rotation,true,target.OutputTechnology,target.TopologyId,target.MonitorDevicePath);
        Reject(()=>transport.Convert(good,other,1),"source_target_mismatch","I3 rejects wrong output despite matching adapter");
        using(var cancelled=new CancellationTokenSource()) {
            cancelled.Cancel();
            try { transport.Convert(good,target,1,cancelled.Token); throw new Exception("FAIL cancelled staging accepted"); }
            catch(OperationCanceledException) { Check(transport.Maps==0 && !transport.IsFaulted,"I3 cancellation before copy neither maps nor faults healthy transport"); }
        }
        using(var disposed=new OwnedTextureFrame(device,0)) {
            disposed.Dispose();
            Reject(()=>transport.Convert(disposed,target,1),"borrowed_bgra_texture_required","I3 rejects disposed texture frame");
        }
        using(var rgba=new OwnedTextureFrame(device,0,Format.R8G8B8A8_UNorm))
            Reject(()=>transport.Convert(rgba,target,1),"descriptor_not_supported","I3 rejects actual RGBA descriptor despite BGRA frame label");
        using(var array=new OwnedTextureFrame(device,0,Format.B8G8R8A8_UNorm,2))
            Reject(()=>transport.Convert(array,target,1),"descriptor_not_supported","I3 rejects texture arrays instead of copying wrong subresource");
        using(var foreignFactory=new OwnedFrameFactory(target,TextureWidth,TextureHeight))
        using(var foreignEngine=new RecordingEngine(I3Log(),foreignFactory)) {
            foreignEngine.Initialize(Startup(target,"vbr"));
            using var foreign=new OwnedTextureFrame(foreignFactory.Source!.OwnedDevice,0);
            Reject(()=>transport.Convert(foreign,target,1),"foreign_device_texture_not_supported","I3 same-LUID foreign-device texture requires a separately implemented sharing protocol");
        }
        var wrong=new AdapterIdentity(target.Adapter.Low^0x40000000u,target.Adapter.High,target.Adapter.VendorId,target.Adapter.DeviceId,false,target.Adapter.DevicePath);
        var wrongTarget=new CaptureTarget(wrong,target.OutputIndex,target.DeviceName,target.Monitor,target.Width,target.Height,
            target.Left,target.Top,target.Rotation,true,target.OutputTechnology,target.TopologyId,target.MonitorDevicePath);
        Reject(()=>{ using var bad=new BgraStagingTransport(device,wrongTarget,TextureWidth,TextureHeight,1); },
            "actual_device_luid_mismatch","I3 actual readback device cannot be relabelled with requested LUID");
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null,DriverType.Warp,DeviceCreationFlags.BgraSupport,
            new[]{FeatureLevel.Level_11_0},out ID3D11Device warp,out ID3D11DeviceContext warpContext).CheckError();
        using(warp) using(warpContext) {
            using var foreign=new OwnedTextureFrame(warp,0);
            Reject(()=>transport.Convert(foreign,target,1),"texture_adapter_mismatch","I3 actual software-adapter texture cannot impersonate selected Intel target");
            using var dxgi=warp.QueryInterface<IDXGIDevice>(); using var parent=dxgi.GetParent<IDXGIAdapter1>(); var d=parent.Description1;
            var fakePhysical=new AdapterIdentity(d.Luid.LowPart,d.Luid.HighPart,d.VendorId,d.DeviceId,false,
                $@"\\?\PCI#VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}#synthetic");
            var fakeTarget=new CaptureTarget(fakePhysical,target.OutputIndex,target.DeviceName,target.Monitor,target.Width,target.Height,
                target.Left,target.Top,target.Rotation,true,target.OutputTechnology,target.TopologyId,target.MonitorDevicePath);
            Reject(()=>{ using var rejected=new BgraStagingTransport(warp,fakeTarget,TextureWidth,TextureHeight,1); },
                "actual_device_luid_mismatch","I3 actual DXGI software flag overrides caller physical-device claim");
        }
        Check(transport.Readbacks==0 && transport.Maps==0 && !transport.IsFaulted,"I3 invalid metadata/descriptors do not copy or poison valid staging path");
        using(var cpu=transport.Convert(good,target,1))
            Check(cpu.Diagnostics.Sequence==0 && cpu.Diagnostics.PresentationTimestampTicks==0 && !good.IsDisposed,"I3 valid conversion remains available after input validation errors");
        Check(transport.Maps==transport.Unmaps,"I3 successful recovery leaves no mapped staging resource");
        using(var colors=new OwnedTextureFrame(device,0,colorProbe:true))
        using(var nv12=transport.Convert(colors,target,1)) {
            var pixels=nv12.Data.Span;
            foreach(int y in new[]{0,90,TextureHeight-1}) {
                foreach(var patch in new[]{(X:16,Y:63,U:102,V:240),(X:48,Y:173,U:42,V:26),(X:80,Y:32,U:240,V:118)}) {
                    int uv=TextureWidth*TextureHeight+(y/2)*TextureWidth+patch.X;
                    Check(pixels[y*TextureWidth+patch.X]==patch.Y && pixels[uv]==patch.U && pixels[uv+1]==patch.V,
                        $"I3 actual GPU readback preserves BGRA channel order/BT.709 chroma at ({patch.X},{y})");
                }
            }
        }
        using(var leaseFactory=new OwnedFrameFactory(target,TextureWidth,TextureHeight))
        using(var leaseEngine=new RecordingEngine(I3Log(),leaseFactory)) {
            leaseEngine.Initialize(Startup(target,"vbr"));
            using var source=new OwnedTextureFrame(leaseFactory.Source!.OwnedDevice,0);
            using var ownRefs=new BgraStagingTransport(leaseFactory.Source.OwnedDevice,target,TextureWidth,TextureHeight,1);
            leaseFactory.Source.Dispose(); // transport/texture hold their own COM references
            using var cpu=ownRefs.Convert(source,target,1);
            Check(cpu.Data.Length==TextureWidth*TextureHeight*3/2,"I3 owned COM references survive disposal of caller device/context wrappers");
        }
        transport.Dispose();
        try { transport.Convert(good,target,1); throw new Exception("FAIL disposed transport accepted"); }
        catch(ObjectDisposedException) { Check(true,"I3 disposed transport cannot access released device resources"); }
    }

    sealed class OwnedTextureFrame : ID3D11VideoFrame
    {
        ID3D11Texture2D? texture;
        public OwnedTextureFrame(ID3D11Device device,int sequence,Format format=Format.B8G8R8A8_UNorm,uint arraySize=1,bool colorProbe=false)
        {
            int pitch=TextureWidth*4+32;
            var bgra=new byte[pitch*TextureHeight]; Array.Fill(bgra,(byte)0xA5);
            for(int y=0;y<TextureHeight;++y) for(int x=0;x<TextureWidth;++x) {
                int p=y*pitch+x*4;
                byte c=(byte)(x/16==sequence%20?220:40+sequence%16*3);
                bgra[p]=bgra[p+1]=bgra[p+2]=c; bgra[p+3]=255;
                if(colorProbe && x<96) {
                    bgra[p]=(byte)(x>=64?255:0); bgra[p+1]=(byte)(x>=32 && x<64?255:0); bgra[p+2]=(byte)(x<32?255:0);
                }
            }
            var pin=GCHandle.Alloc(bgra,GCHandleType.Pinned);
            try {
                var desc=new Texture2DDescription { Width=TextureWidth,Height=TextureHeight,MipLevels=1,ArraySize=arraySize,Format=format,
                    SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource };
                var data=Enumerable.Range(0,(int)arraySize).Select(_=>new SubresourceData(pin.AddrOfPinnedObject(),(uint)pitch,0)).ToArray();
                texture=device.CreateTexture2D(desc,data);
            } finally { pin.Free(); Array.Clear(bgra); }
            Diagnostics=new(sequence,sequence*10_000_000L/30,sequence*10_000_000L/30);
        }
        public object NativeTexture=>texture!;
        public IntPtr SharedHandle=>IntPtr.Zero;
        public VideoFrameOrigin Origin=>VideoFrameOrigin.GpuD3D11Texture;
        public VideoPixelFormat PixelFormat=>VideoPixelFormat.Bgra8;
        public VideoFrameDimensions Dimensions=>new(TextureWidth,TextureHeight);
        public FrameDiagnostics Diagnostics { get; }
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public void Dispose() { ++DisposeCount; if(IsDisposed) return; IsDisposed=true; texture?.Dispose(); texture=null; }
    }
}
