// Phase I3 safety gate. Test-owned BGRA with padded row pitch -> CPU NV12 ->
// SAME Duluka RecordingSession -> real Intel QSV -> existing mux -> decoded MP4.
// No DXGI desktop acquisition, real screen pixels, system audio or microphone.
using System.Diagnostics;
using DulukaEngine.Diagnostics;
using DulukaEngine.Recording;
using DulukaEngine.Video;

static partial class Program
{
    static string I3Evidence => evidenceDirectory ?? Path.Combine(Repo(),"docs","reports","intel-duluka-i3-2026-10-25");

    static void I3PixelTransport(CaptureTarget target)
    {
        Directory.CreateDirectory(I3Evidence);
        I3ConversionContract();
        var frames = new List<ConvertedBgraFrame>();
        using var factory = new OwnedFrameFactory(target);
        using var engine = new RecordingEngine(
            new EngineLogger("I3-controlled-BGRA",EngineLogger.LogLevel.Warning,_=>{}),factory);
        engine.Initialize(Startup(target,"vbr"));
        Check(factory.Source!.DeviceIdentity.SameDevice(target.Adapter),
            "I3 source actual D3D11 device matches exact Intel physical LUID");
        var conf=SessionConfig("I3-unused");
        conf.OutputPath=Path.Combine(I3Evidence,"bgra-stride-to-qsv-synthetic.mp4");
        var result=engine.StartOwnedFrameSession(conf,ConvertedFrames(target,frames));
        Check(result.Pass,$"I3 converted input reuses same session/mux and verifies MP4: {result.ErrorMessage}");
        Check(result.FinalizationState=="Saved" && result.FullDecodeVerified &&
              result.DecodedFrameCount==60 && result.MuxedPacketCount==60 &&
              result.EncoderPacketCount==60 && result.FlushedPackets>0,
              "I3 60/60 decoded, muxed, encoder flush complete");
        Check(result.FramesCaptured==0 && result.SyntheticFramesConsumed==60 &&
              !factory.Source.Started,"I3 synthetic remains distinct from real desktop capture");
        Check(frames.Count==60 && frames.All(f=>f.IsDisposed && f.DisposeCount==1),
              "I3 converted frame source owns/disposes each packet exactly once");
        Check(result.VideoIdentity.CaptureLuidLow==target.Adapter.Low &&
              result.VideoIdentity.EncoderLuidLow==target.Adapter.Low,
              "I3 source/encoder verified same physical Intel LUID");
        VerifyI3MovingMarker(result.OutputPath);
        File.WriteAllText(Path.Combine(I3Evidence,"controlled-session-result.json"),
              System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"I3_CONVERSION_SESSION_PASS session={result.SessionId} input=owned_padded_bgra output=owned_nv12 decoded={result.DecodedFrameCount} flushed={result.FlushedPackets} liveCapture=false");
    }

    static void I3ConversionContract()
    {
        var bgra = new byte[2*16]; // 2x2, 8 real BGRA bytes + 8 padding per row
        Array.Fill(bgra,(byte)0xE7);
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)
        {
            int p=y*16+x*4; bgra[p]=0; bgra[p+1]=0; bgra[p+2]=0; bgra[p+3]=255;
        }
        var black=BgraToNv12Converter.ConvertTopDown(bgra,2,2,16);
        Check(black.Length==6 && black.Take(4).All(v=>v==16) &&
              black[4]==128 && black[5]==128,
              "I3 BT.709 limited black luma/chroma ignores nonzero stride padding");
        for(int y=0;y<2;y++)for(int x=0;x<2;x++) {
            int p=y*16+x*4; bgra[p]=255;bgra[p+1]=255;bgra[p+2]=255;bgra[p+3]=0;
        }
        var white=BgraToNv12Converter.ConvertTopDown(bgra,2,2,16);
        Check(white.Take(4).All(v=>v==235) && white[4]==128 && white[5]==128,
              "I3 BT.709 limited white ignores alpha and stride poison");
        var colored=BgraToNv12Converter.ConvertTopDown(
            new byte[]{255,0,0,255,255,0,0,255,255,0,0,255,255,0,0,255},2,2,8);
        Check(colored[4]>128 && colored[5]<128,"I3 blue pixels yield expected UV ordering");
        foreach(var vector in new[]{(R:255,G:0,B:0,Y:63,U:102,V:240),(R:0,G:255,B:0,Y:173,U:42,V:26),(R:0,G:0,B:255,Y:32,U:240,V:118)}) {
            var rgb=Enumerable.Range(0,4).SelectMany(_=>new[]{(byte)vector.B,(byte)vector.G,(byte)vector.R,(byte)255}).ToArray();
            var nv12=BgraToNv12Converter.ConvertTopDown(rgb,2,2,8);
            Check(nv12.Take(4).All(y=>y==vector.Y) && nv12[4]==vector.U && nv12[5]==vector.V,"I3 BT.709 limited exact RGB vector "+vector);
        }
        var mixed=BgraToNv12Converter.ConvertTopDown(new byte[]{0,0,255,0,0,255,0,0,255,0,0,0,0,0,0,0},2,2,8);
        Check(mixed.SequenceEqual(new byte[]{63,173,32,16,128,128}),"I3 2x2 box chroma averages all four RGB pixels");
        Check(black.Take(4).All(y=>y==16),"I3 converted output owns its bytes independently of mutated BGRA input");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(null!,2,2,16),"bgra_buffer_short_for_pitch","I3 null BGRA input fails closed");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(bgra,0,2,16),"positive_even_dimensions","I3 zero output width rejected before allocation");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(bgra,3,2,16),
              "nv12_requires_positive_even_dimensions","I3 rejects odd width");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(bgra,2,3,16),
              "nv12_requires_positive_even_dimensions","I3 rejects odd height");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(bgra,2,2,7),
              "bgra_row_pitch_too_small","I3 rejects short source pitch");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(new byte[20],2,2,16),
              "bgra_buffer_short_for_pitch","I3 rejects truncated pitched BGRA rows");
        Reject(()=>BgraToNv12Converter.ConvertTopDown(bgra,16384,16384,int.MaxValue),
              "frame_allocation_overflow","I3 rejects overflowing row offsets");
    }

    static IEnumerable<SessionFrameInput> ConvertedFrames(CaptureTarget target,List<ConvertedBgraFrame> owned)
    {
        for(int i=0;i<60;i++) {
            var frame=new ConvertedBgraFrame(i); owned.Add(frame);
            yield return new SessionFrameInput(frame,target,1);
        }
    }

    static void VerifyI3MovingMarker(string output,int width=320,int height=180)
    {
        var psi=new ProcessStartInfo(SessionConfig("unused").FFmpegPath) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var arg in new[]{"-v","error","-xerror","-i",output,"-an","-pix_fmt","gray","-f","rawvideo","pipe:1"})
            psi.ArgumentList.Add(arg);
        using var child=Process.Start(psi)!;
        using var data=new MemoryStream();
        var pipe=child.StandardOutput.BaseStream.CopyToAsync(data);
        var err=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(30000)){child.Kill(true);throw new Exception("I3 decode timeout");}
        pipe.GetAwaiter().GetResult();
        Check(child.ExitCode==0 && string.IsNullOrEmpty(err.GetAwaiter().GetResult()),
              "I3 decoded MP4 contains no stream corruption");
        var decoded=data.ToArray();
        Check(decoded.Length==width*height*60,"I3 decoded exactly 60 whole grayscale frames");
        for(int i=0;i<60;i++) {
            int baseIdx=i*width*height+90*width;
            int marker=i%20*16+8, away=((i%20+10)%20)*16+8;
            int mark=decoded[baseIdx+marker],other=decoded[baseIdx+away];
            Check(mark>other+50,"I3 moving synthetic BGRA marker survives QSV encode/decode frame="+i);
        }
    }

    sealed class ConvertedBgraFrame : INv12VideoFrame
    {
        readonly byte[] bytes;
        public ConvertedBgraFrame(int seq)
        {
            const int width=320,height=180,pitch=320*4+32;
            var bgra=new byte[pitch*height];
            Array.Fill(bgra,(byte)0xA5);
            for(int y=0;y<height;y++)
              for(int x=0;x<width;x++) {
                int p=y*pitch+x*4;
                byte c=(byte)(x/16==seq%20?220:40+(seq%16)*3);
                bgra[p]=c; bgra[p+1]=c; bgra[p+2]=c; bgra[p+3]=0;
              }
            bytes=BgraToNv12Converter.ConvertTopDown(bgra,width,height,pitch);
            Array.Clear(bgra); // borrowed original rows no longer owned
            Diagnostics=new FrameDiagnostics(seq,seq*10_000_000L/30,seq*10_000_000L/30);
        }
        public VideoFrameOrigin Origin=>VideoFrameOrigin.CpuMemory;
        public VideoPixelFormat PixelFormat=>VideoPixelFormat.Nv12;
        public VideoFrameDimensions Dimensions=>new(320,180);
        public FrameDiagnostics Diagnostics {get;}
        public ReadOnlyMemory<byte> Data=>!IsDisposed?bytes:throw new ObjectDisposedException(nameof(ConvertedBgraFrame));
        public int Stride=>320;
        public bool IsDisposed{get;private set;}
        public int DisposeCount{get;private set;}
        public void Dispose(){ if(IsDisposed)return;IsDisposed=true;DisposeCount++;Array.Clear(bytes); }
    }
}
