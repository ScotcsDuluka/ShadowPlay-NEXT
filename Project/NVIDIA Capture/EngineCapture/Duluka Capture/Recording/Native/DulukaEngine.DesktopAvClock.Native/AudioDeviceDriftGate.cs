namespace DulukaEngine.DesktopAvClock.Native;

/// <summary>
/// Experimental single-writer clock-drift detector for actual WASAPI device
/// sample position vs first-sample QPC timestamp (already 100ns).
/// Does not use packet arrival time or mutate media PTS. It does NOT capture
/// audio, resample, or certify perceptual A/V sync. Caller must mark invalid
/// QPC stamps; a new audio device needs a fresh explicit epoch and discontinuity.
/// </summary>
public sealed class AudioDeviceDriftGate
{
    public readonly record struct Observation(bool Accepted,string Classification,
        double DeviationMs,double RollingMedianMs,int ValidObservations,
        int UnreliableConsecutive,string Reason);
    private readonly int _sampleRate;
    private readonly Queue<double> _deviations=new();
    private long _originFrame=-1,_originQpc100ns;
    private long _lastFrame=-1;
    private int _valid,_consecutiveInvalid,_consecutiveBad;
    private bool _failed;
    public bool Failed => _failed;
    public bool WarmedUp => _deviations.Count>=9;

    public AudioDeviceDriftGate(int sampleRate=48000)
    {
        if(sampleRate!=48000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate),"av_drift_rate_unsupported");
        _sampleRate=sampleRate;
    }
    private Observation Fault(string reason,double deviation=double.NaN,
        double median=double.NaN)
    {
        _failed=true;
        return new(false,"reject",deviation,median,_valid,_consecutiveInvalid,reason);
    }
    private Observation State(string label,double deviation,double median,string reason="")
        =>new(true,label,deviation,median,_valid,_consecutiveInvalid,reason);

    /// <param name="firstSampleQpc100ns">WASAPI timestamp for first PCM frame in packet.
    /// NOT time when callback arrived.</param>
    public Observation Observe(long firstDeviceFrame,long firstSampleQpc100ns,
        bool timestampError=false)
    {
        if(_failed)
            return new(false,"reject",double.NaN,double.NaN,
                _valid,_consecutiveInvalid,"av_drift_gate_terminal");
        if(firstDeviceFrame<0 || firstSampleQpc100ns<=0)
            return Fault("av_drift_invalid_stamp");
        if(_lastFrame>=0 && firstDeviceFrame<=_lastFrame)
            return Fault("av_drift_device_position_nonmonotonic");
        if(_lastFrame>=0 && firstDeviceFrame-_lastFrame>_sampleRate*10L)
            return Fault("av_drift_observation_gap_exceeds_10s");
        _lastFrame=firstDeviceFrame;
        if(timestampError)
        {
            _consecutiveInvalid++;
            if(_originFrame<0)
                return Fault("av_drift_initial_clock_unreliable");
            if(_consecutiveInvalid>=5)
                return Fault("av_drift_clock_evidence_lost");
            return State("unreliable",double.NaN,
                _deviations.Count==0?double.NaN:Median(),"av_drift_timestamp_error");
        }
        _consecutiveInvalid=0;
        if(_originFrame<0)
        {
            _originFrame=firstDeviceFrame;
            _originQpc100ns=firstSampleQpc100ns;
        }
        long expected=checked(_originQpc100ns+
            DesktopAvSessionClock.SamplesTo100ns(firstDeviceFrame-_originFrame,_sampleRate));
        double deviation=(firstSampleQpc100ns-expected)/10_000.0;
        if(double.IsNaN(deviation)||double.IsInfinity(deviation))
            return Fault("av_drift_nonfinite_timestamp");
        _deviations.Enqueue(deviation);
        if(_deviations.Count>9)_deviations.Dequeue();
        _valid++;
        double median=Median();
        if(!WarmedUp)return State("warming",deviation,median);
        // Ignore isolated timestamp spikes, but stop publishing any clip when
        // the audio hardware clock is persistently >40ms away from its epoch.
        if(Math.Abs(median)>40)_consecutiveBad++;
        else _consecutiveBad=0;
        if(_consecutiveBad>=3)
            return Fault("av_drift_persistent_over_40ms",deviation,median);
        if(Math.Abs(median)>20)
            return State("warning",deviation,median,"av_drift_median_above_20ms");
        return State("target",deviation,median);
    }
    private double Median()
    {
        var a=_deviations.ToArray();
        Array.Sort(a);
        return a[a.Length/2];
    }
    public Observation EvaluateFinal()
    {
        if(_failed)
            return new(false,"reject",double.NaN,double.NaN,
                _valid,_consecutiveInvalid,"av_drift_gate_terminal");
        if(!WarmedUp || _consecutiveInvalid>0)
            return new(false,"reject",double.NaN,
                _deviations.Count>0?Median():double.NaN,
                _valid,_consecutiveInvalid,"av_drift_not_enough_reliable_evidence");
        double median=Median();
        if(Math.Abs(median)>40)
            return new(false,"reject",double.NaN,median,_valid,0,
                "av_drift_final_median_over_40ms");
        return State(Math.Abs(median)>20?"warning":"target",double.NaN,median);
    }
}
