using System.Runtime.InteropServices;
using DulukaEngine.Encoder;

/// <summary>
/// ISOLATED TEST-ONLY Windows Media Foundation MP4 video remux experiment.
/// Accepts already encoded H264 Annex-B NVENC packets, never re-encodes.
/// NO FFmpeg runtime, WASAPI, screen pixels, Capture worker or deployment.
/// The produced MP4 is UNVERIFIED test evidence until externally fully decoded.
/// Not an implementation of Production Save / RecordingSession.
/// </summary>
internal sealed class NativeNvencVideoMp4Experiment : IDisposable
{
    private static readonly Guid Major=new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype=new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video=new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264=new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize=new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate=new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelRatio=new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid Interlace=new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Bitrate=new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid SequenceHeader=new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid CleanPoint=new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid Audio=new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Aac=new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm=new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioChannels=new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioRate=new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits=new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid BlockAlign=new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid AvgBytes=new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid AacPayload=new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    [DllImport("ole32.dll",ExactSpelling=true)] private static extern int CoInitializeEx(IntPtr ignored,uint mode);
    [DllImport("ole32.dll",ExactSpelling=true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFStartup(uint version,uint flags);
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll",ExactSpelling=true)] private static extern int MFCreateMemoryBuffer(uint bytes,out IntPtr buffer);
    [DllImport("mfreadwrite.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)]string path,IntPtr byteStream,IntPtr attrs,out IntPtr sink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(IntPtr obj,ref Guid key,ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetU32(IntPtr obj,ref Guid key,uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetU64(IntPtr obj,ref Guid key,ulong val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBlob(IntPtr obj,ref Guid key,byte[] val,uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStream(IntPtr obj,IntPtr media,out uint streamIndex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInput(IntPtr obj,uint index,IntPtr media,IntPtr attrs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Begin(IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SinkWrite(IntPtr obj,uint index,IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FinalizeStream(IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Lock(IntPtr obj,out IntPtr address,out uint max,out uint current);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Unlock(IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetLength(IntPtr obj,uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddBuffer(IntPtr obj,IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetTime(IntPtr obj,long ticks);
    private static T V<T>(IntPtr obj,int slot) where T:Delegate
        =>Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size));
    private static void H(int result,string stage)
    {
        if(result<0)throw new InvalidOperationException(stage+":0x"+((uint)result).ToString("X8"));
    }
    private static void G(IntPtr o,Guid key,Guid val)=>H(V<SetGuid>(o,24)(o,ref key,ref val),"mp4_set_guid");
    private static void U(IntPtr o,Guid key,uint val)=>H(V<SetU32>(o,21)(o,ref key,val),"mp4_set_u32");
    private static void L(IntPtr o,Guid key,ulong val)=>H(V<SetU64>(o,22)(o,ref key,val),"mp4_set_u64");
    private static void B(IntPtr o,Guid key,byte[] val)=>H(V<SetBlob>(o,26)(o,ref key,val,(uint)val.Length),"mp4_set_blob");

    private IntPtr _writer;
    private readonly string _path;
    private uint _video,_audio;
    private readonly bool _enableAudio;
    private bool _com,_mf,_finalized,_retain,_disposed;
    private int _frames,_audioPackets;
    public int Frames=>_frames;
    public int AudioPackets=>_audioPackets;
    public NativeNvencVideoMp4Experiment(string path,byte[] annexBHeaders,
        bool nativeAacAudio=false)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(annexBHeaders is null || annexBHeaders.Length is < 12 or > 4096)
            throw new InvalidDataException("mp4_sps_pps_missing");
        _enableAudio=nativeAacAudio;
        _path=Path.GetFullPath(path);
        if(File.Exists(_path))throw new IOException("mp4_test_output_exists");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        IntPtr output=IntPtr.Zero,input=IntPtr.Zero,ao=IntPtr.Zero,ai=IntPtr.Zero;
        try
        {
            H(CoInitializeEx(IntPtr.Zero,0),"mp4_com");_com=true;
            H(MFStartup(0x00020070,0),"mp4_startup");_mf=true;
            H(MFCreateSinkWriterFromURL(_path,IntPtr.Zero,IntPtr.Zero,out _writer),"mp4_open");
            H(MFCreateMediaType(out output),"mp4_video_out_type");
            Type(output,annexBHeaders);
            H(V<AddStream>(_writer,3)(_writer,output,out _video),"mp4_add_h264");
            H(MFCreateMediaType(out input),"mp4_video_in_type");
            Type(input,annexBHeaders);
            H(V<SetInput>(_writer,4)(_writer,_video,input,IntPtr.Zero),"mp4_set_encoded_input");
            if(_enableAudio)
            {
                H(MFCreateMediaType(out ao),"mp4_aac_output_type");
                AudioType(ao,true);
                H(V<AddStream>(_writer,3)(_writer,ao,out _audio),"mp4_add_aac_stream");
                H(MFCreateMediaType(out ai),"mp4_pcm_input_type");
                AudioType(ai,false);
                H(V<SetInput>(_writer,4)(_writer,_audio,ai,IntPtr.Zero),"mp4_set_pcm_input");
            }
            H(V<Begin>(_writer,5)(_writer),"mp4_begin");
        }
        catch{Dispose();throw;}
        finally{
            if(output!=IntPtr.Zero)Marshal.Release(output);
            if(input!=IntPtr.Zero)Marshal.Release(input);
            if(ao!=IntPtr.Zero)Marshal.Release(ao);
            if(ai!=IntPtr.Zero)Marshal.Release(ai);
        }
    }
    private static void Type(IntPtr media,byte[] headers)
    {
        G(media,Major,Video);G(media,Subtype,H264);
        L(media,FrameSize,((ulong)640<<32)|480);
        L(media,FrameRate,((ulong)30<<32)|1);
        L(media,PixelRatio,((ulong)1<<32)|1);
        U(media,Interlace,2);U(media,Bitrate,2_000_000);
        B(media,SequenceHeader,headers);
    }
    private static void AudioType(IntPtr media,bool encoded)
    {
        G(media,Major,Audio);G(media,Subtype,encoded?Aac:Pcm);
        U(media,AudioChannels,2);U(media,AudioRate,48000);U(media,Bits,16);
        if(encoded)
        {
            U(media,AvgBytes,20000);
            U(media,AacPayload,0);
        }
        else
        {
            U(media,BlockAlign,4);
            U(media,AvgBytes,192000);
        }
    }
    private static long AudioTime(int packet) =>
        (long)((decimal)packet*1024m*10_000_000m/48000m);
    /// <summary>
    /// PCM16 stereo test packet, precisely 1024 samples/channel, on a 48kHz
    /// cumulative-sample timeline. The MF sink encodes this into native AAC.
    /// No WASAPI device and no external encoding process is involved.
    /// </summary>
    public void WritePcm16(short[] stereo,int packetIndex)
    {
        if(!_enableAudio || _disposed || _finalized)
            throw new InvalidOperationException("mp4_native_aac_not_available");
        if(stereo is null || stereo.Length!=2048 ||
           packetIndex!=_audioPackets || packetIndex>=19)
            throw new InvalidDataException("mp4_pcm_packet_order_or_size");
        var bytes=new byte[4096];
        Buffer.BlockCopy(stereo,0,bytes,0,bytes.Length);
        Submit(_audio,bytes,AudioTime(packetIndex),
            AudioTime(packetIndex+1)-AudioTime(packetIndex),false);
        _audioPackets++;
    }
    public void Write(PacketMetadata metadata,byte[] accessUnit)
    {
        if(_disposed||_finalized)throw new InvalidOperationException("mp4_writer_terminal");
        if(metadata.Sequence!=_frames || metadata.CodecKey!="NVENC_H264" ||
           metadata.PresentationTimestampTicks!=_frames*333333L ||
           metadata.DecodingTimestampTicks!=metadata.PresentationTimestampTicks ||
           accessUnit is null || accessUnit.Length is < 6 or > 4_194_304)
            throw new InvalidDataException("mp4_video_packet_contract");
        Submit(_video,accessUnit,metadata.PresentationTimestampTicks,333333,
            metadata.IsKeyFrame);
        _frames++;
    }
    private void Submit(uint stream,byte[] accessUnit,long pts,long duration,bool keyframe)
    {
        IntPtr sample=IntPtr.Zero,buffer=IntPtr.Zero;bool locked=false;
        try
        {
            H(MFCreateSample(out sample),"mp4_create_sample");
            H(MFCreateMemoryBuffer((uint)accessUnit.Length,out buffer),"mp4_create_buffer");
            H(V<Lock>(buffer,3)(buffer,out IntPtr dst,out _,out _),"mp4_lock");
            locked=true;
            Marshal.Copy(accessUnit,0,dst,accessUnit.Length);
            H(V<Unlock>(buffer,4)(buffer),"mp4_unlock");locked=false;
            H(V<SetLength>(buffer,6)(buffer,(uint)accessUnit.Length),"mp4_length");
            H(V<AddBuffer>(sample,42)(sample,buffer),"mp4_add_buffer");
            H(V<SetTime>(sample,36)(sample,pts),"mp4_pts");
            H(V<SetTime>(sample,38)(sample,duration),"mp4_duration");
            if(keyframe)U(sample,CleanPoint,1);
            H(V<SinkWrite>(_writer,6)(_writer,stream,sample),"mp4_write");
        }
        finally
        {
            if(locked&&buffer!=IntPtr.Zero){try{V<Unlock>(buffer,4)(buffer);}catch{}}
            if(sample!=IntPtr.Zero)Marshal.Release(sample);
            if(buffer!=IntPtr.Zero)Marshal.Release(buffer);
        }
    }
    public string FinalizeForTestOnly()
    {
        if(_disposed||_finalized||_frames!=12||
           (_enableAudio&&_audioPackets!=19))
            throw new InvalidOperationException("mp4_experimental_av_packet_budget_incomplete");
        H(V<FinalizeStream>(_writer,11)(_writer),"mp4_finalize");
        _finalized=true;
        Marshal.Release(_writer);_writer=IntPtr.Zero;
        if(!File.Exists(_path)||new FileInfo(_path).Length==0)
            throw new InvalidDataException("mp4_final_file_missing");
        _retain=true; // test evidence ONLY; not a Production Saved transition.
        return _path;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        if(_writer!=IntPtr.Zero){Marshal.Release(_writer);_writer=IntPtr.Zero;}
        if(_mf){MFShutdown();_mf=false;}
        if(_com){CoUninitialize();_com=false;}
        if(!_retain&&File.Exists(_path))File.Delete(_path);
    }
}
