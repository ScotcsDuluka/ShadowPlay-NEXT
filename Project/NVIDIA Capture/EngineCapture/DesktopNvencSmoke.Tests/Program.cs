using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using DulukaEngine.Encoder;
using DulukaEngine.Encoder.Nvenc;
using DulukaEngine.Video;

// Isolated opt-in NVENC Normal-Path smoke, never invoked by NvCapture.
// Reuses the read-only D3D11 synthetic GPU frame fixture already present
// in Encoder.Tests via reflection. Never invokes its fault-injection, race,
// double-destroy, or stress scenarios.
if(args.Length!=3)throw new ArgumentException("expected isolated evidence directory + test-only ffmpeg + ffprobe");
string dir=Path.GetFullPath(args[0]);
Directory.CreateDirectory(dir);
string evidence=Path.Combine(dir,"nvenc-synthetic-passthrough.h264");
if(File.Exists(evidence))throw new IOException("evidence already exists (no overwrite)");
int checks=0;
void Check(bool value,string msg){
    if(!value)throw new Exception("NVENC_SMOKE_FAIL "+msg);
    Console.WriteLine("PASS "+msg);checks++;
}
var asm=typeof(NvencEncoderBackend).Assembly;
var fixture=asm.GetType("DulukaEngine.Encoder.Tests.Lifecycle.NvencNativeFaultTests",true)!;
MethodInfo M(string name)=>fixture.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)
    ??throw new MissingMethodException(fixture.FullName,name);
object? Call(string name,params object?[] parameters)
{
    try{return M(name).Invoke(null,parameters);}
    catch(TargetInvocationException error) when(error.InnerException is not null){
        throw error.InnerException;
    }
}
Check(File.Exists(Path.Combine(AppContext.BaseDirectory,"DulukaEngine.Encoder.Tests.dll")),
    "isolated test fixture binaries loaded without Capture worker");
Call("Test_HardwareGate");
Check(true,"real NVIDIA D3D11 adapter + NVENC function table hardware gate");
var config=(EncoderConfig)(Call("ValidSmallConfig")??throw new Exception("missing config"));
Check(config.CodecKey=="NVENC_H264" && config.ExpectedWidth==640 &&
    config.ExpectedHeight==480 && config.FrameRateFps==30,
    "safe 640x480 30fps real NVENC backend configuration");
using var backend=(NvencEncoderBackend)(Call("NewBackend",(object)Array.Empty<Action<string>>())
    ??throw new Exception("no encoder backend"));
long totalBytes=0;
int packets=0,keyframes=0;
long previousPts=-1;
using var encoded=new MemoryStream();
var accessUnits=new List<(PacketMetadata Meta,byte[] Payload)>();
var h264Gate=new NvencH264PacketGate();
byte[]? firstAccessUnit=null;
PacketMetadata firstMetadata=default;
var sw=Stopwatch.StartNew();
backend.Initialize(config);
backend.Start();
try
{
    using var gpu=(IDisposable)(Call("CreateDevice")??throw new Exception("GPU device unavailable"));
    for(int i=0;i<12;i++)
    {
        using var frame=(IVideoFrame)(Call("CreateSharedFrame",gpu,640,480,(long)i)
            ??throw new Exception("synthetic GPU frame unavailable"));
        EncodedPacket? packet=null;
        bool written=backend.Encode(frame,ref packet);
        Check(written&&packet is not null&&packet.PayloadLength>0,
            "real NVENC encoded synthetic GPU BGRA frame "+i);
        if(packet is not null)
        {
            try
            {
                Check(packet.Metadata.CodecKey=="NVENC_H264" &&
                    packet.Metadata.Sequence==packets &&
                    packet.Metadata.PresentationTimestampTicks==i*333333L &&
                    packet.Metadata.DecodingTimestampTicks==packet.Metadata.PresentationTimestampTicks &&
                    packet.Metadata.PresentationTimestampTicks>previousPts,
                    "packet ownership/codec/timestamp contract "+i);
                previousPts=packet.Metadata.PresentationTimestampTicks;
                var view=packet.Payload.AsSpan(0,packet.PayloadLength);
                h264Gate.Validate(packet.Metadata,view);
                accessUnits.Add((packet.Metadata,view.ToArray()));
                if(packets==0){firstAccessUnit=view.ToArray();firstMetadata=packet.Metadata;}
                if(packet.Metadata.IsKeyFrame)keyframes++;
                encoded.Write(packet.Payload,0,packet.PayloadLength);
                totalBytes+=packet.PayloadLength;
                packets++;
            }
            finally{
                packet.Dispose();
                Check(packet.DisposeCount==1,"packet released exactly once "+i);
            }
        }
    }
    // Flush is callback-based but this synchronous backend may have zero delayed packets.
    int drain=backend.Flush(packet=>{
        try{
            if(packet.PayloadLength<=0)throw new InvalidDataException("NVENC flush empty packet");
            encoded.Write(packet.Payload,0,packet.PayloadLength);
            totalBytes+=packet.PayloadLength;
            packets++;
        }finally{packet.Dispose();}
    });
    Check(drain>=0&&packets==12&&totalBytes>400,"12 NVENC video packets conserved after flush");
}
finally
{
    try{backend.Stop();}catch(InvalidOperationException){}
}
Check(sw.ElapsedMilliseconds<20000,"bounded 12-frame smoke without encoder fault-injection");
var bitstream=encoded.ToArray();
bool annexB=bitstream.Length>4 && bitstream[0]==0 && bitstream[1]==0 &&
    (bitstream[2]==1 || (bitstream[2]==0 && bitstream[3]==1));
Check(annexB,"NVENC H.264 output is Annex B encoded bitstream");
Check(keyframes>0,"at least one real NVENC keyframe produced");
Check(h264Gate.HasCodecHeaders&&h264Gate.DetectedIdr==1&&h264Gate.AcceptedPackets==12,
    "NVENC first IDR includes native SPS/PPS, 12 monotonic Annex-B access units accepted");
void ExpectRejected(Action test,string description)
{
    try{test();throw new Exception("NVENC_GATE_UNEXPECTED_ACCEPT "+description);}
    catch(InvalidDataException){Check(true,description);}
}
Check(firstAccessUnit is not null,"first encoded NVENC access unit available for negative validation");
byte[] forgedStart=firstAccessUnit!.ToArray();forgedStart[0]=0xFF;
ExpectRejected(()=>new NvencH264PacketGate().Validate(firstMetadata,forgedStart),
    "corrupt H264 first start code rejected");
var forgedMeta=new PacketMetadata(firstMetadata.Sequence+1,
    firstMetadata.PresentationTimestampTicks,firstMetadata.DecodingTimestampTicks,
    firstMetadata.DurationTicks,firstMetadata.IsKeyFrame,
    firstMetadata.IsReferenceFrame,firstMetadata.CodecKey,firstMetadata.CodecSpecificFlags);
ExpectRejected(()=>new NvencH264PacketGate().Validate(forgedMeta,firstAccessUnit),
    "wrong first H264 source sequence rejected");
ExpectRejected(()=>h264Gate.Validate(firstMetadata,firstAccessUnit),
    "duplicate/reordered packet rejected after valid 12-frame session");
var forgedKey=new PacketMetadata(firstMetadata.Sequence,
    firstMetadata.PresentationTimestampTicks,firstMetadata.DecodingTimestampTicks,
    firstMetadata.DurationTicks,false,firstMetadata.IsReferenceFrame,
    firstMetadata.CodecKey,firstMetadata.CodecSpecificFlags);
ExpectRejected(()=>new NvencH264PacketGate().Validate(forgedKey,firstAccessUnit),
    "keyframe flag cannot contradict true IDR bytes");
File.WriteAllBytes(evidence,bitstream);
Check(new FileInfo(evidence).Length==totalBytes,"source bitstream evidence bytes conserved");
string mp4=Path.Combine(dir,"nvenc-native-video-test-only-unverified.mp4");
using(var native=new NativeNvencVideoMp4Experiment(mp4,h264Gate.GetAnnexBSequenceHeader()))
{
    foreach(var item in accessUnits)
        native.Write(item.Meta,item.Payload);
    Check(native.Frames==12,"Media Foundation native MP4 sink received 12 already encoded H264 samples");
    Check(native.FinalizeForTestOnly()==mp4&&File.Exists(mp4),
        "native compressed passthrough MP4 staged as TEST-ONLY evidence (not Production Saved)");
}
Check(new FileInfo(mp4).Length>1000,"test-only native H264 MP4 contains muxed bytes");
IndependentVideoEvidence.Verify(evidence,mp4,args[1],args[2],Check);

// Fail-closed writer state: incomplete and duplicate packets must not leak an
// apparently finished MP4.  Only isolated synthetic evidence files are touched.
string partial=Path.Combine(dir,"nvenc-native-incomplete-must-not-save.mp4");
using(var incomplete=new NativeNvencVideoMp4Experiment(
    partial,h264Gate.GetAnnexBSequenceHeader()))
{
    foreach(var item in accessUnits.Take(11))
        incomplete.Write(item.Meta,item.Payload);
    try{
        incomplete.FinalizeForTestOnly();
        throw new Exception("incomplete NVENC MP4 was accepted");
    }catch(InvalidOperationException){
        Check(true,"native MP4 rejects 11/12 incomplete packet budget");
    }
}
Check(!File.Exists(partial),"partial native MP4 deleted after failed finalization");
string duplicate=Path.Combine(dir,"nvenc-native-duplicate-must-not-save.mp4");
using(var ordered=new NativeNvencVideoMp4Experiment(
    duplicate,h264Gate.GetAnnexBSequenceHeader()))
{
    ordered.Write(accessUnits[0].Meta,accessUnits[0].Payload);
    try{
        ordered.Write(accessUnits[0].Meta,accessUnits[0].Payload);
        throw new Exception("duplicate packet unexpectedly accepted");
    }catch(InvalidDataException){
        Check(true,"native MP4 rejects duplicate H264 packet sequence and PTS");
    }
}
Check(!File.Exists(duplicate),"invalid packet MP4 never retained as a completed output");

// Experimental TWO-TRACK native MP4: retain the original NVENC H264 packet
// payloads and encode only synthetic in-memory stereo PCM through MF AAC.
// Audio and video use one 100ns session timeline. No microphones/screen input.
static short[] AudioTone(int index)
{
    var samples=new short[2048];
    for(int n=0;n<1024;n++)
    {
        double t=(index*1024.0+n)/48000.0;
        samples[2*n]=(short)(6000*Math.Sin(2*Math.PI*440*t));
        samples[2*n+1]=(short)(6000*Math.Sin(2*Math.PI*660*t));
    }
    return samples;
}
string av=Path.Combine(dir,"nvenc-native-two-track-test-only-unverified.mp4");
using(var native=new NativeNvencVideoMp4Experiment(
    av,h264Gate.GetAnnexBSequenceHeader(),nativeAacAudio:true))
{
    int nextPcm=0;
    for(int i=0;i<accessUnits.Count;i++)
    {
        var item=accessUnits[i];
        native.Write(item.Meta,item.Payload);
        // 1600 samples per 30fps frame; packet boundaries are 1024 frames.
        while(nextPcm<19 && nextPcm*1024 < (i+1)*1600)
        {
            native.WritePcm16(AudioTone(nextPcm),nextPcm);
            nextPcm++;
        }
    }
    Check(native.Frames==12 && native.AudioPackets==19,
        "one Native MF Sink Writer owns 12 NVENC H264 and 19 stereo PCM packets");
    Check(!File.Exists(Path.Combine(dir,"nvenc-native-saved.mp4")),
        "native experimental A/V path did not publish a Production Saved file");
    Check(native.FinalizeForTestOnly()==av&&File.Exists(av),
        "native H264+AAC two-track MP4 finalized as test-only evidence");
}
IndependentVideoEvidence.Verify(evidence,av,args[1],args[2],Check,withAudio:true);
NativeMfTwoTrackDecodeEvidence.Verify(av,Check);
NativeNvencAvClockAcceptance.Verify(accessUnits,19,Check);
string shortAv=Path.Combine(dir,"nvenc-native-short-audio-must-not-save.mp4");
using(var partialAv=new NativeNvencVideoMp4Experiment(
    shortAv,h264Gate.GetAnnexBSequenceHeader(),nativeAacAudio:true))
{
    foreach(var item in accessUnits)partialAv.Write(item.Meta,item.Payload);
    for(int i=0;i<18;i++)partialAv.WritePcm16(AudioTone(i),i);
    try
    {
        partialAv.FinalizeForTestOnly();
        throw new Exception("incomplete native AAC packet budget unexpectedly accepted");
    }
    catch(InvalidOperationException)
    {
        Check(true,"Native H264+AAC two-track finalization rejects 18/19 PCM packets");
    }
}
Check(!File.Exists(shortAv),"unverified incomplete A/V MP4 cleaned on Dispose");

// Fail-closed PUBLISH gate, separate from native Sink Writer finalization.
// Neither invalid input nor a decoding failure may create a Verified file.
string badAv=Path.Combine(dir,"native-av-malformed-test-only.mp4");
string badFinal=Path.Combine(dir,"native-av-malformed-must-not-be-verified.mp4");
File.WriteAllBytes(badAv,new byte[4096]);
try
{
    NativeVerifiedEvidencePublisher.Publish(badAv,badFinal,Check);
    throw new Exception("malformed native A/V was promoted");
}
catch(Exception error) when(error is InvalidDataException or InvalidOperationException or IOException)
{
    Check(true,"malformed MP4 is rejected by native Media Foundation acceptance before promotion");
}
Check(!File.Exists(badFinal)&&File.Exists(badAv),
    "malformed MP4 never obtains a Verified destination");
File.Delete(badAv);
// A real, structurally plausible but TRUNCATED copy must also fail closed.
string truncatedAv=Path.Combine(dir,"native-av-truncated-test-only.mp4");
string truncatedFinal=Path.Combine(dir,"native-av-truncated-must-not-be-verified.mp4");
File.Copy(av,truncatedAv);
using(var cut=new FileStream(truncatedAv,FileMode.Open,FileAccess.Write,FileShare.None))
    cut.SetLength(cut.Length-4096);
try
{
    NativeVerifiedEvidencePublisher.Publish(truncatedAv,truncatedFinal,Check);
    throw new Exception("truncated two-track MP4 unexpectedly promoted");
}
catch(Exception error) when(error is InvalidDataException or InvalidOperationException or IOException)
{
    Check(true,"truncated two-track MP4 rejected before Native Verified publication");
}
Check(!File.Exists(truncatedFinal) && File.Exists(truncatedAv),
    "truncated two-track MP4 remains unverified after native decode failure");
File.Delete(truncatedAv);
string verifiedAv=Path.Combine(dir,"nvenc-native-two-track-verified-test-only.mp4");
Check(NativeVerifiedEvidencePublisher.Publish(av,verifiedAv,Check)==verifiedAv &&
    !File.Exists(av)&&File.Exists(verifiedAv),
    "H264+AAC source promoted only after native full-decode and byte-hash verification");
Console.WriteLine("AORUS_NVENC_SYNTHETIC_SMOKE_PASS checks="+checks+
    " encoded_frames="+packets+" bytes="+totalBytes+" keyframes="+keyframes+
    " h264_sha256="+Convert.ToHexString(SHA256.HashData(bitstream))+
    " elapsed_ms="+sw.ElapsedMilliseconds+
    " live_capture=false mic=false production=false ffmpeg_runtime=false");
