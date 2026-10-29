using DulukaEngine.DesktopAvClock.Native;
using System.Diagnostics;

int passed=0;
void Good(bool condition,string name)
{
    if(!condition)throw new Exception("FAIL "+name);
    Console.WriteLine("PASS "+name);
    passed++;
}
void Bad(Action action,string name)
{
    try {action();throw new Exception("FAIL accepted "+name);}
    catch(InvalidDataException){Good(true,name);}
}
const long epoch=100_000_000;
const long frequency=10_000_000;
long origin100ns=DesktopAvSessionClock.QpcTo100ns(epoch,frequency);
Good(DesktopAvSessionClock.QpcTo100ns(3_000_000,3_000_000)==10_000_000,
    "DXGI raw QPC is converted once using real QPC frequency");
Good(DesktopAvSessionClock.SamplesTo100ns(48000,48000)==10_000_000,
    "WASAPI PCM content duration uses exact cumulative samples");

var mode=new DesktopAvSessionClock(epoch,frequency);
var first=mode.OfferRealVideo(1,0,epoch);
Good(first.Count==1&&!first[0].IsHeld&&first[0].Pts100ns==0,
    "Desktop first real frame starts one session epoch");
mode.SwitchVideoSource(1,2,epoch+1_000_000);
Bad(()=>mode.OfferRealVideo(1,1,epoch+1_250_000),
    "late frame from old Desktop source is rejected after mode switch");
var full=mode.OfferRealVideo(2,0,epoch+2_000_000);
Good(full.Count==6&&full.Count(x=>x.IsHeld)==5 &&
    full[0].Pts100ns==DesktopAvSessionClock.SamplesTo100ns(1,30) &&
    full[^1].Pts100ns==2_000_000 && full[^1].SourceGeneration==2,
    "Desktop to Fullscreen gap has five explicit hold slots and new real frame at 200ms");
Good(full.Take(5).All(x=>x.SourceGeneration==1&&x.SourceFrameSequence==0),
    "held frames carry last valid Desktop frame identity, not fabricated Fullscreen pixels");
mode.SwitchVideoSource(2,3,epoch+2_500_000);
var desktopAgain=mode.OfferRealVideo(3,0,epoch+3_000_000);
Good(desktopAgain.Count==3&&desktopAgain.Count(x=>x.IsHeld)==2 &&
    desktopAgain[^1].Pts100ns==3_000_000,
    "Fullscreen to Desktop returns to original epoch without losing 100ms");
Bad(()=>mode.SwitchVideoSource(2,4,epoch+3_100_000),
    "unapproved generation transition fails closed");
Bad(()=>mode.OfferRealVideo(3,0,epoch+3_333_333),
    "duplicate source frame sequence fails closed");
Bad(()=>mode.OfferRealVideo(3,1,epoch+3_000_000),
    "duplicate QPC present fails closed");
Good(mode.VideoFrameSlots==10,
    "CFR timeline maintains all ten slots across two source changes");

var longGap=new DesktopAvSessionClock(epoch,frequency,maxHoldMs:250);
longGap.OfferRealVideo(1,0,epoch);
Bad(()=>longGap.OfferRealVideo(1,1,epoch+4_000_000),
    "400ms video outage exceeds 250ms bounded freeze policy");
Good(longGap.VideoFrameSlots==1,
    "failed large video gap does not silently advance output timeline");
Bad(()=>new DesktopAvSessionClock(epoch,frequency).OfferRealVideo(1,0,epoch+333_333),
    "first video frame after missing initial slot rejected rather than invented");

var audio=new DesktopAvSessionClock(epoch,frequency);
audio.OfferRealVideo(1,0,epoch);
var a0=audio.OfferAudio(20_000,origin100ns,1024);
Good(a0.Pts100ns==0&&a0.GapFrames==0,
    "first WASAPI timestamp anchors audio on same session clock");
var a1=audio.OfferAudio(21_024,
    origin100ns+DesktopAvSessionClock.SamplesTo100ns(1024,48000)+120_000,1024);
Good(a1.Pts100ns==a0.EndPts100ns&&a1.GapFrames==0&&
    Math.Abs(a1.QpcDeviationMs-12)<.001&&!a1.TimestampUnreliable,
    "12ms packet QPC jitter is diagnostic only; audio PCM PTS stays continuous");
Bad(()=>audio.OfferAudio(21_024,origin100ns+500_000,1024),
    "reordered device-position audio packets fail closed");
var missing=audio.OfferAudio(22_048+480,
    origin100ns+DesktopAvSessionClock.SamplesTo100ns(2048+480,48000),1024);
Good(missing.GapFrames==480&&audio.PendingSilenceFrames==480,
    "10ms missing audio is an explicit silence compensation obligation");
Bad(()=>audio.OfferAudio(23_552,origin100ns+700_000,1024),
    "next audio packet blocked until missing PCM silence is actually inserted");
Bad(()=>audio.ConfirmSilenceWritten(479),
    "incomplete silence materialization cannot clear recovery gate");
audio.ConfirmSilenceWritten(480);
Good(audio.PendingSilenceFrames==0,
    "exact 480 PCM silence samples confirm gap recovered");
var late=new DesktopAvSessionClock(epoch,frequency);
Bad(()=>late.OfferAudio(0,origin100ns,1024,timestampError:true),
    "invalid first WASAPI QPC is never guessed as audio origin");
Bad(()=>new DesktopAvSessionClock(epoch,frequency).OfferAudio(0,origin100ns-10_000,1024),
    "audio beginning before session epoch needs explicit pre-roll policy");
var severe=new DesktopAvSessionClock(epoch,frequency);
severe.OfferAudio(0,origin100ns,1024);
Bad(()=>severe.OfferAudio(1024+5000,origin100ns+1_000_000,1024),
    "audio gap greater than 100ms rejected rather than silently shortened");

var ending=new DesktopAvSessionClock(epoch,frequency);
for(int i=0;i<60;i++)
    ending.OfferRealVideo(1,i,epoch+DesktopAvSessionClock.SamplesTo100ns(i,30));
long samples=0;
for(int i=0;i<94;i++){
    ending.OfferAudio(samples,origin100ns+DesktopAvSessionClock.SamplesTo100ns(samples,48000),1024);
    samples+=1024;
}
var verified=ending.VerifyEnd(60,94L*1024);
Good(verified.Accepted&&Math.Abs(verified.EndSkewMs)<10,
    "two-second 60 video / 94 AAC-size PCM-packet timeline ends within 10ms");
Good(!ending.VerifyEnd(59,94L*1024).Accepted,
    "reported missing video frame cannot pass Saved gate");
Good(!ending.VerifyEnd(60,95L*1024).Accepted,
    "reported missing audio packet cannot pass Saved gate");
var delayed=new DesktopAvSessionClock(epoch,frequency);
for(int i=0;i<30;i++)
    delayed.OfferRealVideo(1,i,epoch+DesktopAvSessionClock.SamplesTo100ns(i,30));
for(int i=0;i<47;i++)
    delayed.OfferAudio((long)i*1024,origin100ns+500_000+
        DesktopAvSessionClock.SamplesTo100ns((long)i*1024,48000),1024);
Good(!delayed.VerifyEnd(30,47L*1024).Accepted,
    "audio starting 50ms late fails 40ms acceptance even with valid PCM samples");

// Thirty-minute arithmetic drift stress: time advances from the shared epoch.
// No screen, microphone, speakers, FFmpeg or any capture devices are opened.
var stress=new DesktopAvSessionClock(epoch,frequency,frameRate:60);
const int seconds=1800;
var stopwatch=Stopwatch.StartNew();
for(int i=0;i<seconds*60;i++)
{
    long ticks=DesktopAvSessionClock.SamplesTo100ns(i,60);
    long raw=epoch+ticks; // this test uses a 10MHz QPC frequency.
    var slots=stress.OfferRealVideo(1,i,raw);
    if(slots.Count!=1||slots[0].IsHeld)throw new Exception("FAIL 30min_video_drift");
}
long frameCounter=0;
for(int i=0;i<seconds*48000/1024;i++)
{
    long timestamp=origin100ns+DesktopAvSessionClock.SamplesTo100ns(frameCounter,48000);
    // Keep the device cursor authoritative; QPC arrival noise does not retime samples.
    long noise=(i>0&&i%200==0)?200_000:0;
    var pkt=stress.OfferAudio(frameCounter,timestamp+noise,1024);
    if(pkt.GapFrames!=0)throw new Exception("FAIL 30min_audio_gap");
    frameCounter+=1024;
}
var result=stress.VerifyEnd(seconds*60,seconds*48000L);
Good(result.Accepted&&Math.Abs(result.EndSkewMs)<0.0001,
    "30-minute 60fps/48kHz arithmetic clocks end exactly together without PCM packet rounding drift");
Good(stress.VideoFrameSlots==108_000&&frameCounter==86_400_000,
    "30min stress conserves 108,000 video slots and 86.4M PCM samples");
Good(stopwatch.ElapsedMilliseconds<15000,
    "30min synthetic timeline verification is bounded in CPU time");

// Additional high-rate timebase check: a 200ms video transition at 240fps
// needs 47 HELD slots with explicitly retained old-frame provenance.
var fast=new DesktopAvSessionClock(epoch,frequency,frameRate:240,maxHoldMs:250);
fast.OfferRealVideo(1,0,epoch);
fast.SwitchVideoSource(1,2,epoch+500_000);
var highRateSwitch=fast.OfferRealVideo(2,0,epoch+2_000_000);
Good(highRateSwitch.Count==48&&highRateSwitch.Count(x=>x.IsHeld)==47 &&
     highRateSwitch[^1].Pts100ns==2_000_000,
    "240fps Desktop/Fullscreen mode change preserves all 200ms CFR slots");
Good(highRateSwitch.Take(47).All(x=>x.SourceGeneration==1 && x.IsHeld),
    "240fps held slots retain source provenance across 47 repeated-frame positions");
var noisy=new DesktopAvSessionClock(epoch,frequency);
noisy.OfferAudio(10_000,origin100ns,1024);
var noisyPacket=noisy.OfferAudio(11_024,
    origin100ns+DesktopAvSessionClock.SamplesTo100ns(1024,48000)+600_000,1024);
Good(noisyPacket.TimestampUnreliable &&
     noisyPacket.Pts100ns==DesktopAvSessionClock.SamplesTo100ns(1024,48000),
    "60ms WASAPI QPC timestamp jitter is marked unreliable without retiming audio");
AudioDeviceDriftTests.Run();
Console.WriteLine($"AORUS_DESKTOP_NATIVE_AV_CONTINUITY_PASS checks={passed} " +
    $"video_sec=1800 synthetic=true live_desktop=false audio_devices=false " +
    $"runtime_ffmpeg=false production=false elapsed_ms={stopwatch.ElapsedMilliseconds}");
