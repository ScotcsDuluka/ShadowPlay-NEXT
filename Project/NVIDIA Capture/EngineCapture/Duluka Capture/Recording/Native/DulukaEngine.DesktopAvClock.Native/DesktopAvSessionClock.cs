namespace DulukaEngine.DesktopAvClock.Native;

/// <summary>
/// Single-writer, deterministic clock/gap plan for one Desktop or Fullscreen
/// RecordingSession. No capture devices, COM, FFmpeg, processes or files.
/// The caller MUST materialize held video frames and any indicated PCM silence
/// before submitting media to a CFR native mux. This contract does not itself
/// create frames, record audio or certify live A/V synchronization.
/// </summary>
public sealed class DesktopAvSessionClock
{
    public const long TicksPerSecond=10_000_000;
    public readonly record struct VideoSlot(long Pts100ns,long SourceGeneration,
        long SourceFrameSequence,bool IsHeld);
    public readonly record struct AudioPacketPlan(long Pts100ns,long EndPts100ns,
        long GapFrames,double QpcDeviationMs,bool TimestampUnreliable);
    public readonly record struct EndAcceptance(bool Accepted,double EndSkewMs,string Reason);

    private readonly long _epoch100ns,_qpcFrequency;
    private readonly int _fps,_audioRate;
    private readonly int _maxVideoHoldSlots,_maxAudioGapFrames;
    private long _videoGeneration=1,_sourceSwitchRawQpc,_lastRawPresent=0;
    private long _lastVideoSlot=-1,_lastVideoSourceSeq=-1;
    private long _lastValidFrameGeneration=0,_lastValidFrameSeq=-1;
    private bool _audioAnchored;
    private long _audioAnchorDeviceFrame,_audioAnchorPts,_audioEndDevice=-1;
    private long _pendingSilenceFrames;
    public int FramesPerSecond => _fps;
    public long CurrentVideoGeneration => _videoGeneration;
    public long VideoFrameSlots => _lastVideoSlot+1;
    public long PendingSilenceFrames => _pendingSilenceFrames;

    public DesktopAvSessionClock(long rawEpochQpc,long qpcFrequency,
        int frameRate=30,int audioRate=48000,int maxHoldMs=250,int maxAudioGapMs=100)
    {
        if(rawEpochQpc<=0||qpcFrequency<=0)
            throw new ArgumentOutOfRangeException(nameof(rawEpochQpc),"av_qpc_origin_invalid");
        if(frameRate is not (30 or 60 or 120 or 240))
            throw new ArgumentOutOfRangeException(nameof(frameRate),"av_fps_unverified");
        if(audioRate!=48000)
            throw new ArgumentOutOfRangeException(nameof(audioRate),"av_pcm_rate_unverified");
        if(maxHoldMs is < 0 or > 500 || maxAudioGapMs is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maxHoldMs),"av_gap_policy_unbounded");
        _epoch100ns=QpcTo100ns(rawEpochQpc,qpcFrequency);
        _qpcFrequency=qpcFrequency;
        _sourceSwitchRawQpc=rawEpochQpc;
        _fps=frameRate;_audioRate=audioRate;
        _maxVideoHoldSlots=(int)((long)frameRate*maxHoldMs/1000);
        _maxAudioGapFrames=audioRate*maxAudioGapMs/1000;
    }

    public static long QpcTo100ns(long rawQpc,long frequency)
    {
        if(rawQpc<0||frequency<=0)
            throw new ArgumentOutOfRangeException(nameof(rawQpc),"av_qpc_invalid");
        return checked((long)decimal.Round((decimal)rawQpc*TicksPerSecond/frequency,
            0,MidpointRounding.AwayFromZero));
    }
    public static long SamplesTo100ns(long frames,int rate)
    {
        if(frames<0||rate<=0)
            throw new ArgumentOutOfRangeException(nameof(frames),"av_samples_invalid");
        return checked((long)decimal.Round((decimal)frames*TicksPerSecond/rate,
            0,MidpointRounding.AwayFromZero));
    }
    private long VideoPts(long index)=>SamplesTo100ns(index,_fps);
    private long RelativeQpc(long rawQpc)=>checked(QpcTo100ns(rawQpc,_qpcFrequency)-_epoch100ns);

    /// <summary>
    /// Only source identity switches: the session's clock NEVER resets. The
    /// first real frame of the new generation must be later than switch QPC.
    /// Stale old-source callbacks are rejected even if they arrive late.
    /// </summary>
    public void SwitchVideoSource(long oldGeneration,long newGeneration,long switchRawQpc)
    {
        long at=RelativeQpc(switchRawQpc);
        if(oldGeneration!=_videoGeneration || newGeneration<=oldGeneration ||
            at<0 || switchRawQpc<_sourceSwitchRawQpc ||
            (_lastRawPresent>0 && switchRawQpc<_lastRawPresent))
            throw new InvalidDataException("av_video_source_transition_invalid");
        _videoGeneration=newGeneration;
        _sourceSwitchRawQpc=switchRawQpc;
        _lastVideoSourceSeq=-1;
    }

    /// <summary>
    /// A new REAL capture frame must supply the active generation and raw
    /// DXGI QPC timestamp. Any skipped CFR slots are explicitly marked HOLD.
    /// Do not interpret HOLD as a new captured image. Missing video exceeding
    /// the hold budget fails, and must end/fault the recording session.
    /// </summary>
    public IReadOnlyList<VideoSlot> OfferRealVideo(long sourceGeneration,
        long sourceFrameSequence,long rawCaptureQpc)
    {
        if(sourceGeneration!=_videoGeneration || sourceFrameSequence<0 ||
           sourceFrameSequence<=_lastVideoSourceSeq)
            throw new InvalidDataException("av_video_stale_generation_or_sequence");
        if(rawCaptureQpc<_sourceSwitchRawQpc || rawCaptureQpc<=_lastRawPresent)
            throw new InvalidDataException("av_video_stale_capture_qpc");
        long rawTime=RelativeQpc(rawCaptureQpc);
        if(rawTime<0)throw new InvalidDataException("av_video_before_session_epoch");
        long nextSlot=checked((long)decimal.Round((decimal)rawTime*_fps/TicksPerSecond,
            0,MidpointRounding.AwayFromZero));
        if(nextSlot<=_lastVideoSlot)
            throw new InvalidDataException("av_video_duplicate_or_backward_cfr_slot");
        if(_lastVideoSlot<0 && nextSlot!=0)
            throw new InvalidDataException("av_first_video_frame_requires_session_anchor");
        long missing=nextSlot-_lastVideoSlot-1;
        if(missing>_maxVideoHoldSlots)
            throw new InvalidDataException("av_video_gap_exceeds_hold_budget");
        if(missing>0 && _lastValidFrameGeneration==0)
            throw new InvalidDataException("av_hold_without_valid_frame");
        var slots=new VideoSlot[checked((int)missing+1)];
        for(int i=0;i<missing;i++)
            slots[i]=new(VideoPts(_lastVideoSlot+1+i),
                _lastValidFrameGeneration,_lastValidFrameSeq,true);
        slots[^1]=new(VideoPts(nextSlot),sourceGeneration,sourceFrameSequence,false);
        _lastVideoSlot=nextSlot;
        _lastRawPresent=rawCaptureQpc;
        _lastVideoSourceSeq=sourceFrameSequence;
        _lastValidFrameGeneration=sourceGeneration;
        _lastValidFrameSeq=sourceFrameSequence;
        return slots;
    }

    /// <summary>
    /// WASAPI packet QPC is ALREADY 100ns. DevicePosition in PCM sample
    /// frames determines content progression and avoids arrival time jitter.
    /// Audio device switches/discontinuities require a separate explicit
    /// re-anchor policy, never silently resetting this session.
    /// </summary>
    public AudioPacketPlan OfferAudio(long devicePositionFrames,long firstSampleQpc100ns,
        int pcmFrames,bool timestampError=false)
    {
        if(devicePositionFrames<0||firstSampleQpc100ns<=0 ||
           pcmFrames is < 1 or > 48000)
            throw new InvalidDataException("av_invalid_wasapi_packet");
        if(_pendingSilenceFrames!=0)
            throw new InvalidDataException("av_unresolved_audio_silence_gap");
        if(!_audioAnchored)
        {
            if(timestampError)throw new InvalidDataException("av_audio_first_stamp_invalid");
            _audioAnchorPts=firstSampleQpc100ns-_epoch100ns;
            if(_audioAnchorPts<0 || _audioAnchorPts>TicksPerSecond)
                throw new InvalidDataException("av_audio_initial_timestamp_outside_epoch");
            _audioAnchorDeviceFrame=devicePositionFrames;
            _audioEndDevice=devicePositionFrames;
            _audioAnchored=true;
        }
        if(devicePositionFrames<_audioEndDevice)
            throw new InvalidDataException("av_audio_overlapping_or_reordered_packet");
        long gap=devicePositionFrames-_audioEndDevice;
        if(gap>_maxAudioGapFrames)
            throw new InvalidDataException("av_audio_gap_exceeds_recovery_budget");
        long start=checked(_audioAnchorPts+
            SamplesTo100ns(devicePositionFrames-_audioAnchorDeviceFrame,_audioRate));
        long end=checked(_audioAnchorPts+
            SamplesTo100ns(devicePositionFrames-_audioAnchorDeviceFrame+pcmFrames,_audioRate));
        long expectedStamp=checked(_epoch100ns+start);
        double discrepancy=(firstSampleQpc100ns-expectedStamp)/10000.0;
        _audioEndDevice=checked(devicePositionFrames+pcmFrames);
        _pendingSilenceFrames=gap;
        return new(start,end,gap,discrepancy,
            timestampError || Math.Abs(discrepancy)>40);
    }

    /// <summary>
    /// The caller asserts that exactly the missing PCM samples were written
    /// as silence to the native AAC route before any later media is accepted.
    /// This method does not create or write PCM buffers.
    /// </summary>
    public void ConfirmSilenceWritten(long frames)
    {
        if(_pendingSilenceFrames<=0 || frames!=_pendingSilenceFrames)
            throw new InvalidDataException("av_silence_completion_mismatch");
        _pendingSilenceFrames=0;
    }

    public EndAcceptance VerifyEnd(long expectedVideoSlots,long expectedAudioFrames)
    {
        if(!_audioAnchored || _lastVideoSlot<0 || _pendingSilenceFrames!=0)
            return new(false,double.NaN,"av_missing_track_or_unresolved_audio_gap");
        long emitted=_lastVideoSlot+1;
        long pcmFrames=_audioEndDevice-_audioAnchorDeviceFrame;
        if(expectedVideoSlots!=emitted || expectedAudioFrames!=pcmFrames)
            return new(false,double.NaN,"av_missing_video_or_audio_frames");
        long videoEnd=VideoPts(emitted);
        long audioEnd=checked(_audioAnchorPts+SamplesTo100ns(pcmFrames,_audioRate));
        double skew=(audioEnd-videoEnd)/10000.0;
        if(Math.Abs(skew)>40)
            return new(false,skew,"av_track_end_skew_exceeds_40ms");
        return new(true,skew,"");
    }
}
