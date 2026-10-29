using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// TEST HARNESS ONLY. External ffprobe/ffmpeg run after native writer finalized.
/// They are NOT used by NVENC, Windows Media Foundation or Duluka Runtime.
/// </summary>
internal static class IndependentVideoEvidence
{
    private static string Run(string exe,params string[] args)
    {
        if(!File.Exists(exe))throw new FileNotFoundException("test_tool_absent",exe);
        var psi=new ProcessStartInfo(exe){
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        foreach(string arg in args)psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)??throw new IOException("test_verifier_spawn_failed");
        var err=process.StandardError.ReadToEndAsync();
        var text=process.StandardOutput.ReadToEndAsync();
        if(!process.WaitForExit(30000)){
            process.Kill(true);
            throw new TimeoutException("test_verifier_timeout");
        }
        string errors=err.GetAwaiter().GetResult();
        string output=text.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException("test_media_verifier_exit_"+process.ExitCode+": "+
                errors[..Math.Min(errors.Length,1200)]);
        return output;
    }
    private static int Prefix(ReadOnlySpan<byte> s,int i)
    {
        if(i+3>s.Length||s[i]!=0||s[i+1]!=0)return 0;
        if(s[i+2]==1)return 3;
        return i+4<=s.Length&&s[i+2]==0&&s[i+3]==1?4:0;
    }
    private static (int Count,byte[] Hash) VclDigest(byte[] bytes)
    {
        using var ms=new MemoryStream();
        int nalCount=0;
        Span<byte> len=stackalloc byte[4];
        for(int i=0;i<bytes.Length;)
        {
            int prefix=Prefix(bytes,i);
            if(prefix==0)throw new InvalidDataException("test_vcl_annexb_expected");
            int body=i+prefix;
            if(body>=bytes.Length)throw new InvalidDataException("test_vcl_empty_nal");
            int end=body+1;
            while(end<bytes.Length&&Prefix(bytes,end)==0)end++;
            int stop=end;
            while(stop>body+1&&bytes[stop-1]==0)stop--;
            if(stop<=body)throw new InvalidDataException("test_vcl_invalid_nal");
            int type=bytes[body]&31;
            if(type is 1 or 5)
            {
                int length=stop-body;
                BinaryPrimitives.WriteInt32BigEndian(len,length);
                ms.Write(len);
                ms.Write(bytes,body,length);
                nalCount++;
            }
            i=end;
        }
        return (nalCount,SHA256.HashData(ms.ToArray()));
    }
    public static void Verify(string rawH264,string nativeMp4,string ffmpeg,string ffprobe,
        Action<bool,string> check,bool withAudio=false)
    {
        string json=Run(ffprobe,"-v","error","-count_frames",
            "-show_entries","stream=codec_name,codec_type,width,height,sample_rate,channels,nb_read_frames,start_time,duration:format=duration",
            "-of","json",nativeMp4);
        using var probe=JsonDocument.Parse(json);
        var streams=probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        check(streams.Length==(withAudio?2:1),
            withAudio?"native A/V MP4 contains exactly H264 + AAC tracks":
            "native video-only MP4 has exactly one encoded stream");
        var video=streams.Single(x=>x.GetProperty("codec_type").GetString()=="video");
        if(withAudio)
        {
            var audio=streams.Single(x=>x.GetProperty("codec_type").GetString()=="audio");
            check(audio.GetProperty("codec_name").GetString()=="aac" &&
                  audio.GetProperty("sample_rate").GetString()=="48000" &&
                  audio.GetProperty("channels").GetInt32()==2,
                "native A/V audio is 48kHz stereo AAC");
            var aDuration=double.Parse(audio.GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            var vDuration=double.Parse(video.GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            check(Math.Abs(aDuration-vDuration)<0.04,
                "native A/V encoded track duration end skew below 40ms");
        }
        check(video.GetProperty("codec_name").GetString()=="h264" &&
              video.GetProperty("codec_type").GetString()=="video" &&
              video.GetProperty("width").GetInt32()==640 &&
              video.GetProperty("height").GetInt32()==480 &&
              video.GetProperty("nb_read_frames").GetString()=="12",
            "independent MP4 probe reports 12 H.264 NVENC frames at 640x480");
        double duration=double.Parse(probe.RootElement.GetProperty("format")
            .GetProperty("duration").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        check(Math.Abs(duration-0.4)<0.02,
            "native MP4 experimental timeline is 12 frames near 400ms");
        if(withAudio)
            Run(ffmpeg,"-nostdin","-v","error","-xerror","-err_detect","explode",
                "-i",nativeMp4,"-map","0:v:0","-map","0:a:0","-f","null","NUL");
        else
            Run(ffmpeg,"-nostdin","-v","error","-xerror","-err_detect","explode",
                "-i",nativeMp4,"-map","0:v:0","-f","null","NUL");
        check(true,withAudio?
            "independent full decode of BOTH Native NVENC H264 and AAC succeeds":
            "independent full decode of Native NVENC MP4 succeeds");
        string extracted=Path.Combine(Path.GetDirectoryName(nativeMp4)!,
            Path.GetFileNameWithoutExtension(nativeMp4)+".extracted-test-only.h264");
        if(File.Exists(extracted))throw new IOException("test_extraction_output_exists");
        Run(ffmpeg,"-nostdin","-v","error","-i",nativeMp4,
            "-map","0:v:0","-c:v","copy",
            "-bsf:v","h264_mp4toannexb","-f","h264",extracted);
        var original=VclDigest(File.ReadAllBytes(rawH264));
        var remuxed=VclDigest(File.ReadAllBytes(extracted));
        check(original.Count==12 && remuxed.Count==original.Count,
            "independent MP4 remux preserves 12 H.264 VCL NAL units");
        check(CryptographicOperations.FixedTimeEquals(original.Hash,remuxed.Hash),
            "NVENC VCL NAL SHA-256 identical before/after Media Foundation MP4; no video re-encode");
        Console.WriteLine("AORUS_NATIVE_NVENC_MP4_VCL_SHA256="+
            Convert.ToHexString(original.Hash));
    }
}
