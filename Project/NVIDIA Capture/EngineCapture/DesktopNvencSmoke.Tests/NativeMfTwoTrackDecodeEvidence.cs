using System.Runtime.InteropServices;

/// <summary>
/// Native test acceptance: read and FULL-DECODE the same H264+AAC two-track
/// MP4 using Windows Media Foundation Source Reader only. No external
/// subprocess or FFmpeg path, no production media state, no capture device.
/// An intentional bounded synthetic clip (12 frames / 19 PCM input packets).
/// </summary>
internal static class NativeMfTwoTrackDecodeEvidence
{
    private static readonly Guid Major=new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype=new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video=new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Audio=new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12=new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm16=new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Channels=new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Rate=new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits=new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private const uint VideoStream=0xFFFFFFFC;
    private const uint AudioStream=0xFFFFFFFD;
    private const uint Eos=0x2;
    private const uint Error=0x1;
    [DllImport("ole32.dll",ExactSpelling=true)] private static extern int CoInitializeEx(IntPtr a,uint apartment);
    [DllImport("ole32.dll",ExactSpelling=true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFStartup(uint version,uint flags);
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)]string url,IntPtr attrs,out IntPtr reader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(
        IntPtr o,ref Guid key,ref Guid val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetU32(
        IntPtr o,ref Guid key,uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetCurrent(
        IntPtr reader,uint index,IntPtr reserved,IntPtr media);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Read(
        IntPtr reader,uint request,uint control,out uint actual,out uint flags,
        out long pts,out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLength(
        IntPtr sample,out uint size);
    private static T V<T>(IntPtr p,int slot) where T:Delegate
        =>Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));
    private static void H(int hr,string stage)
    {
        if(hr<0)throw new InvalidDataException(
            "native_mf_full_decode_"+stage+"_hr_0x"+((uint)hr).ToString("X8"));
    }
    private static void G(IntPtr o,Guid key,Guid val)
        =>H(V<SetGuid>(o,24)(o,ref key,ref val),"set_guid");
    private static void U(IntPtr o,Guid key,uint val)
        =>H(V<SetU32>(o,21)(o,ref key,val),"set_u32");
    private static void Configure(IntPtr reader,uint index,bool audio)
    {
        IntPtr media=IntPtr.Zero;
        try
        {
            H(MFCreateMediaType(out media),"create_decode_type");
            G(media,Major,audio?Audio:Video);
            G(media,Subtype,audio?Pcm16:Nv12);
            if(audio)
            {
                U(media,Channels,2);U(media,Rate,48000);U(media,Bits,16);
            }
            H(V<SetCurrent>(reader,7)(reader,index,IntPtr.Zero,media),
                audio?"set_aac_pcm_decoder":"set_h264_nv12_decoder");
        }
        finally{if(media!=IntPtr.Zero)Marshal.Release(media);}
    }
    private static (int Samples,long Bytes,long LastPts,bool Eos) Decode(
        IntPtr reader,uint index,bool audio)
    {
        int count=0;
        long bytes=0,last=long.MinValue;
        bool eos=false;
        for(int i=0;i<4096;i++)
        {
            IntPtr sample=IntPtr.Zero;
            try
            {
                H(V<Read>(reader,9)(reader,index,0,out uint actual,out uint flags,
                    out long pts,out sample),"read_sample");
                if((flags&Error)!=0)
                    throw new InvalidDataException("native_mf_reader_error_flag");
                if((flags&0x30)!=0)
                    throw new InvalidDataException("native_mf_format_changed_without_gate");
                if(sample!=IntPtr.Zero)
                {
                    if(pts<last)throw new InvalidDataException("native_mf_decoder_pts_backwards");
                    last=pts;
                    H(V<GetLength>(sample,45)(sample,out uint len),"decoded_sample_length");
                    if(len==0)throw new InvalidDataException("native_mf_decoded_empty_payload");
                    bytes=checked(bytes+len);
                    if(++count>1000||bytes>50_000_000)
                        throw new InvalidDataException("native_mf_decode_resource_limit");
                }
                if((flags&Eos)!=0){eos=true;break;}
                if(sample==IntPtr.Zero && flags==0)
                    throw new InvalidDataException("native_mf_decode_no_progress");
            }
            finally{if(sample!=IntPtr.Zero)Marshal.Release(sample);}
        }
        if(!eos || count==0)throw new InvalidDataException(
            "native_mf_missing_samples_or_eos_"+(audio?"audio":"video"));
        return(count,bytes,last,eos);
    }

    public static void Verify(string path,Action<bool,string> check)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(!File.Exists(path))throw new FileNotFoundException("native_mp4_missing",path);
        IntPtr reader=IntPtr.Zero;bool com=false,mf=false;
        try
        {
            H(CoInitializeEx(IntPtr.Zero,0),"com_init");com=true;
            H(MFStartup(0x00020070,0),"mf_start");mf=true;
            H(MFCreateSourceReaderFromURL(path,IntPtr.Zero,out reader),"open_two_track_mp4");
            Configure(reader,VideoStream,false);
            Configure(reader,AudioStream,true);
            var v=Decode(reader,VideoStream,false);
            var a=Decode(reader,AudioStream,true);
            check(v.Eos && v.Samples==12 && v.Bytes==12L*640*480*3/2,
                "native Media Foundation fully decodes all 12 NVENC H264 frames to exact NV12 bytes");
            check(a.Eos && a.Samples==19 && a.Bytes==19L*1024*4,
                "native Media Foundation fully decodes all 19 AAC blocks to exact stereo PCM16 bytes");
            check(a.LastPts>=0 && v.LastPts>=0,
                "native A/V decode confirms monotonic nonnegative last media timestamps");
            Console.WriteLine("AORUS_NATIVE_MF_TWO_TRACK_FULL_DECODE_PASS video_frames="+v.Samples+
                " video_bytes="+v.Bytes+" audio_decode_samples="+a.Samples+
                " audio_pcm_bytes="+a.Bytes+
                " runtime_ffmpeg=false live_capture=false");
        }
        finally
        {
            if(reader!=IntPtr.Zero)Marshal.Release(reader);
            if(mf)MFShutdown();
            if(com)CoUninitialize();
        }
    }
}
