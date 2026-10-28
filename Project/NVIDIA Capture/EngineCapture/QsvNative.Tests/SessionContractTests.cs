using DulukaEngine.Diagnostics;
using DulukaEngine.Encoder;
using DulukaEngine.Media;
using DulukaEngine.Recording;
using DulukaEngine.Video;

static partial class Program
{
    static CaptureTarget ContractTarget(bool attached = true, string topology = "contract-topology") => new(
        new AdapterIdentity(123, 0, 0x8086, 0x1234, false, @"\\?\PCI#VEN_8086&DEV_1234#test#{id}"),
        0, "contract-display", (nint)1, 320, 180, 0, 0, 1, attached, 0x80000000, topology, "contract-monitor");

    sealed class ContractFrame(long sequence, long pts, long? qpc = null) : INv12VideoFrame
    {
        public VideoFrameOrigin Origin => VideoFrameOrigin.CpuMemory;
        public VideoPixelFormat PixelFormat => VideoPixelFormat.Nv12;
        public VideoFrameDimensions Dimensions => new(320, 180);
        public FrameDiagnostics Diagnostics => new(sequence, qpc ?? pts, pts);
        public ReadOnlyMemory<byte> Data { get; } = new byte[320 * 180 * 3 / 2];
        public int Stride => 320;
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public void Dispose() { IsDisposed = true; ++DisposeCount; }
    }

    sealed class ContractEncoder : IEncoderBackend, IEncoderSubmissionBackend, IEncoderDiagnostics
    {
        public Func<IVideoFrame, EncoderSubmission>? OnSubmit;
        public readonly List<EncodedPacket> Delayed = new();
        public int Starts, FlushCalls;
        public EncoderState CurrentState { get; private set; } = EncoderState.Initialized;
        public IEncoderDiagnostics Diagnostics => this;
        public long SubmittedFrames { get; private set; }
        public long EncodedPackets { get; private set; }
        public long DroppedFrames => 0;
        public long FlushCycles => FlushCalls;
        public long ErrorCount => 0;
        public string LastErrorIfAny => "";
        public string LastErrorType => "";
        public void Initialize(EncoderConfig config) { }
        public void Start() { ++Starts; CurrentState = EncoderState.Running; }
        public EncoderSubmission Submit(IVideoFrame frame)
        {
            var value = OnSubmit?.Invoke(frame) ?? new EncoderSubmission(true, null);
            if(value.Accepted) ++SubmittedFrames;
            if(value.Packet is not null) ++EncodedPackets;
            return value;
        }
        public bool Encode(IVideoFrame frame, ref EncodedPacket packet) { var r = Submit(frame); packet = r.Packet!; return packet is not null; }
        public int Flush(Action<EncodedPacket> sink) { ++FlushCalls; foreach(var packet in Delayed) sink(packet); return Delayed.Count; }
        public void Stop() { CurrentState = EncoderState.Stopped; }
        public void Dispose() { CurrentState = EncoderState.Disposed; }
    }

    sealed class ContractCapture : IVideoCaptureRuntime
    {
        public CaptureTarget ActualTarget => ContractTarget();
        public int OutputWidth => 320;
        public int OutputHeight => 180;
        public int OutputRefreshRate => 30;
        public int Starts;
        public long AccessLostCount=>0;
        public long TexturesCreated=>0;
        public long TexturesDisposed=>0;
        public long FramesPushed=>0;
        public IVideoBackendDiagnostics Diagnostics => throw new Exception("contract must not read capture diagnostics");
        public void Initialize(IVideoBackendContext context) => throw new Exception("contract must not initialize a device");
        public void Start(IVideoFrameSink sink) { ++Starts; throw new Exception("contract must not start capture"); }
        public void Stop() { }
        public void Dispose() { }
    }

    static EncodedPacket ContractPacket(long sequence, long? pts = null) => new(
        new PacketMetadata(sequence, pts ?? sequence * 10_000_000 / 30, pts ?? sequence * 10_000_000 / 30,
            333333, sequence == 0, true, "H264", 0), new byte[]{0,0,0,1,0x65}, 5);

    static SessionResult VerifiedContractResult() => new() {
        FramesSubmitted=2, EncoderPacketCount=2, FramesEncoded=2, PacketsQueuedForMux=2, MuxedPacketCount=2,
        DecodedFrameCount=2, FullDecodeVerified=true, MediaVerificationRequired=true,
        FileExists=true, FileSize=100, VideoStreamFound=true
    };

    static void SessionContracts()
    {
        var target = ContractTarget();
        Check(target.Eligible, "contract target is test-owned metadata only");
        var router = new SessionFrameRouter(target, 30, 1, 320, 180);
        using(var detached = new SessionFrameInput(new ContractFrame(0,0),ContractTarget(false),1))
            Reject(()=>router.Route(detached), "source_target_mismatch", "detached same-identity output is rejected");
        using(var changed = new SessionFrameInput(new ContractFrame(0,0),ContractTarget(topology:"new-topology"),1))
            Reject(()=>router.Route(changed), "source_target_mismatch", "changed topology is rejected before routing");
        Reject(()=>router.AuthorizeGeneration(1,2,null!), "generation_transition_not_authorized", "null generation target rejected explicitly");
        var disposed = new ContractFrame(0,0); disposed.Dispose();
        using(var input = new SessionFrameInput(disposed,target,1))
            Reject(()=>router.Route(input), "frame_disposed", "disposed NV12 cannot reach encoder");
        using(var first = new SessionFrameInput(new ContractFrame(0,0,10000000),target,1)) {
            var view=router.RouteSource(first);
            Check(((IColorVideoFrame)view).Color is null, "uncolored borrowed source view does not throw a cast error");
            view.Dispose();
            Check(!((ContractFrame)first.Frame).IsDisposed, "borrowed view never disposes source payload");
        }
        router.AuthorizeGeneration(1,2,target);
        using(var recovered = new SessionFrameInput(new ContractFrame(0,0,10333333),target,2))
            Check(router.RouteSource(recovered).Diagnostics.PresentationTimestampTicks==333333,
                "authorized source ordinal reset preserves single session timeline");
        using(var old = new SessionFrameInput(new ContractFrame(1,666666,10666666),target,1))
            Reject(()=>router.RouteSource(old), "stale_source_generation", "stale callback rejected after generation transition");

        using(var encoder = new ContractEncoder()) {
            var result = new SessionResult(); var progress = new List<(long,long,long)>();
            var feed = new List<byte[]>();
            var sink = new SessionPacketSink(encoder,result,(bytes,count)=>feed.Add(bytes.Take(count).ToArray()),30,
                (submitted,encoded,queued)=>progress.Add((submitted,encoded,queued)));
            using var first = new ContractFrame(0,0); using var second = new ContractFrame(1,333333);
            Check(sink.Submit(first) && sink.Submit(second) && result.FramesSubmitted==2 && result.EncoderPacketCount==0,
                "accepted delayed frames advance submission independently of packets");
            encoder.Delayed.Add(ContractPacket(0)); encoder.Delayed.Add(ContractPacket(1));
            sink.Drain(); sink.Drain();
            Check(encoder.FlushCalls==1 && result.FlushedPackets==2 && result.PacketsQueuedForMux==2 && feed.Count==2,
                "drain is idempotent and delivers every delayed packet exactly once");
            Check(encoder.Delayed.All(p=>p.DisposeCount==1), "drained packet payloads have one session owner");
            Check(progress.Last()==(2L,2L,2L), "published progress separates accepted, encoded and queued counts");
            Reject(()=>sink.Submit(first), "packet_sink_terminal", "terminal sink cannot accept a new frame after drain");
        }
        using(var encoder = new ContractEncoder()) {
            var result = new SessionResult(); var packet = ContractPacket(0);
            encoder.OnSubmit = _=>new(true,packet);
            var sink = new SessionPacketSink(encoder,result,(_,_)=>throw new IOException("injected_sink_failure"),30);
            using var frame = new ContractFrame(0,0);
            Reject(()=>sink.Submit(frame), "injected_sink_failure", "mux failure propagates without losing packet ownership");
            Check(packet.DisposeCount==1 && result.EncoderErrors==1 && result.PacketsQueuedForMux==0,
                "failed mux feed cannot increment delivered count or leak packet");
            Reject(()=>sink.Drain(), "packet_sink_faulted", "failed packet delivery cannot be retried as a successful drain");
        }
        using(var encoder = new ContractEncoder()) {
            var result = new SessionResult(); var sink = new SessionPacketSink(encoder,result,(_,_)=>{},30);
            using var frame = new ContractFrame(0,0); sink.Submit(frame);
            Reject(()=>sink.Drain(), "drain_frame_count_mismatch", "missing delayed output fails before mux completion");
            Check(result.EncoderErrors==1, "missing drain output is a session encoder error");
        }
        Check(VerifiedContractResult().Pass, "complete decoded packet conservation passes");
        foreach(Action<SessionResult> corrupt in new Action<SessionResult>[] {
            r=>r.MuxedPacketCount=1,r=>r.PacketsQueuedForMux=1,r=>r.FramesEncoded=1,
            r=>r.DecodedFrameCount=1,r=>r.EncoderPacketCount=1,r=>r.FramesSubmitted=0,
            r=>r.MediaVerificationPending=true,
            r=>r.FinalizationState="Failed",r=>r.FinalizationState="Partial",r=>r.FinalizationState="Cancelled"}) {
            var result = VerifiedContractResult(); corrupt(result);
            Check(!result.Pass, "incomplete packet evidence or unsuccessful terminal state never reports Saved");
        }
        var log=new EngineLogger("session-contract",EngineLogger.LogLevel.Error,_=>{});
        using(var capture = new ContractCapture()) using(var encoder = new ContractEncoder()) {
            using var session = new CaptureSession(capture,encoder,new SessionConfig {
                OutputPath=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".mp4"),TargetFps=30,DurationSeconds=1,
                EncodeWidth=320,EncodeHeight=180,AudioEnabled=true,MicEnabled=false },log);
            var rejected=session.RunOwnedFrames(Array.Empty<SessionFrameInput>(),1);
            Check(rejected.SessionId==session.SessionId && rejected.AudioRequested && rejected.FinalizationState=="Failed" && !rejected.Pass,
                "unsupported owned audio returns authoritative session failure with requested policy");
            Check(capture.Starts==0 && encoder.Starts==0 && session.RealFramesCapturedThisSession==0,
                "audio rejection opens no capture device, encoder or real-frame progress");
        }
        using(var capture = new ContractCapture()) using(var encoder = new ContractEncoder()) {
            using var session = new CaptureSession(capture,encoder,new SessionConfig {
                OutputPath=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".mp4"),TargetFps=30,DurationSeconds=1,
                EncodeWidth=320,EncodeHeight=180,AudioEnabled=false,MicEnabled=false },log);
            session.Stop();
            var cancelled=session.RunOwnedFrames(Array.Empty<SessionFrameInput>(),1);
            Check(cancelled.FinalizationState=="Cancelled" && cancelled.SessionId==session.SessionId && !cancelled.Pass,
                "cancelled before start keeps session identity and cannot report Saved");
            Check(capture.Starts==0 && encoder.Starts==0, "early cancellation starts no source, encoder, audio or mux");
        }
        Check(!SessionMediaVerifier.Verify("missing", "missing",3601,320,180,30).Verified,
            "unbounded full-decode expectation rejected before spawning a media tool");
    }

    static void SessionMediaSynthetic()
    {
        Directory.CreateDirectory(Evidence);
        string ffmpeg=Path.Combine(Repo(),"Project","FFmpeg","ffmpeg.exe");
        string video=Path.Combine(Evidence,"session-verifier-video.mp4");
        string av=Path.Combine(Evidence,"session-verifier-audio-video.mp4");
        void Generate(string path,bool audio) {
            var start=new System.Diagnostics.ProcessStartInfo(ffmpeg) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
            var args=new List<string>{"-nostdin","-y","-v","error","-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=2"};
            if(audio) args.AddRange(new[]{"-f","lavfi","-i","sine=frequency=900:sample_rate=48000:duration=2"});
            args.AddRange(new[]{"-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-bf","0","-pix_fmt","yuv420p"});
            if(audio) args.AddRange(new[]{"-c:a","aac"}); else args.Add("-an");
            args.AddRange(new[]{"-t","2","-movflags","+faststart",path});
            foreach(var arg in args) start.ArgumentList.Add(arg);
            using var child=System.Diagnostics.Process.Start(start)!;
            var errors=child.StandardError.ReadToEndAsync(); var output=child.StandardOutput.ReadToEndAsync();
            if(!child.WaitForExit(30000)) { child.Kill(true); child.WaitForExit(2000); throw new Exception("synthetic_generator_timeout"); }
            output.GetAwaiter().GetResult();
            Check(child.ExitCode==0,"test-owned lavfi media generated: "+errors.GetAwaiter().GetResult());
        }
        Generate(video,false); Generate(av,true);
        var clean=SessionMediaVerifier.Verify(video,ffmpeg,60,320,180,30);
        Check(clean.Verified && clean.DecodedFrames==60 && clean.Packets==60,"strict CFR video conserves 60 decoded access units: "+clean.Reason);
        var sound=SessionMediaVerifier.VerifyNative(av,ffmpeg,60,320,180,30,1);
        Check(sound.Verified && sound.DecodedFrames==60 && sound.Packets==60,"native media verification accepts exactly requested synthetic AAC track: "+sound.Reason);
        Check(!SessionMediaVerifier.VerifyNative(av,ffmpeg,60,320,180,30,0).Verified,"unrequested audio stream fails native acceptance");
        Check(!SessionMediaVerifier.VerifyNative(video,ffmpeg,60,320,180,30,1).Verified,"missing requested audio stream fails native acceptance");
        Check(!SessionMediaVerifier.VerifyNative(video,ffmpeg,59,320,180,30,0).Verified,"one missing decoded frame fails acceptance");
        Check(!SessionMediaVerifier.VerifyNative(video,ffmpeg,60,640,360,30,0).Verified,"wrong output surface dimensions fail acceptance");
        int hooks=0;
        var hook=SessionMediaVerifier.Verify(video,ffmpeg,60,320,180,30,_=>{++hooks; throw new IOException("injected_registration_failure");});
        Check(hook.Verified && hooks==1,"throwing optional process registration retains verifier child ownership");
        string broken=Path.Combine(Evidence,"session-verifier-truncated.mp4");
        var bytes=File.ReadAllBytes(video); File.WriteAllBytes(broken,bytes[..(bytes.Length/2)]);
        Check(!SessionMediaVerifier.VerifyNative(broken,ffmpeg,60,320,180,30,0).Verified,"truncated MP4 with preserved headers cannot claim Saved");
        string rounded=Path.Combine(Evidence,"session-verifier-native-rounded-timebase.mp4");
        var roundingStart=new System.Diagnostics.ProcessStartInfo(ffmpeg) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var arg in new[]{"-nostdin","-y","-v","error","-i",video,"-map","0:v:0","-c:v","copy",
            "-bsf:v","setts=prescale=1:time_base=1/1200000:pts=N*40000:dts=N*40000:duration=39999",rounded}) roundingStart.ArgumentList.Add(arg);
        using(var child=System.Diagnostics.Process.Start(roundingStart)!) {
            var errors=child.StandardError.ReadToEndAsync(); var output=child.StandardOutput.ReadToEndAsync();
            if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("rounded_synthetic_generation_timeout"); }
            output.GetAwaiter().GetResult();
            Check(child.ExitCode==0,"test-owned rounded container timebase fixture generated: "+errors.GetAwaiter().GetResult());
        }
        var roundedEvidence=SessionMediaVerifier.VerifyNative(rounded,ffmpeg,60,320,180,30,0);
        Check(roundedEvidence.Verified && roundedEvidence.DecodedFrames==60 && roundedEvidence.Packets==60,
            "native rate accepts at most one actual container timestamp tick of rounding per frame: "+roundedEvidence.Reason);
        Check(!SessionMediaVerifier.Verify(rounded,ffmpeg,60,320,180,30).Verified,
            "strict owned CFR acceptance still rejects rounded cadence metadata");
        string longer=Path.Combine(Evidence,"session-verifier-native-3601-frames.mp4");
        var longStart=new System.Diagnostics.ProcessStartInfo(ffmpeg) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var arg in new[]{"-nostdin","-y","-v","error","-f","lavfi","-i","testsrc2=size=32x32:rate=60",
            "-frames:v","3601","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-bf","0","-an",longer}) longStart.ArgumentList.Add(arg);
        using(var child=System.Diagnostics.Process.Start(longStart)!) {
            var errors=child.StandardError.ReadToEndAsync(); var output=child.StandardOutput.ReadToEndAsync();
            if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("long_synthetic_generation_timeout"); }
            output.GetAwaiter().GetResult();
            Check(child.ExitCode==0,"test-owned 3601-frame native compatibility fixture generated: "+errors.GetAwaiter().GetResult());
        }
        var longEvidence=SessionMediaVerifier.VerifyNative(longer,ffmpeg,3601,32,32,60,0);
        Check(longEvidence.Verified && !longEvidence.VerificationPending && longEvidence.DecodedFrames==3601 && longEvidence.Packets==3601,
            "native verification has no 3600-frame rejection and fully decodes/counts longer output: "+longEvidence.Reason);
        Check(!longEvidence.PacketTimelineVerified,"long native verification exposes aggregate acceptance without claiming detailed packet timeline scan");
        SessionOwnedLifecycleSynthetic(video,ffmpeg);
    }

    static void SessionOwnedLifecycleSynthetic(string video,string ffmpeg)
    {
        string raw=Path.Combine(Evidence,"session-owned-source.h264");
        var start=new System.Diagnostics.ProcessStartInfo(ffmpeg) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        foreach(var arg in new[]{"-nostdin","-y","-v","error","-i",video,"-map","0:v:0","-c:v","copy",
            "-bsf:v","h264_mp4toannexb","-f","h264",raw}) start.ArgumentList.Add(arg);
        using(var child=System.Diagnostics.Process.Start(start)!) {
            var errors=child.StandardError.ReadToEndAsync(); var output=child.StandardOutput.ReadToEndAsync();
            if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("synthetic_remux_timeout"); }
            output.GetAwaiter().GetResult();
            Check(child.ExitCode==0,"test-owned H264 elementary stream extracted: "+errors.GetAwaiter().GetResult());
        }
        var probe=new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var arg in new[]{"-v","error","-f","h264","-show_packets","-show_entries","packet=pos,size","-of","json",raw})
            probe.ArgumentList.Add(arg);
        var accessUnits=new List<byte[]>(); var data=File.ReadAllBytes(raw);
        using(var child=System.Diagnostics.Process.Start(probe)!) {
            var output=child.StandardOutput.ReadToEndAsync(); var errors=child.StandardError.ReadToEndAsync();
            if(!child.WaitForExit(30000)) { child.Kill(true); throw new Exception("synthetic_packet_scan_timeout"); }
            using var doc=System.Text.Json.JsonDocument.Parse(output.GetAwaiter().GetResult());
            Check(child.ExitCode==0 && errors.GetAwaiter().GetResult().Length==0,"test-owned access units packet-scanned");
            foreach(var packet in doc.RootElement.GetProperty("packets").EnumerateArray()) {
                int position=int.Parse(packet.GetProperty("pos").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                int length=int.Parse(packet.GetProperty("size").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                accessUnits.Add(data[position..(position+length)]);
            }
        }
        Check(accessUnits.Count==60,"fixture contains 60 actual complete H264 access units");
        void RunCase(string label,int inputCount,int badGeneration=-1,int stopAfter=-1)
        {
            using var capture=new ContractCapture(); using var encoder=new ContractEncoder();
            var frames=new List<ContractFrame>();
            encoder.OnSubmit=frame=> {
                int index=(int)frame.Diagnostics.Sequence;
                encoder.Delayed.Add(new EncodedPacket(new PacketMetadata(index,index*10_000_000L/30,index*10_000_000L/30,
                    333333,index==0,true,"H264",0),accessUnits[index].ToArray(),accessUnits[index].Length));
                return new EncoderSubmission(true,null);
            };
            var config=new SessionConfig {
                OutputPath=Path.Combine(Evidence,"session-owned-"+label+".mp4"),FFmpegPath=ffmpeg,TargetFps=30,DurationSeconds=1,
                EncodeWidth=320,EncodeHeight=180,AudioEnabled=false,MicEnabled=false };
            using var session=new CaptureSession(capture,encoder,config,new EngineLogger("owned-contract",EngineLogger.LogLevel.Error,_=>{}));
            IEnumerable<SessionFrameInput> Source() {
                for(int index=0;index<inputCount;++index) {
                    if(index==stopAfter) session.Stop();
                    var frame=new ContractFrame(index,index*10_000_000L/30); frames.Add(frame);
                    yield return new SessionFrameInput(frame,capture.ActualTarget,index==badGeneration?2:1);
                }
            }
            var result=session.RunOwnedFrames(Source(),1);
            File.WriteAllText(Path.Combine(Evidence,"session-owned-"+label+"-result.json"),
                System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Check(frames.All(f=>f.DisposeCount==1),label+": all yielded source envelopes disposed exactly once");
            Check(encoder.Delayed.All(p=>p.DisposeCount==1) && encoder.FlushCalls==1,label+": delayed packets drained once before Stop");
            Check(capture.Starts==0 && session.RealFramesCapturedThisSession==0 && result.FramesCaptured==0,
                label+": synthetic owned input never starts or claims desktop capture");
            Check(result.FullDecodeVerified && result.DecodedFrameCount==result.FramesSubmitted && result.MuxedPacketCount==result.FramesSubmitted,
                label+": finalized output fully decodes every accepted delayed input: "+result.ErrorMessage);
            if(label=="complete") Check(result.Pass && result.FinalizationState=="Saved", "complete session reaches Saved after actual decode");
            else if(label=="cancelled") Check(!result.Pass && result.FinalizationState=="Cancelled", "mid-source cancellation retains verified partial without success");
            else Check(!result.Pass && result.FinalizationState=="Partial",label+": verified incomplete output is explicitly Partial");
            Check(session.FramesEncodedThisSession==result.EncoderPacketCount && session.PacketsQueuedForMuxThisSession==result.PacketsQueuedForMux,
                label+": published packet progress matches terminal evidence");
        }
        RunCase("complete",30);
        RunCase("source-fault",30,badGeneration:3);
        RunCase("early-eof",10);
        RunCase("cancelled",30,stopAfter:10);
    }
}
