using DulukaEngine.Encoder;

/// <summary>
/// Experimental bounded H.264 Annex B access-unit validation for the isolated
/// AORUS GTX 1080 Ti smoke test. No FFmpeg, encoder operations or I/O.
/// Strict 30fps source PTS contract matches the test's FrameDiagnostics;
/// do not wire this directly into production for variable/59.94fps clocks.
/// </summary>
internal sealed class NvencH264PacketGate
{
    private byte[]? _sps,_pps;
    private long _nextPacket;
    public long AcceptedPackets => _nextPacket;
    public int DetectedIdr {get;private set;}
    public bool HasCodecHeaders => _sps is not null && _pps is not null;
    public byte[] GetAnnexBSequenceHeader()
    {
        if(_sps is null || _pps is null)
            throw new InvalidOperationException("nvenc_missing_sps_pps");
        var header=new byte[8+_sps.Length+_pps.Length];
        header[3]=1;_sps.CopyTo(header,4);
        header[7+_sps.Length]=1;_pps.CopyTo(header,8+_sps.Length);
        return header;
    }
    private static int Start(ReadOnlySpan<byte> data,int i)
    {
        if(i+3>data.Length || data[i]!=0 || data[i+1]!=0)return 0;
        if(data[i+2]==1)return 3;
        return i+4<=data.Length && data[i+2]==0 && data[i+3]==1?4:0;
    }
    public void Validate(PacketMetadata meta,ReadOnlySpan<byte> accessUnit)
    {
        if(accessUnit.Length<6 || accessUnit.Length>4_194_304)
            throw new InvalidDataException("nvenc_packet_size_unbounded");
        if(meta.CodecKey!="NVENC_H264" || meta.Sequence!=_nextPacket)
            throw new InvalidDataException("nvenc_packet_codec_sequence_mismatch");
        if(meta.PresentationTimestampTicks!=_nextPacket*333333L ||
           meta.DecodingTimestampTicks!=meta.PresentationTimestampTicks)
            throw new InvalidDataException("nvenc_packet_pts_dts_mismatch");

        byte[]? candidateSps=_sps,candidatePps=_pps;
        bool hasVcl=false,hasIdr=false;
        int count=0;
        for(int index=0;index<accessUnit.Length;)
        {
            int prefix=Start(accessUnit,index);
            if(prefix==0)throw new InvalidDataException("nvenc_annexb_missing_start_code");
            int payload=index+prefix;
            if(payload>=accessUnit.Length)
                throw new InvalidDataException("nvenc_annexb_empty_nal");
            int end=payload+1;
            while(end<accessUnit.Length&&Start(accessUnit,end)==0)end++;
            int final=end;
            while(final>payload+1&&accessUnit[final-1]==0)final--;
            int length=final-payload;
            if(length<=0||++count>96)
                throw new InvalidDataException("nvenc_nal_empty_or_excessive");
            int kind=accessUnit[payload]&31;
            if((accessUnit[payload]&0x80)!=0)
                throw new InvalidDataException("nvenc_forbidden_nal_bit");
            if(kind is 1 or 5)hasVcl=true;
            if(kind==5)hasIdr=true;
            if(kind==7)
            {
                var current=accessUnit.Slice(payload,length);
                if(candidateSps is not null&&!current.SequenceEqual(candidateSps))
                    throw new InvalidDataException("nvenc_codec_sps_reconfigured");
                candidateSps=current.ToArray();
            }
            if(kind==8)
            {
                var current=accessUnit.Slice(payload,length);
                if(candidatePps is not null&&!current.SequenceEqual(candidatePps))
                    throw new InvalidDataException("nvenc_codec_pps_reconfigured");
                candidatePps=current.ToArray();
            }
            index=end;
        }
        if(_nextPacket==0 && (candidateSps is null || candidatePps is null || !hasIdr))
            throw new InvalidDataException("nvenc_first_packet_requires_sps_pps_idr");
        if(!hasVcl||meta.IsKeyFrame!=hasIdr)
            throw new InvalidDataException("nvenc_keyframe_vcl_mismatch");
        _sps=candidateSps;_pps=candidatePps;
        if(hasIdr)DetectedIdr++;
        _nextPacket++;
    }
}
