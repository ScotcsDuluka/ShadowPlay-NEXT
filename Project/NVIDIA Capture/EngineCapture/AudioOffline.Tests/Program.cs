using System;
using System.IO;
using DulukaEngine.Audio;

internal static class Program {
    const int SampleRate = 48000;
    const long T0 = 100_000_000L;
    static int passed;
    static void Check(string title, bool predicate) {
        if(!predicate) throw new Exception("FAIL " + title);
        Console.WriteLine("PASS " + title); passed++;
    }
    static AudioPacket Packet(AudioTrackKind track, long t, int frames, int channels) {
        var pcm = new byte[frames*channels*2];
        for (int i=0; i<pcm.Length; i+=2) { pcm[i] = 0x34; pcm[i+1] = 0x12; }
        return new AudioPacket(track,t,frames,pcm,false,0,0,0,SampleRate,channels);
    }
    static byte[] GetWav(string file, int channels, double seconds) {
        Check(Path.GetFileName(file)+" finalized file exists",File.Exists(file));
        byte[] all = File.ReadAllBytes(file);
        Check(Path.GetFileName(file)+" RIFF header",all.Length>=44&&
            System.Text.Encoding.ASCII.GetString(all,0,4)=="RIFF"&&
            System.Text.Encoding.ASCII.GetString(all,8,4)=="WAVE");
        Check(Path.GetFileName(file)+" audio PCM format",BitConverter.ToUInt16(all,20)==1 &&
            BitConverter.ToUInt16(all,22)==channels &&
            BitConverter.ToUInt32(all,24)==SampleRate&&
            BitConverter.ToUInt16(all,34)==16);
        int expected=(int)Math.Round(SampleRate*channels*2*seconds);
        int bytes = all.Length-44;
        Check(Path.GetFileName(file)+" sample duration within 1 sample",Math.Abs(bytes-expected)<=channels*2);
        Check(Path.GetFileName(file)+" header sizes exact",BitConverter.ToUInt32(all,40)==bytes&&
             BitConverter.ToUInt32(all,4)==all.Length-8);
        return all;
    }
    static bool NonzeroAt(byte[] audio, int channels, double seconds) {
        int i=44+(int)(SampleRate*seconds)*channels*2;
        return i+1<audio.Length&&(audio[i]!=0||audio[i+1]!=0);
    }
    static bool SilenceAt(byte[] audio, int channels, double seconds) {
        int i=44+(int)(SampleRate*seconds)*channels*2;
        return i+1<audio.Length&&audio[i]==0&&audio[i+1]==0;
    }
    static void Main(string[] args) {
        if (args.Length != 0) throw new Exception("This offline test takes no device or output path arguments");
        // Outputs only to this test project's ignored build directory.
        var root=Path.Combine(AppContext.BaseDirectory,"AudioOfflineTestArtifacts");
        Directory.CreateDirectory(root);

        string p1=Path.Combine(root,"aligned-stereo.wav");
        using (var s=new AudioTimelineWavSink(p1)) {
            s.SetVideoStart(T0);
            s.Write(Packet(AudioTrackKind.System,T0+1_000_000L,9600,2));
            s.Complete(T0+5_000_000L);
            Check("stereo sink accounted frames without drop",
                Math.Abs(s.BytesWritten-96000)<=4&&s.BytesDropped==0);
            s.Complete(T0+5_000_000L);
        }
        var w1=GetWav(p1,2,0.5);
        Check("stereo initial silence",SilenceAt(w1,2,0.05));
        Check("stereo source samples",NonzeroAt(w1,2,0.15));
        Check("stereo trailing silence",SilenceAt(w1,2,0.4));

        string p2=Path.Combine(root,"pending-outoforder-mono.wav");
        using (var s=new AudioTimelineWavSink(p2)) {
            s.Write(Packet(AudioTrackKind.Microphone,T0+2_000_000L,2400,1));
            s.Write(Packet(AudioTrackKind.Microphone,T0+500_000L,2400,1));
            Check("pre-alignment sink hasn't flushed",s.BytesWritten==0);
            s.SetVideoStart(T0);
            s.Complete(T0+4_000_000L);
        }
        var w2=GetWav(p2,1,0.4);
        Check("out-of-order pending packets sorted (early)",NonzeroAt(w2,1,0.06));
        Check("out-of-order pending packets sorted (late)",NonzeroAt(w2,1,0.21));
        Check("gap repaired with silence",SilenceAt(w2,1,0.15));
        Check("tail filled with silence",SilenceAt(w2,1,0.33));

        string p3=Path.Combine(root,"pre-video-clip.wav");
        using (var s=new AudioTimelineWavSink(p3)) {
            s.SetVideoStart(T0);
            s.Write(Packet(AudioTrackKind.System,T0-1_000_000L,9600,1));
            s.Complete(T0+2_000_000L);
        }
        var w3=GetWav(p3,1,0.2);
        Check("pre-video samples properly cropped",NonzeroAt(w3,1,0.02));
        Check("end filled after clipping",SilenceAt(w3,1,0.17));

        string p4=Path.Combine(root,"no-device-no-packets.wav");
        using (var s=new AudioTimelineWavSink(p4)) {
            s.SetVideoStart(T0);
            s.Complete(T0+2_000_000L);
            Check("missing track does not fabricate PCM",s.BytesWritten==0&&!File.Exists(p4));
        }

        Console.WriteLine("AUDIO_OFFLINE_SMOKE_PASS checks="+passed+
             " : no WASAPI device, no microphone, no sound playback");
    }
}