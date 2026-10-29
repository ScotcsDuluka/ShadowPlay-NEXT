using DulukaEngine.DesktopAvClock.Native;

internal static class AudioDeviceDriftTests
{
    public static void Run()
    {
        int pass=0;
        void Check(bool ok,string name)
        {
            if(!ok)throw new Exception("DRIFT_TEST_FAIL "+name);
            pass++;Console.WriteLine("PASS DRIFT "+name);
        }
        const long start=12_345_000_000;
        AudioDeviceDriftGate.Observation Push(AudioDeviceDriftGate gate,int second,
            long deviation100ns=0,bool invalid=false)
            =>gate.Observe(second*48000L,
                checked(start+second*10_000_000L+deviation100ns),invalid);

        var steady=new AudioDeviceDriftGate();
        for(int i=0;i<1800;i++)
        {
            long jitter=(i%31==0&&i>0)?150_000:0; // isolated +15ms
            var res=Push(steady,i,jitter);
            if(!res.Accepted)throw new Exception("30min_jitter_false_positive_at="+i);
        }
        var ok=steady.EvaluateFinal();
        Check(ok.Accepted&&ok.Classification=="target"&&ok.RollingMedianMs<1,
            "30-minute 48k device cursor with isolated 15ms QPC spikes stays in target");
        Check(ok.ValidObservations==1800,
            "1800 separate reliable one-second checkpoints evaluated");

        var slowDrift=new AudioDeviceDriftGate();
        int firstReject=-1;
        for(int i=0;i<1800;i++)
        {
            // 100ppm = 0.1ms/sec; beyond 40ms around minute 7.
            var r=Push(slowDrift,i,i*1000L);
            if(!r.Accepted){firstReject=i;break;}
        }
        Check(firstReject is >=400 and <=550,
            "100ppm physical audio clock error detected before 10 minutes");
        Check(slowDrift.Failed&&!slowDrift.EvaluateFinal().Accepted,
            "persistent >40ms drift terminal; final media cannot be accepted");
        Check(!Push(slowDrift,firstReject+1).Accepted,
            "faulted drift monitor never silently recovers within same session");

        var tolerable=new AudioDeviceDriftGate();
        for(int i=0;i<1800;i++)
        {
            // 10ppm = 18ms after 30min; should remain <=40ms.
            if(!Push(tolerable,i,i*100L).Accepted)
                throw new Exception("low_ppm_false_rejection");
        }
        Check(tolerable.EvaluateFinal().Accepted,
            "10ppm device drift remains inside 40ms end acceptance");
        var isolated=new AudioDeviceDriftGate();
        for(int i=0;i<28;i++)
        {
            var r=Push(isolated,i,i==16?1_500_000:0);
            if(!r.Accepted)throw new Exception("isolated_150ms_stamp_should_not_fault");
        }
        Check(isolated.EvaluateFinal().Accepted,
            "isolated 150ms QPC outlier does not distort robust rolling median");

        var persistent=new AudioDeviceDriftGate();
        AudioDeviceDriftGate.Observation bad=default;
        for(int i=0;i<22;i++)
        {
            bad=Push(persistent,i,i<9?0:650_000);
            if(!bad.Accepted)break;
        }
        Check(!bad.Accepted&&bad.Reason=="av_drift_persistent_over_40ms",
            "sustained 65ms audio timestamp shift fails closed");
        var invalid=new AudioDeviceDriftGate();
        Check(!Push(invalid,0,0,true).Accepted &&
              invalid.Failed,
            "invalid first device timestamp cannot establish timeline origin");
        var broken=new AudioDeviceDriftGate();
        Push(broken,0);
        for(int i=1;i<=4;i++)
            Check(Push(broken,i,0,true).Accepted,"invalid stamp "+i+" flagged without minting valid evidence");
        Check(!Push(broken,5,0,true).Accepted&&broken.Failed,
            "five consecutive bad device timestamps reject missing evidence");
        var position=new AudioDeviceDriftGate();
        Push(position,0);
        Check(!position.Observe(0,start+10_000_000).Accepted,
            "audio sample device cursor cannot repeat or move backwards");
        var missing=new AudioDeviceDriftGate();
        Push(missing,0);
        Check(!missing.Observe(11*48000L,start+110_000_000L).Accepted,
            "unobserved gap greater than 10s needs explicit new clock contract");
        var shortClip=new AudioDeviceDriftGate();
        for(int i=0;i<8;i++)Push(shortClip,i);
        Check(!shortClip.EvaluateFinal().Accepted,
            "short session without nine reliable checkpoints cannot be certified by long-run gate");
        Console.WriteLine($"AORUS_AUDIO_DEVICE_DRIFT_GATE_PASS checks={pass} " +
            "sample_rate=48000 simulated_duration_sec=1800 device_capture=false production=false");
    }
}
