using DulukaEngine.DesktopAvClock.Native;
using DulukaEngine.Encoder;

/// <summary>
/// Isolated synthetic integration check: the SAME 12 real NVENC packet
/// metadata and 19 synthetic PCM blocks used by the native two-track
/// SinkWriter must fit one 100ns Session clock. No capture devices, FFmpeg
/// or real WASAPI endpoints are opened here. Not a live sync certification.
/// </summary>
internal static class NativeNvencAvClockAcceptance
{
    public static void Verify(IReadOnlyList<(PacketMetadata Meta,byte[] Payload)> video,
        int pcmPackets,Action<bool,string> check)
    {
        const long epoch=100_000_000;
        const long frequency=10_000_000; // synthetic QPC 10MHz test domain
        var clock=new DesktopAvSessionClock(epoch,frequency);
        check(video.Count==12 && pcmPackets==19,
            "native AV clock test uses the exact 12-NVENC / 19-PCM mux input budgets");
        for(int i=0;i<video.Count;i++)
        {
            var p=video[i];
            var slots=clock.OfferRealVideo(1,i,checked(epoch+p.Meta.PresentationTimestampTicks));
            if(slots.Count!=1 || slots[0].IsHeld ||
               Math.Abs(slots[0].Pts100ns-p.Meta.PresentationTimestampTicks)>10)
                throw new InvalidDataException("nvenc_av_clock_video_pts_not_one_shared_epoch");
        }
        check(clock.VideoFrameSlots==12,
            "all actual NVENC output timestamps map into 12 unique CFR video slots");

        var drift=new AudioDeviceDriftGate();
        for(int i=0;i<pcmPackets;i++)
        {
            long first=(long)i*1024;
            long stamp=checked(epoch+DesktopAvSessionClock.SamplesTo100ns(first,48000));
            var a=clock.OfferAudio(first,stamp,1024);
            if(a.GapFrames!=0 || a.TimestampUnreliable ||
               a.Pts100ns!=DesktopAvSessionClock.SamplesTo100ns(first,48000))
                throw new InvalidDataException("nvenc_av_clock_pcm_packet_outside_session");
            var diagnostic=drift.Observe(first,stamp);
            if(!diagnostic.Accepted)
                throw new InvalidDataException("nvenc_av_clock_drift_not_accepted");
        }
        check(drift.EvaluateFinal().Accepted,
            "19 synthetic PCM packets preserve one sample clock without drift");
        var final=clock.VerifyEnd(12,19L*1024);
        check(final.Accepted && Math.Abs(final.EndSkewMs)<10,
            "NVENC hardware PTS / Native AAC samples align under 10ms end-skew metadata gate");
        check(!clock.VerifyEnd(12,18L*1024).Accepted,
            "missing 1024 PCM samples reject two-track completion");
        check(!clock.VerifyEnd(11,19L*1024).Accepted,
            "missing one encoded NVENC frame rejects two-track completion");

        var shifted=new DesktopAvSessionClock(epoch,frequency);
        for(int i=0;i<12;i++)
            shifted.OfferRealVideo(1,i,epoch+video[i].Meta.PresentationTimestampTicks);
        for(int i=0;i<19;i++)
            shifted.OfferAudio((long)i*1024,epoch+500_000+
                DesktopAvSessionClock.SamplesTo100ns((long)i*1024,48000),1024);
        check(!shifted.VerifyEnd(12,19L*1024).Accepted,
            "50ms simulated audio capture offset cannot be published as synced");

        var stale=new DesktopAvSessionClock(epoch,frequency);
        stale.OfferRealVideo(1,0,epoch);
        stale.SwitchVideoSource(1,2,epoch+10_000);
        bool rejected=false;
        try {stale.OfferRealVideo(1,1,epoch+333_333);}
        catch(InvalidDataException){rejected=true;}
        check(rejected,"stale Desktop frames rejected after synthetic Fullscreen source generation change");

        Console.WriteLine("AORUS_NVENC_NATIVE_AV_CLOCK_GATE_PASS video_frames=12 pcm_frames=19456 "+
            "sample_rate=48000 device_capture=false live_av_sync_verified=false "+
            "track_end_skew_ms="+final.EndSkewMs.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture));
    }
}
