using System.Diagnostics;
using System.Text.Json;
using DulukaEngine.Diagnostics;
using DulukaEngine.Encoder;
using DulukaEngine.Encoder.Qsv;
using DulukaEngine.Media;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

static partial class Program
{
    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null && !File.Exists(Path.Combine(dir.FullName,"AGENTS.md"))) dir=dir.Parent;
        return dir?.FullName ?? throw new Exception("repository not found");
    }
    static string Evidence => evidenceDirectory ?? Path.Combine(Repo(),"docs","reports","intel-duluka-i2-2026-10-24");
    static EngineLogger Log() => new("I2-synthetic-session",EngineLogger.LogLevel.Warning,
        line=>File.AppendAllText(Path.Combine(Evidence,"session-runtime.log"),line+Environment.NewLine));
    static SessionConfig SessionConfig(string name) => new() {
        OutputPath=Path.Combine(Evidence,name+".mp4"), DurationSeconds=2, TargetFps=30,
        FFmpegPath=Path.Combine(Repo(),"Project","FFmpeg","ffmpeg.exe"),
        AudioEnabled=false, MicEnabled=false, AudioSidecarMode=true, UseNativeResolution=true
    };
    static EngineStartupConfig Startup(CaptureTarget target,string rc) => new() {
        CaptureTarget=target, CaptureOutputIndex=target.OutputIndex, CodecKey="QUICKSYNC_H264",
        RateControl=rc, Preset="veryfast", Cq=24, BitrateBps=2_000_000, Fps=30, GopSize=30
    };
    static IEnumerable<SessionFrameInput> Inputs(CaptureTarget target,List<Nv12Frame> owned,int badGeneration=-1)
    {
        for(int i=0;i<60;++i) {
            var frame=new Nv12Frame(i); owned.Add(frame);
            yield return new(frame,target,i==badGeneration?2:1);
        }
    }
    static void SessionAcceptance(CaptureTarget target,string rc)
    {
        Directory.CreateDirectory(Evidence);
        using var factory=new OwnedFrameFactory(target);
        using var engine=new RecordingEngine(Log(),factory);
        engine.Initialize(Startup(target,rc));
        Check(factory.Source!.DeviceIdentity.SameDevice(factory.Encoders[0].ActualAdapter),"separate actual source/encoder D3D11 devices share exact LUID");
        var frames=new List<Nv12Frame>();
        var result=engine.StartOwnedFrameSession(SessionConfig("native-session-"+rc),Inputs(target,frames));
        File.WriteAllText(Path.Combine(Evidence,"native-session-"+rc+"-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Check(result.Pass,"one Duluka session produces verified "+rc+" MP4: "+result.ErrorMessage);
        Check(result.FinalizationState=="Saved" && result.FullDecodeVerified && result.DecodedFrameCount==60,"Saved requires full decode of exactly 60 frames");
        Check(result.FramesSubmitted==60 && result.EncoderPacketCount==60 && result.MuxedPacketCount==60,"submitted/encoded/muxed frame conservation");
        Check(result.SyntheticFramesConsumed==60 && result.FramesCaptured==0,"synthetic frames never counted as desktop capture");
        Check(result.FlushedPackets>0,"delayed flush packets reached session muxer");
        Check(result.SessionId.Length==32,"single real session ID returned");
        Check(frames.Count==60 && frames.All(f=>f.IsDisposed && f.DisposeCount==1),"source frames disposed exactly once after borrowing");
        Check(!factory.Source.Started,"owned-frame session never starts live capture source");
        Check(result.VideoIdentity.CaptureLuidLow==target.Adapter.Low && result.VideoIdentity.EncoderLuidLow==target.Adapter.Low,
            "session reports actual source and QSV device identity");
        var badCount=SessionMediaVerifier.Verify(result.OutputPath,SessionConfig("unused").FFmpegPath,61,320,180,30);
        Check(!badCount.Verified,"wrong decoded frame expectation fails media acceptance");
        VerifyDecodedOwnership(result.OutputPath);
        Console.WriteLine($"SESSION_NATIVE mode={rc} session={result.SessionId} frames={result.DecodedFrameCount} flushed={result.FlushedPackets} luid={factory.Encoders[0].ActualAdapter}");
    }
    static void Reject(Action work,string code,string name)
    {
        try { work(); }
        catch(Exception ex) { Check(ex.Message.Contains(code,StringComparison.Ordinal),name+": "+ex.Message); return; }
        throw new Exception("FAIL rejection missing: "+name);
    }
    static void VerifyDecodedOwnership(string path)
    {
        var info=new ProcessStartInfo(SessionConfig("unused").FFmpegPath) {
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach(var arg in new[]{"-v","error","-xerror","-i",path,"-an","-pix_fmt","gray","-f","rawvideo","pipe:1"}) info.ArgumentList.Add(arg);
        using var child=Process.Start(info)!;
        using var bytes=new MemoryStream();
        var copy=child.StandardOutput.BaseStream.CopyToAsync(bytes);
        var errors=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("synthetic content decode timeout"); }
        copy.GetAwaiter().GetResult();
        Check(child.ExitCode==0 && errors.GetAwaiter().GetResult().Length==0,"synthetic pixels fully decode without errors");
        var pixels=bytes.ToArray();
        Check(pixels.Length==320*180*60,"decoded ownership evidence contains exactly 60 complete frames");
        for(int i=0;i<60;++i) {
            int marker=i%20*16+8, background=((i%20+10)%20)*16+8;
            int start=i*320*180+90*320;
            // QSV is lossy and limited-range luma converts to full-range gray.
            double expected=(32+i%16*4-16)*255.0/219;
            Check(pixels[start+marker]>190 && Math.Abs(pixels[start+background]-expected)<16,
                "decoded moving marker survives caller buffer release at frame "+i);
        }
    }
    static void FailureAndRecovery(CaptureTarget target)
    {
        var cfg=Config(); cfg.RateControl="cbr";
        Reject(()=>QsvEncoderBackend.ValidateConfiguration(cfg),"cbr_known_failure","CBR rejected without silent VBR substitution");
        cfg=Config(); cfg.Preset="p4";
        Reject(()=>QsvEncoderBackend.ValidateConfiguration(cfg),"preset_not_verified","NVENC preset cannot leak into Intel policy");
        cfg=Config(); cfg.ExpectedInputFormat=VideoPixelFormat.Bgra8;
        Reject(()=>QsvEncoderBackend.ValidateConfiguration(cfg),"nv12_system_memory","unimplemented GPU BGRA transport rejected");
        cfg=Config(); cfg.EncodeWidth=160;
        Reject(()=>QsvEncoderBackend.ValidateConfiguration(cfg),"scaling_not_implemented","scaling cannot be silently ignored");
        var planner=new RecordingBackendFactory();
        var plan=planner.Plan(Startup(target,"vbr"));
        Check(!plan.RuntimeWired,"native synthetic proof does not enable live Desktop pair");
        Reject(()=>planner.CreateEncoderForInput(plan,VideoPixelFormat.Bgra8,Log()),"transport_not_implemented","factory rejects QSV incompatible source transport");
        var router=new SessionFrameRouter(target,30,1,320,180);
        using(var input=new SessionFrameInput(new Nv12Frame(1),target,1))
            Reject(()=>router.Route(input),"source_pts_discontinuity","source timeline discontinuity rejected before encoder");
        var other=new CaptureTarget(target.Adapter,target.OutputIndex+1,target.DeviceName,target.Monitor,target.Width,target.Height,
            target.Left,target.Top,target.Rotation,true,target.OutputTechnology,target.TopologyId,target.MonitorDevicePath);
        using(var input=new SessionFrameInput(new Nv12Frame(0),other,1))
            Reject(()=>router.Route(input),"source_target_mismatch","output mismatch cannot route into the session");
        using(var encoder=new QsvEncoderBackend(target.Adapter)) {
            encoder.Initialize(Config()); encoder.Start();
            var result=new SessionResult();
            var sink=new SessionPacketSink(encoder,result,(_,_)=>throw new IOException("injected_mux_failure"),30);
            using var frame=new Nv12Frame(0);
            Check(sink.Submit(frame) && result.FramesSubmitted==1 && result.EncoderPacketCount==0,"delayed input accepted exactly once without fake packet count");
            Reject(()=>sink.Drain(),"injected_mux_failure","flush propagates session mux failure");
            Check(encoder.CurrentState==EncoderState.Faulted && result.PacketsQueuedForMux==0 && result.EncoderErrors==1,
                "mux failure faults flush and cannot count a queued packet");
        }
        using(var encoder=new QsvEncoderBackend(target.Adapter))
        {
            Reject(()=>encoder.Initialize(Config("cbr")),"cbr_known_failure","invalid config touches no native encoder");
            Check(encoder.CurrentState==EncoderState.Created,"invalid configuration remains retryable");
            encoder.Initialize(Config()); encoder.Start();
            using var first=new Nv12Frame(0);
            encoder.Submit(first).Packet?.Dispose();
            using var duplicate=new Nv12Frame(0);
            Reject(()=>encoder.Submit(duplicate),"pts_not_monotonic","duplicate input is not resubmitted as backpressure");
            var packets=new List<EncodedPacket>(); encoder.Flush(packets.Add);
            Check(encoder.NativeLockedSurfaces==0 && encoder.NativeOutstandingPackets==0,"error validation leaves owned surfaces drainable");
            foreach(var packet in packets) packet.Dispose(); encoder.Stop();
            encoder.Start();
            using var restart=new Nv12Frame(0); encoder.Submit(restart).Packet?.Dispose();
            encoder.Flush(p=>p.Dispose()); encoder.Stop();
            Check(encoder.CurrentState==EncoderState.Stopped,"same native instance supports a fresh timestamp epoch after stop/start");
        }
        using(var factory=new OwnedFrameFactory(target))
        using(var engine=new RecordingEngine(Log(),factory))
        {
            engine.Initialize(Startup(target,"vbr")); var owned=new List<Nv12Frame>();
            var failed=engine.StartOwnedFrameSession(SessionConfig("native-fault-partial"),Inputs(target,owned,3));
            Check(!failed.Pass && failed.FinalizationState=="Failed" && !failed.FullDecodeVerified,"stale-generation partial session cannot claim Saved");
            Check(failed.ErrorMessage.Contains("stale_source_generation"),"router rejects old source generation before encoding");
            Check(owned.All(f=>f.DisposeCount==1),"fault path returns all yielded source ownership");
            var recoveredFrames=new List<Nv12Frame>();
            var recovered=engine.StartOwnedFrameSession(SessionConfig("native-recovered"),Inputs(target,recoveredFrames));
            Check(recovered.Pass,"new session recovers from previous native pending-output fault: "+recovered.ErrorMessage);
            Check(recovered.SessionId!=failed.SessionId,"recovery uses new session identity without hidden mid-session restart");
            Check(factory.Encoders.Count==2,"faulted native encoder replaced through original RecordingEngine factory");
            File.WriteAllText(Path.Combine(Evidence,"native-recovery-result.json"),JsonSerializer.Serialize(recovered,new JsonSerializerOptions{WriteIndented=true}));
        }
        for(int i=0;i<3;++i)
        {
            using var encoder=new QsvEncoderBackend(target.Adapter); encoder.Initialize(Config()); encoder.Start();
            using var frame=new Nv12Frame(0); encoder.Submit(frame).Packet?.Dispose();
            encoder.Flush(p=>p.Dispose()); encoder.Stop();
        }
        Check(true,"three additional native create/submit/flush/destroy cycles complete without access violation");
        var wrong=new AdapterIdentity(target.Adapter.Low^0x40000000u,target.Adapter.High,target.Adapter.VendorId,target.Adapter.DeviceId,false,target.Adapter.DevicePath);
        using(var encoder=new QsvEncoderBackend(wrong))
        {
            Reject(()=>encoder.Initialize(Config()),"EnumAdapterByLuid","missing actual LUID does not fall back to default GPU");
            Check(encoder.CurrentState==EncoderState.Faulted,"failed native initialization is terminal until disposed");
        }
    }

    // Test-owned NV12 source; creates only a D3D11 device for actual identity
    // evidence. It has no Desktop Duplication/WGC/Audio call or MP4 ownership.
    sealed class OwnedFrameSource(CaptureTarget target,int width=320,int height=180) : IVideoCaptureRuntime, IVideoBackendDiagnostics
    {
        ID3D11Device? device;
        ID3D11DeviceContext? context;
        public ID3D11Device OwnedDevice=>device ?? throw new ObjectDisposedException(nameof(OwnedFrameSource));
        public AdapterIdentity DeviceIdentity{get;private set;}=null!;
        public bool Started;
        public CaptureTarget ActualTarget=>target;
        public int OutputWidth=>width; public int OutputHeight=>height; public int OutputRefreshRate=>30;
        public long AccessLostCount=>0; public long TexturesCreated=>0; public long TexturesDisposed=>0;
        public long FramesPushed=>0; public long EmittedFrames=>0; public long DroppedFrames=>0;
        public long ReplacedFrames=>0; public long NoFrameCount=>0; public long ErrorCount=>0;
        public IVideoBackendDiagnostics Diagnostics=>this;
        public void Initialize(IVideoBackendContext ctx)
        {
            using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var snapshot=DxgiDisplayInventory.Collect();
            uint index=(uint)DxgiDisplayInventory.ResolveAdapterIndex(target,snapshot);
            factory.EnumAdapters1(index,out IDXGIAdapter1 adapter).CheckError();
            using(adapter)
                D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport|DeviceCreationFlags.VideoSupport,
                    new[]{FeatureLevel.Level_11_0},out device,out context).CheckError();
            using var dxgi=device.QueryInterface<IDXGIDevice>();
            using var parent=dxgi.GetParent<IDXGIAdapter>(); var d=parent.Description;
            DeviceIdentity=new(d.Luid.LowPart,d.Luid.HighPart,d.VendorId,d.DeviceId,false,target.Adapter.DevicePath);
            if(!target.Adapter.SameDevice(DeviceIdentity)) throw new Exception("owned_source_actual_device_mismatch");
        }
        public void Start(IVideoFrameSink sink) { Started=true; throw new Exception("owned source cannot start desktop acquisition"); }
        public void Stop() { }
        public void Dispose() { context?.Dispose(); device?.Dispose(); context=null; device=null; }
    }
    sealed class OwnedFrameFactory(CaptureTarget target,int width=320,int height=180) : IRecordingBackendFactory, IDisposable
    {
        readonly RecordingBackendFactory real=new();
        public OwnedFrameSource? Source;
        public List<QsvEncoderBackend> Encoders=new();
        public CaptureCompositionPlan Plan(EngineStartupConfig startup)
        {
            var planned=real.Plan(startup);
            // This explicitly implemented fixture supplies owned NV12, not the
            // unsupported default DXGI BGRA pair. All identity checks remain.
            return new(planned.Target,new EncoderProvider("intel:qsv-h264",GpuVendor.Intel,true,false,"QUICKSYNC_H264"));
        }
        public void ValidateTarget(CaptureCompositionPlan plan)=>real.ValidateTarget(plan);
        public IVideoCaptureRuntime CreateCapture(CaptureCompositionPlan plan,bool cursor,EngineLogger log)=>Source=new(target,width,height);
        public IEncoderRuntimeBackend CreateEncoder(CaptureCompositionPlan plan,EngineLogger log)
        {
            var encoder=(QsvEncoderBackend)real.CreateEncoderForInput(plan,VideoPixelFormat.Nv12,log);
            Encoders.Add(encoder); return encoder;
        }
        public void Dispose() { Source?.Dispose(); foreach(var encoder in Encoders) encoder.Dispose(); }
    }
}
