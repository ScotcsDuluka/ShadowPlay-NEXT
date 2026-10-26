using System.Text.Json;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using DulukaEngine.Video.Transport.D3D11;
using Vortice.Direct3D11;

static partial class Program
{
    static string I3CEvidence => evidenceDirectory ?? Path.Combine(Repo(), "docs/reports/intel-duluka-i3c-2026-10-25/A-evidence");
    sealed class SyntheticCompositionProducer : ISourceCompositionProducer
    {
        readonly Func<int,D3D11VideoFrame>? frameFactory;
        readonly CancellationTokenSource stopping = new();
        readonly int frameCount;
        readonly SourceFaultKind? injectFault;
        Task? worker;
        int stops;
        public CaptureTarget ActualTarget { get; }
        public bool IsTestOwnedSynthetic { get; set; } = true;
        public long Generation { get; set; } = 87;
        public int PrepareCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount => Volatile.Read(ref stops);
        public bool Disposed { get; private set; }
        public int FramesPushed { get; private set; }
        public SyntheticCompositionProducer(CaptureTarget target,Func<int,D3D11VideoFrame>? frameFactory=null,
            int frameCount=0,SourceFaultKind? fault=null) {
            ActualTarget=target; this.frameFactory=frameFactory; this.frameCount=frameCount; injectFault=fault;
        }
        public long PrepareSourceGeneration() { ++PrepareCount; return Generation; }
        public void Start(IVideoFrameSink sink) {
            if(++StartCount!=1) throw new Exception("synthetic_producer_started_twice");
            worker=Task.Run(()=>{
                var source=(BgraSessionSource)sink;
                if(injectFault.HasValue) {
                    source.SourceFault(injectFault.Value);
                    return;
                }
                for(int i=0;i<frameCount && !stopping.IsCancellationRequested;++i) {
                    var raw=frameFactory!(i);
                    var transferred=false;
                    try {
                        var outcome=sink.TryPush(FrameAcquisitionResult.Available(raw,i,raw.Diagnostics.CaptureTimeTicks));
                        transferred=outcome==PushOutcome.Pushed || outcome==PushOutcome.Replaced;
                        if(!transferred) throw new Exception("composition_synthetic_unexpected_drop");
                        ++FramesPushed;
                    } finally { if(!transferred) raw.Dispose(); }
                    WaitFor(()=>stopping.IsCancellationRequested || source.RoutedFrames>=i+1 ||
                        source.State.StartsWith("Faulted",StringComparison.Ordinal),"composition routed "+i);
                    if(source.State.StartsWith("Faulted",StringComparison.Ordinal))
                        throw new Exception("composition_early_source_fault:"+source.State);
                }
                if(!stopping.IsCancellationRequested) source.Complete();
            });
        }
        public void Stop() {
            if(Interlocked.Exchange(ref stops,1)!=0) return;
            stopping.Cancel();
            // Fake worker is cooperative; never wait on any Ddagrab thread here.
            if(worker is not null && !worker.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("composition_synthetic_worker_join_timeout");
        }
        public void Dispose() { Stop(); stopping.Dispose(); Disposed=true; }
    }

    static void I3C(CaptureTarget target)
    {
        Directory.CreateDirectory(I3CEvidence);
        foreach(string rateControl in new[]{"vbr","cqp"}) foreach(bool full in new[]{false,true})
            CompositionSession(target,rateControl,full);
        CompositionNegatives(target);
        Console.WriteLine($"I3C_SYNTHETIC_ONLY_PASS passed={passed} failed=0 live_desktop=false audio_opened=false");
    }

    static void CompositionSession(CaptureTarget target,string rateControl,bool full)
    {
        using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
        using var engine=new RecordingEngine(I3Log(),factory);
        var startup=Startup(target,rateControl); startup.Color=new(full);
        engine.Initialize(startup);
        var device=factory.Source!.OwnedDevice;
        ProtectSourceContext(device);
        var producer=new SyntheticCompositionProducer(target,i=>Stamped(device,target,87,i),60);
        var driver=SourceCompositionDriver.Prepare(engine,producer,target,TextureWidth,TextureHeight,30,new(full),waitTimeoutMs:10000);
        string key=rateControl+"-"+(full?"full":"limited");
        try {
            Check(driver.ReservedGeneration==87 && producer.PrepareCount==1 && !factory.Source.Started,
                "I3C exact target and generation reserved without DXGI acquisition "+key);
            var session=SessionConfig("i3c-unused");
            session.OutputPath=Path.Combine(I3CEvidence,"composition-"+key+".mp4");
            var result=driver.Run(session);
            File.WriteAllText(Path.Combine(I3CEvidence,"composition-"+key+".json"),
                JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Check(result.Pass && result.FinalizationState=="Saved" && result.FullDecodeVerified &&
                result.FramesSubmitted==60 && result.EncoderPacketCount==60 && result.MuxedPacketCount==60 &&
                result.DecodedFrameCount==60,"I3C producer driver => original session => 60/60 full MP4 decode "+key+": "+result.ErrorMessage);
            Check(result.SyntheticFramesConsumed==60 && result.FramesCaptured==0 && producer.FramesPushed==60 &&
                driver.Source.RawFramesDisposed==60 && !factory.Source.Started,
                "I3C producer handoff/ownership conservation; zero real screen/audio "+key);
            Check(producer.StartCount==1 && producer.StopCount==1 &&
                factory.Encoders[0].NativeLockedSurfaces==0 && factory.Encoders[0].NativeOutstandingPackets==0,
                "I3C deterministic producer stop and native flush "+key);
            VerifyI3MovingMarker(result.OutputPath,TextureWidth,TextureHeight);
            VerifyColor(result.OutputPath,"i3c-"+key,full,I3CEvidence);
        } finally { driver.Dispose(); }
        Check(producer.Disposed && driver.Source.State=="Disposed",
            "I3C owns and releases synthetic producer/source once "+key);
    }

    static void CompositionNegatives(CaptureTarget target)
    {
        using var factory=new OwnedFrameFactory(target,TextureWidth,TextureHeight);
        using var engine=new RecordingEngine(I3Log(),factory);
        var startup=Startup(target,"vbr"); startup.Color=new(false); engine.Initialize(startup);
        var invalid=Retarget(target,output:target.OutputIndex+1);
        var wrong=new SyntheticCompositionProducer(invalid);
        Reject(()=>SourceCompositionDriver.Prepare(engine,wrong,target,TextureWidth,TextureHeight,30,new(false)),
            "composition_target_identity_mismatch","I3C rejects producer on wrong output before reservation");
        Check(wrong.PrepareCount==0 && wrong.StartCount==0,"I3C wrong output never reserves or starts source");
        wrong.Dispose();
        var neverLive=new SyntheticCompositionProducer(target){IsTestOwnedSynthetic=false};
        Reject(()=>SourceCompositionDriver.Prepare(engine,neverLive,target,TextureWidth,TextureHeight,30,new(false)),
            "intel_native_live_input_not_verified","I3C live production gate stays CLOSED");
        neverLive.Dispose();
        var wrongGeneration=new SyntheticCompositionProducer(target){Generation=0};
        Reject(()=>SourceCompositionDriver.Prepare(engine,wrongGeneration,target,TextureWidth,TextureHeight,30,new(false)),
            "composition_invalid_source_generation","I3C refuses missing producer generation");
        wrongGeneration.Dispose();
        var staleTarget=new CaptureTarget(target.Adapter,target.OutputIndex,target.DeviceName,target.Monitor,
            target.Width,target.Height,target.Left,target.Top,target.Rotation,target.Attached,target.OutputTechnology,
            target.TopologyId,target.MonitorDevicePath+"-stale");
        var staleProducer=new SyntheticCompositionProducer(staleTarget);
        Reject(()=>SourceCompositionDriver.Prepare(engine,staleProducer,staleTarget,TextureWidth,TextureHeight,30,new(false)),
            "capture_target_stale_or_ambiguous","I3C current topology must verify against read-only DXGI inventory");
        staleProducer.Dispose();
        ProtectSourceContext(factory.Source!.OwnedDevice);
        var noFrames=new SyntheticCompositionProducer(target);
        using(var driver=SourceCompositionDriver.Prepare(engine,noFrames,target,TextureWidth,TextureHeight,30,new(false),waitTimeoutMs:10000)) {
            using var stale=Stamped(factory.Source.OwnedDevice,target,driver.ReservedGeneration-1,0);
            Check(driver.Source.TryPush(FrameAcquisitionResult.Available(stale,0,stale.Diagnostics.CaptureTimeTicks))==PushOutcome.Dropped &&
                !stale.IsDisposed && driver.Source.Rejected==1,
                "I3C stale callback rejected; producer retains dropped texture ownership");
            var config=SessionConfig("i3c-unused"); config.OutputPath=Path.Combine(I3CEvidence,"composition-cancelled.mp4");
            var run=Task.Run(()=>driver.Run(config));
            WaitFor(()=>noFrames.StartCount>0,"synthetic empty producer started");
            driver.Stop();
            WaitFor(()=>run.IsCompleted,"I3C idle cancellation");
            var result=run.GetAwaiter().GetResult();
            Check(!result.Pass && result.FinalizationState=="Cancelled" && driver.Source.State=="Stopped",
                "I3C Stop cancels pending read through original session");
        }
        var fatal=new SyntheticCompositionProducer(target,fault:SourceFaultKind.DeviceRemoved);
        using(var driver=SourceCompositionDriver.Prepare(engine,fatal,target,TextureWidth,TextureHeight,30,new(false),waitTimeoutMs:10000)) {
            var config=SessionConfig("i3c-unused"); config.OutputPath=Path.Combine(I3CEvidence,"composition-faulted.mp4");
            var result=driver.Run(config);
            Check(!result.Pass && result.FinalizationState=="Failed" &&
                result.ErrorMessage.Contains("source_fault",StringComparison.Ordinal),
                "I3C injected device removal fails session without GPU fallback or fake media success");
            Reject(()=>driver.Source.AuthorizeRecovery(target,driver.ReservedGeneration+1),"not_authorized",
                "I3C terminal fault cannot authorize same-session generation recovery");
        }
    }
}
