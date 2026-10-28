using System.Text.Json;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using DulukaEngine.Encoder;
using DulukaEngine.Encoder.Qsv;

static partial class Program
{
    static int passed;
    static string? evidenceDirectory;
    static void Check(bool condition, string name) {
        if(!condition) throw new Exception("FAIL "+name);
        ++passed; Console.WriteLine("PASS "+name);
    }
    static EncoderConfig Config(string rc="vbr") => new() {
        CodecKey="QUICKSYNC_H264", FFmpegCodec="h264_qsv", RateControl=rc, Preset="veryfast",
        ExpectedInputFormat=VideoPixelFormat.Nv12, ExpectedWidth=320, ExpectedHeight=180,
        EncodeWidth=320, EncodeHeight=180, FrameRateFps=30, GopSize=30,
        BitrateBps=rc=="cqp"?0:2_000_000, MinrateBps=0, MaxrateBps=rc=="cqp"?0:4_000_000,
        BufsizeBps=0, Cq=24, MaxInFlightFrames=4, FlushTimeoutMs=10000
    };
    static void Probe(AdapterIdentity target, string rc)
    {
        using var encoder = new QsvEncoderBackend(target);
        encoder.Initialize(Config(rc));
        Check(encoder.ActualAdapter.SameDevice(target),"native actual QSV device LUID matches physical Intel target");
        Console.WriteLine($"ACTUAL_QSV_IDENTITY {encoder.ActualAdapter} runtime={encoder.RuntimeVersion} implementation={encoder.HardwareImplementation}");
        Directory.CreateDirectory(Evidence);
        File.WriteAllText(Path.Combine(Evidence,"native-device-"+rc+".json"),JsonSerializer.Serialize(new {
            Requested=target.ToString(), Actual=encoder.ActualAdapter.ToString(), Runtime=encoder.RuntimeVersion,
            Implementation=encoder.HardwareImplementation, RateControl=rc, Input="owned CPU NV12", Width=320, Height=180, Fps=30,
            ScreenRead=false, AudioOpened=false },new JsonSerializerOptions{WriteIndented=true}));
        encoder.Start(); var output=new List<EncodedPacket>(); int delayed=0;
        for(int i=0;i<60;++i) {
            using var frame=new Nv12Frame(i);
            var result=encoder.Submit(frame);
            Check(result.Accepted && !frame.IsDisposed,$"frame {i} borrowed and accepted once");
            if(result.Packet is not null) output.Add(result.Packet); else ++delayed;
        }
        int flushed=encoder.Flush(output.Add);
        Check(output.Count==60 && encoder.SubmittedFrames==60 && encoder.EncodedPackets==60,"60 accepted inputs conserved through delayed packet drain");
        Check(encoder.NativeOutstandingPackets==0 && encoder.NativeLockedSurfaces==0,"flush releases all native jobs and surface locks");
        for(int i=0;i<output.Count;++i) {
            var p=output[i];
            Check(p.Metadata.PresentationTimestampTicks==i*10_000_000L/30 &&
                p.Metadata.DecodingTimestampTicks==p.Metadata.PresentationTimestampTicks,"packet PTS/DTS and ordering "+i);
            Check(p.PayloadLength>4 && p.Payload[0]==0 && p.Payload[1]==0,"Annex B session mux transport "+i);
            p.Dispose(); Check(p.DisposeCount==1,"packet sole ownership "+i);
        }
        encoder.Stop();
        Check(encoder.CurrentState==EncoderState.Stopped,"native encoder stops after explicit session drain");
        Console.WriteLine($"QSV_PROBE mode={rc} packets={output.Count} delayed={delayed} flushed={flushed}");
    }
    static int Main(string[] args)
    {
        try {
        int evidenceArg=Array.FindIndex(args,a=>a.Equals("--evidence-dir",StringComparison.OrdinalIgnoreCase));
        if(evidenceArg>=0) {
            if(evidenceArg+1>=args.Length) throw new ArgumentException("evidence_directory_value_required");
            evidenceDirectory=Path.GetFullPath(args[evidenceArg+1]);
        }
        if(args.Contains("--session-contract-only",StringComparer.OrdinalIgnoreCase)) {
            SessionContracts();
            Console.WriteLine($"SESSION_CONTRACT_PASS passed={passed} failed=0 live_desktop=false audio_opened=false gpu_created=false");
            return 0;
        }
        if(args.Contains("--native-source-fault-synthetic-only",StringComparer.OrdinalIgnoreCase)) {
            NativeSourceFaultSynthetic();
            Console.WriteLine($"NATIVE_SOURCE_FAULT_SYNTHETIC_PASS passed={passed} failed=0 live_desktop=false audio_opened=false gpu_created=false");
            return 0;
        }
        if(args.Contains("--session-media-synthetic-only",StringComparer.OrdinalIgnoreCase)) {
            SessionMediaSynthetic();
            Console.WriteLine($"SESSION_MEDIA_SYNTHETIC_PASS passed={passed} failed=0 live_desktop=false audio_opened=false gpu_created=false");
            return 0;
        }
        if(args.Length==7 && args[0]=="--verify-existing-media") {
            var media=DulukaEngine.Media.SessionMediaVerifier.VerifyNative(args[1],SessionConfig("unused").FFmpegPath,
                long.Parse(args[2]),int.Parse(args[3]),int.Parse(args[4]),int.Parse(args[5]),int.Parse(args[6]));
            Console.WriteLine(JsonSerializer.Serialize(media,new JsonSerializerOptions{WriteIndented=true}));
            return media.Verified?0:1;
        }
        // Environment gate: this suite probes the REAL native QSV runtime and
        // therefore requires a physical eligible Intel output. On machines
        // without one it must SKIP truthfully (like Video.Tests HardwareGate),
        // never crash with an enumeration exception.
        var intelOutputs=DxgiDisplayInventory.Collect().Outputs.Where(t=>t.Eligible && t.Adapter.Vendor==GpuVendor.Intel).ToList();
        if(intelOutputs.Count==0) {
            Console.WriteLine("QSV_NATIVE_SKIP reason=no_eligible_intel_output screen_pixels_read=false audio_opened=false");
            return 0;
        }
        var target=intelOutputs.Single(t=>t.Eligible && t.Adapter.Vendor==GpuVendor.Intel);
        if(args.Contains("--i3c",StringComparer.OrdinalIgnoreCase)) {
            I3C(target);
            return 0;
        }
        if(args.Contains("--i3b",StringComparer.OrdinalIgnoreCase)) {
            I3B(target);
            Console.WriteLine($"I3B_PASS passed={passed} failed=0 live_desktop=false audio_opened=false"); return 0;
        }
        if(args.Contains("--i3-transport",StringComparer.OrdinalIgnoreCase)) {
            I3PixelTransport(target);
            I3D3D11Transport(target);
            I3FrameAdapter(target);
            Console.WriteLine($"I3_SAFE_TRANSPORT_PASS passed={passed} failed=0 live_desktop=false audio_opened=false");
            return 0;
        }
        Probe(target.Adapter,"vbr"); Probe(target.Adapter,"cqp");
        SessionAcceptance(target, "vbr"); SessionAcceptance(target, "cqp");
        FailureAndRecovery(target);
        Console.WriteLine($"QSV_NATIVE_PASS passed={passed} failed=0 screen_pixels_read=false audio_opened=false"); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    internal sealed class Nv12Frame : INv12VideoFrame
    {
        readonly byte[] bytes;
        public Nv12Frame(int sequence) {
            bytes=new byte[320*180*3/2];
            Array.Fill(bytes,(byte)128);
            // Moving luma marker and sequence bits are test-owned data.
            for(int y=0;y<180;++y) for(int x=0;x<320;++x)
                bytes[y*320+x]=(byte)((x/16==sequence%20)?210:32+(sequence%16)*4);
            Diagnostics=new(sequence,sequence*10_000_000L/30,sequence*10_000_000L/30);
        }
        public VideoFrameOrigin Origin=>VideoFrameOrigin.CpuMemory;
        public VideoPixelFormat PixelFormat=>VideoPixelFormat.Nv12;
        public VideoFrameDimensions Dimensions=>new(320,180);
        public FrameDiagnostics Diagnostics{get;}
        public ReadOnlyMemory<byte> Data=>!IsDisposed?bytes:throw new ObjectDisposedException(nameof(Nv12Frame));
        public int Stride=>320;
        public bool IsDisposed{get;private set;}
        public int DisposeCount {get;private set;}
        public void Dispose() { ++DisposeCount; if(!IsDisposed) { IsDisposed=true; Array.Clear(bytes); } }
    }
}
