using System.Diagnostics;
using System.Text.Json;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using DulukaEngine.Video.Transport.D3D11;
using Vortice.Direct3D11;

static partial class Program
{
    static string I3BEvidence=>evidenceDirectory ?? Path.Combine(Repo(),"docs/reports/intel-duluka-i3b-2026-10-25");
    const long QpcOrigin=50_000_000_000;
    static D3D11VideoFrame Stamped(ID3D11Device device,CaptureTarget target,long generation,int index,int? sequence=null,long? qpc=null)
    {
        using var synthetic=new OwnedTextureFrame(device,index);
        return new D3D11VideoFrame(((ID3D11Texture2D)synthetic.NativeTexture).QueryInterface<ID3D11Texture2D>(),
            TextureWidth,TextureHeight,sequence ?? index,qpc ?? QpcOrigin+index*10_000_000L/30,
            qpc ?? QpcOrigin+index*10_000_000L/30,IntPtr.Zero,target,generation);
    }
    static void WaitFor(Func<bool> condition,string message) {
        if(!SpinWait.SpinUntil(condition,TimeSpan.FromSeconds(15))) throw new Exception("test_deadline:"+message);
    }
    static void I3B(CaptureTarget target)
    {
        Directory.CreateDirectory(I3BEvidence);
        Check(typeof(D3D11VideoFrame).GetConstructor(new[]{typeof(ID3D11Texture2D),typeof(int),typeof(int),typeof(long),typeof(long),typeof(long),typeof(IntPtr)}) is not null &&
            typeof(SessionFrameInput).GetConstructor(new[]{typeof(IVideoFrame),typeof(CaptureTarget),typeof(long)}) is not null &&
            typeof(BgraFrameAdapter).GetConstructor(new[]{typeof(CaptureTarget),typeof(long),typeof(int),typeof(int),typeof(int)}) is not null,
            "I3B preserves original frame/envelope/adapter constructor signatures for existing binaries");
        Check(BgraSessionSource.QpcTo100ns(123456789,3000000)==411522630,"I3B raw DXGI QPC conversion uses supplied frequency, not TimeSpan/QPC subtraction");
        Check(BgraSessionSource.QpcTo100ns(-1,3000000)==-3,"I3B signed acquire/present QPC diagnostic delta preserves legacy truncation");
        foreach(var vector in new[]{(R:0,G:0,B:0,Y:0,U:128,V:128),(R:255,G:255,B:255,Y:255,U:128,V:128),
            (R:255,G:0,B:0,Y:54,U:99,V:255),(R:0,G:255,B:0,Y:182,U:30,V:12),(R:0,G:0,B:255,Y:18,U:255,V:116)}) {
            var rgb=Enumerable.Range(0,4).SelectMany(_=>new[]{(byte)vector.B,(byte)vector.G,(byte)vector.R,(byte)255}).ToArray();
            var nv12=BgraToNv12Converter.ConvertTopDown(rgb,2,2,8,true);
            Check(nv12.Take(4).All(y=>y==vector.Y) && nv12[4]==vector.U && nv12[5]==vector.V,"I3B full-range RGB conversion exact vector "+vector);
        }
        foreach(bool full in new[]{false,true}) foreach(string rc in new[]{"vbr","cqp"}) LifecycleSession(target,rc,full,rc=="vbr" && !full);
        LifecycleFaults(target);
    }
    static void LifecycleSession(CaptureTarget target,string rc,bool full,bool recover)
    {
        using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
        using var engine=new RecordingEngine(I3Log(),factory);
        var startup=Startup(target,rc); startup.Color=new(full); engine.Initialize(startup);
        ProtectSourceContext(factory.Source!.OwnedDevice);
        using var source=new BgraSessionSource(target,7,TextureWidth,TextureHeight,30,new(full),waitTimeoutMs:10000);
        string key=$"{rc}-{(full?"full":"limited")}";
        var conf=SessionConfig("unused"); conf.OutputPath=Path.Combine(I3BEvidence,"lifecycle-"+key+".mp4");
        var running=Task.Run(()=>engine.StartSourceSession(conf,source));
        var frames=new List<D3D11VideoFrame>();
        try {
            for(int i=0;i<60;++i) {
                if(recover && i==30) {
                    source.SourceFault(SourceFaultKind.AccessLost);
                    source.AuthorizeRecovery(target,8);
                    Check(source.Recoveries==1,"I3B explicit AccessLost authorization preserves same session/device and timeline");
                }
                long gen=recover && i>=30?8:7; int seq=recover && i>=30?i-30:i;
                var frame=Stamped(factory.Source.OwnedDevice,target,gen,i,seq); frames.Add(frame);
                var outcome=source.TryPush(FrameAcquisitionResult.Available(frame,seq,frame.Diagnostics.CaptureTimeTicks));
                if(outcome!=PushOutcome.Pushed) { frame.Dispose(); throw new Exception("test_source_rejected:"+source.State); }
                WaitFor(()=>source.RoutedFrames>=i+1 || running.IsCompleted,"routed frame "+i);
                if(running.IsCompleted && source.RoutedFrames<i+1) throw new Exception("session_early:"+running.Result.ErrorMessage);
            }
            source.Complete();
            WaitFor(()=>running.IsCompleted,"session finalize");
            var result=running.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(I3BEvidence,"lifecycle-"+key+"-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Check(result.Pass,"I3B lifecycle through original session and full decoder "+key+": "+result.ErrorMessage);
            Check(result.DecodedFrameCount==60 && result.FramesSubmitted==60 && result.MuxedPacketCount==60 && result.EncoderPacketCount==60,"I3B conserves 60 source/encoder/mux/decoded frames "+key);
            Check(result.SessionId.Length==32 && result.FinalizationState=="Saved" && result.FlushedPackets>0,"I3B single session + delayed flush saved "+key);
            Check(result.FramesCaptured==0 && result.SyntheticFramesConsumed==60 && !factory.Source.Started,"I3B no live source or audio start "+key);
            Check(frames.All(f=>f.IsDisposed && f.DisposeCount==1) && source.RawFramesDisposed==60,"I3B source-to-adapter-to-session ownership ends exactly once "+key);
            Check(factory.Encoders[0].NativeOutstandingPackets==0 && factory.Encoders[0].NativeLockedSurfaces==0,"I3B drain releases all native ownership "+key);
            VerifyI3MovingMarker(result.OutputPath,TextureWidth,TextureHeight);
            VerifyColor(result.OutputPath,key,full);
            File.WriteAllText(Path.Combine(I3BEvidence,"lifecycle-"+key+"-source.json"),JsonSerializer.Serialize(new {
                source.Accepted,source.Rejected,source.QueueDrops,source.SequenceGaps,source.DuplicateFrames,source.Recoveries,
                source.RawFramesDisposed,source.RoutedFrames,Adapter=factory.Source.DeviceIdentity.ToString(),
                Encoder=factory.Encoders[0].ActualAdapter.ToString(),Runtime=factory.Encoders[0].RuntimeVersion,
                target.OutputIndex,SourceGeneration=7,FinalGeneration=recover?8:7,full,source.State
            },new JsonSerializerOptions{WriteIndented=true}));
        } finally { engine.Stop(); source.Stop(); WaitFor(()=>running.IsCompleted,"cleanup session"); }
    }
    static void VerifyColor(string mp4,string key,bool full,string? outputDirectory=null)
    {
        var evidence=outputDirectory ?? I3BEvidence;
        string raw=Path.Combine(evidence,"lifecycle-"+key+".h264");
        RunTool(SessionConfig("unused").FFmpegPath,new[]{"-y","-v","error","-i",mp4,"-map","0:v:0","-c","copy","-bsf:v","h264_mp4toannexb",raw});
        foreach(string path in new[]{mp4,raw}) {
            string probe=Path.Combine(Repo(),"Project/FFmpeg/ffprobe.exe");
            string body=RunTool(probe,new[]{"-v","error","-show_streams","-of","json",path});
            File.WriteAllText(Path.Combine(evidence,Path.GetFileName(path)+"-color.json"),body);
            using var parsed=JsonDocument.Parse(body); var video=parsed.RootElement.GetProperty("streams")[0];
            Check(video.GetProperty("color_range").GetString()==(full?"pc":"tv") &&
                new[]{"color_space","color_transfer","color_primaries"}.All(name=>video.GetProperty(name).GetString()=="bt709"),
                "I3B explicit BT.709 "+(full?"full":"limited")+" reaches "+Path.GetExtension(path)+" (elementary stream proves VUI)");
        }
    }
    static string RunTool(string tool,IEnumerable<string> args)
    {
        var psi=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in args) psi.ArgumentList.Add(arg);
        using var child=Process.Start(psi)!; var output=child.StandardOutput.ReadToEndAsync(); var error=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("media_tool_deadline"); }
        if(child.ExitCode!=0 || error.GetAwaiter().GetResult().Length>0) throw new Exception("media_tool_failed:"+error.Result);
        return output.GetAwaiter().GetResult();
    }
    static void LifecycleFaults(CaptureTarget target)
    {
        using var deviceSource=new OwnedFrameSource(target,TextureWidth,TextureHeight); deviceSource.Initialize(null!);
        ProtectSourceContext(deviceSource.OwnedDevice);
        var device=deviceSource.OwnedDevice;
        foreach(var kind in new[]{SourceFaultKind.Disconnected,SourceFaultKind.DeviceRemoved,SourceFaultKind.OutputChanged}) {
            using var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false));
            var pending=Stamped(device,target,1,0); Check(source.TryPush(FrameAcquisitionResult.Available(pending,0,0))==PushOutcome.Pushed,"I3B fault fixture pending frame transferred");
            source.SourceFault(kind);
            Reject(()=>source.Read(),"source_fault:","I3B injected "+kind+" stops read without device fallback");
            Check(pending.IsDisposed && pending.DisposeCount==1,"I3B "+kind+" returns queued GPU ownership");
            Reject(()=>source.AuthorizeRecovery(target,2),"not_authorized","I3B terminal "+kind+" requires new validated session");
        }
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false))) {
            source.SourceFault(SourceFaultKind.AccessLost); source.SourceFault(SourceFaultKind.DeviceRemoved);
            Reject(()=>source.AuthorizeRecovery(target,2),"not_authorized","I3B fatal device removal escalates earlier AccessLost instead of authorizing recovery");
        }
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false),capacity:1)) {
            var first=Stamped(device,target,1,0); source.TryPush(FrameAcquisitionResult.Available(first,0,0));
            using(var dropped=Stamped(device,target,1,1)) {
                Check(source.TryPush(FrameAcquisitionResult.Available(dropped,1,0))==PushOutcome.Dropped && !dropped.IsDisposed,"I3B bounded queue drop retains caller ownership");
            }
            source.Stop(); Check(first.IsDisposed && source.RawFramesDisposed==1 && source.QueueDrops==1,"I3B Stop disposes pending queue without double release");
        }
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false))) {
            var read=Task.Run(()=>{try { source.Read(); return false; } catch(OperationCanceledException){return true;} });
            source.Stop(); WaitFor(()=>read.IsCompleted,"idle stop"); Check(read.Result,"I3B Stop interrupts waiting read");
        }
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false),waitTimeoutMs:30))
            Reject(()=>source.Read(),"frame_wait_deadline","I3B absent/delayed source has a bounded wait deadline");
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false))) {
            var router=new SessionFrameRouter(target,30,1,TextureWidth,TextureHeight);
            var raw=Stamped(device,target,1,0); source.TryPush(FrameAcquisitionResult.Available(raw,0,0)); using(var cpu=source.Read()!.Frame) { }
            using(var initial=Stamped(device,target,1,0)) {
                // Router consumes converted frames, with QPC still untouched.
                using var adapter=new BgraFrameAdapter(target,1,TextureWidth,TextureHeight);
                var cpu=adapter.Convert(initial,target,1);
                using var envelope=new SessionFrameInput(cpu,target,1);
                Check(router.RouteSource(envelope).Diagnostics.PresentationTimestampTicks==0,"I3B session router owns QPC epoch selection");
            }
            source.SourceFault(SourceFaultKind.AccessLost); source.AuthorizeRecovery(target,2);
            var stale=Stamped(device,target,1,1);
            Check(source.TryPush(FrameAcquisitionResult.Available(stale,1,0))==PushOutcome.Dropped && !stale.IsDisposed,"I3B old generation callback cannot acquire ownership after authorized recovery"); stale.Dispose();
            var current=Stamped(device,target,2,1); source.TryPush(FrameAcquisitionResult.Available(current,1,0));
            {
                var cpu=source.Read()!.Frame;
                router.AuthorizeGeneration(1,2,target);
                using var envelope=new SessionFrameInput(cpu,target,2);
                Check(router.RouteSource(envelope).Diagnostics.PresentationTimestampTicks==333333,"I3B stale callback dropped; session router keeps original origin after authorized generation");
            }
        }
        foreach(string invalid in new[]{"duplicate","delayed","generation","output","device"}) {
            using var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false));
            var first=Stamped(device,target,1,0); source.TryPush(FrameAcquisitionResult.Available(first,0,0)); using(var accepted=source.Read()!.Frame) { }
            var other=invalid=="output"?new CaptureTarget(target.Adapter,target.OutputIndex+1,target.DeviceName,target.Monitor,
                target.Width,target.Height,target.Left,target.Top,target.Rotation,true,target.OutputTechnology,target.TopologyId,target.MonitorDevicePath):target;
            var bad=Stamped(device,other,invalid=="generation"?2:1,1,invalid=="duplicate"?0:1,
                invalid=="delayed"?QpcOrigin+10_000_000:QpcOrigin+333333);
            if(invalid=="device") {
                bad.Dispose(); using var foreign=new OwnedFrameSource(target,TextureWidth,TextureHeight); foreign.Initialize(null!); ProtectSourceContext(foreign.OwnedDevice);
                bad=Stamped(foreign.OwnedDevice,target,1,1); source.TryPush(FrameAcquisitionResult.Available(bad,1,0));
                Reject(()=>source.Read(),"device_changed","I3B same-LUID device migration rejected");
            } else {
                var pushed=source.TryPush(FrameAcquisitionResult.Available(bad,1,0));
                if(pushed==PushOutcome.Dropped) bad.Dispose();
                if(invalid=="generation") Check(source.Rejected==1 && source.RoutedFrames==1,"I3B generation mismatch is dropped without resetting timestamp origin");
                else if(invalid=="delayed") {
                    var cpu=source.Read()!.Frame;
                    var router=new SessionFrameRouter(target,30,1,TextureWidth,TextureHeight);
                    using var adapter=new BgraFrameAdapter(target,1,TextureWidth,TextureHeight);
                    using var firstRaw=Stamped(device,target,1,0);
                    var normalized=adapter.Convert(firstRaw,target,1);
                    using var firstInput=new SessionFrameInput(normalized,target,1);
                    router.RouteSource(firstInput);
                    using var delayed=new SessionFrameInput(cpu,target,1);
                    Reject(()=>router.RouteSource(delayed),"timestamp_gap","I3B delayed source cannot compress or restart the session timeline");
                }
                else Reject(()=>source.Read(),invalid=="duplicate"?"duplicate_or_reordered":"source_fault:","I3B rejects "+invalid+" without rewriting timeline");
            }
            Check(bad.IsDisposed,"I3B invalid "+invalid+" frame has a terminal owner");
        }
        SessionFaultAndStop(target);
        using(var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false))) {
            var raw=Stamped(device,target,1,0); source.TryPush(FrameAcquisitionResult.Available(raw,0,0));
            using var context=device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            using var protection=context.QueryInterface<ID3D11Multithread>();
            protection.Enter();
            Task<bool>? reader=null; Task? disposing=null;
            try {
                reader=Task.Run(()=>{try{using var cpu=source.Read()!.Frame;return false;}catch(OperationCanceledException){return true;}});
                WaitFor(()=>source.NativeDeviceBound,"conversion owns device before shutdown");
                disposing=Task.Run(source.Dispose);
                WaitFor(()=>source.State=="Stopped","shutdown cancellation issued");
                Check(!raw.IsDisposed && !disposing.IsCompleted,"I3B concurrent Dispose waits for borrowed texture/conversion before releasing adapter COM state");
            } finally { protection.Leave(); }
            WaitFor(()=>reader!.IsCompleted && disposing!.IsCompleted,"conversion/shutdown acknowledgment");
            Check(reader!.Result && raw.IsDisposed && raw.DisposeCount==1 && source.State=="Disposed","I3B shutdown cancels in-flight adapter and releases GPU frame exactly once");
        }
    }
    static void SessionFaultAndStop(CaptureTarget target)
    {
        foreach(bool stop in new[]{false,true}) foreach(bool pending in new[]{false,true}) {
            using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
            using var engine=new RecordingEngine(I3Log(),factory); var cfg=Startup(target,"vbr"); cfg.Color=new(false); engine.Initialize(cfg);
            using var source=new BgraSessionSource(target,1,TextureWidth,TextureHeight,30,new(false),waitTimeoutMs:10000);
            var session=SessionConfig("unused"); session.OutputPath=Path.Combine(I3BEvidence,stop?"cancelled.mp4":"faulted.mp4");
            var run=Task.Run(()=>engine.StartSourceSession(session,source));
            WaitFor(()=>engine.GetStatus().CurrentSessionId is not null,"session preparing");
            if(pending) {
                ProtectSourceContext(factory.Source!.OwnedDevice);
                var frame=Stamped(factory.Source.OwnedDevice,target,1,0); source.TryPush(FrameAcquisitionResult.Available(frame,0,0));
                WaitFor(()=>source.RoutedFrames==1,"pending frame read");
            }
            if(stop) engine.Stop(); else source.SourceFault(SourceFaultKind.DeviceRemoved);
            WaitFor(()=>run.IsCompleted,"fault/cancel session"); var result=run.Result;
            Check(!result.Pass && result.FinalizationState==(stop?"Cancelled":"Failed"),"I3B original session reports truthful "+(stop?"cancel":"device loss"));
            source.Dispose(); source.Dispose(); Check(source.State=="Disposed","I3B adapter dispose/shutdown idempotent after session termination");
            if(!stop && pending) {
                using var next=new BgraSessionSource(target,2,TextureWidth,TextureHeight,30,new(false),waitTimeoutMs:10000);
                var nextConfig=SessionConfig("unused"); nextConfig.OutputPath=Path.Combine(I3BEvidence,"validated-new-session.mp4");
                var recover=Task.Run(()=>engine.StartSourceSession(nextConfig,next));
                try {
                    for(int i=0;i<60;++i) {
                        var frame=Stamped(factory.Source!.OwnedDevice,target,2,i);
                        if(next.TryPush(FrameAcquisitionResult.Available(frame,i,0))!=PushOutcome.Pushed) {frame.Dispose();throw new Exception("recovery fixture rejected");}
                        WaitFor(()=>next.RoutedFrames>=i+1 || recover.IsCompleted,"new session recovery frame");
                        if(recover.IsCompleted && next.RoutedFrames<i+1) throw new Exception(recover.Result.ErrorMessage);
                    }
                    next.Complete(); WaitFor(()=>recover.IsCompleted,"new validated session finalize");
                    Check(recover.Result.Pass && recover.Result.DecodedFrameCount==60 && recover.Result.SessionId!=result.SessionId,"I3B terminal device-loss signal recovers only in new fully verified session");
                    Check(factory.Encoders.Count==2,"I3B original engine factory validates/replaces faulted encoder; no hidden source device creation");
                } finally {engine.Stop();next.Stop();WaitFor(()=>recover.IsCompleted,"recovery cleanup");}
            }
        }
    }
}
