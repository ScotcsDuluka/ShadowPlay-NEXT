using Vortice.Direct3D11;

namespace DulukaEngine.Video.Transport.D3D11;

public sealed record RoutedSourceFrame(OwnedNv12Frame Frame, CaptureTarget Target, long Generation, long PreviousGeneration);

// Session-owned source lifecycle and handoff; no device creation or recording
// engine. TryPush ownership follows the existing sink transfer contract.
public sealed class BgraSessionSource : IVideoFrameSink, ISourceLifecycleSink, IDisposable
{
    readonly object gate=new(), readGate=new();
    readonly Queue<ISourceStampedVideoFrame> queue=new();
    readonly CaptureTarget target;
    readonly int width,height,fps,capacity,waitMs;
    readonly CancellationTokenSource stopped=new();
    readonly VideoColorDescriptor color;
    BgraFrameAdapter adapter;
    ID3D11Device? boundDevice;
    long generation, transition, lastSequence=-1, lastQpc=-1, routed;
    SourceFaultKind? fault;
    long recoveryStarted;
    bool RecoveryExpired()=>System.Diagnostics.Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds>=waitMs;
    public static long QpcTo100ns(long value,long frequency) {
        return SourceTimeUnits.QpcTo100ns(value,frequency);
    }
    bool complete, disposed;
    int reading;
    int deviceBound;
    sealed class InterruptedFrame : Exception { }
    public bool NativeDeviceBound=>Volatile.Read(ref deviceBound)!=0;
    public long InitialGeneration { get; }
    public CaptureTarget Target=>target;
    public VideoColorDescriptor Color=>color;
    public int FrameRateFps=>fps;
    public long Accepted { get; private set; }
    public long Rejected { get; private set; }
    public long QueueDrops { get; private set; }
    public long SequenceGaps { get; private set; }
    public long DuplicateFrames { get; private set; }
    public long Recoveries { get; private set; }
    public long RawFramesDisposed { get; private set; }
    public long RoutedFrames=>Interlocked.Read(ref routed);
    public string State { get { lock(gate) return disposed?"Disposed":stopped.IsCancellationRequested?"Stopped":fault==SourceFaultKind.AccessLost?"SourceInterrupted":fault.HasValue?"Faulted:"+fault:complete?"Completed":"Running"; } }

    public BgraSessionSource(CaptureTarget selected,long sourceGeneration,int width,int height,int fps,
        VideoColorDescriptor color,int capacity=4,int waitTimeoutMs=2000)
    {
        if(fps<1 || fps>120 || capacity<1 || capacity>32 || waitTimeoutMs<1 || waitTimeoutMs>30000)
            throw new ArgumentException("source_invalid_bounded_plan");
        this.color=color ?? throw new ArgumentNullException(nameof(color));
        target=selected; generation=InitialGeneration=sourceGeneration; this.width=width; this.height=height;
        this.fps=fps; this.capacity=capacity; waitMs=waitTimeoutMs;
        adapter=new(selected,sourceGeneration,width,height,5000,color);
    }
    public PushOutcome TryPush(FrameAcquisitionResult result)
    {
        lock(gate) {
            if(result.Status==FrameAcquisitionStatus.Error) { SourceFault(SourceFaultKind.InvalidFrame); return PushOutcome.Dropped; }
            if(result.Status!=FrameAcquisitionStatus.FrameAvailable) return PushOutcome.Dropped;
            if(disposed || complete || stopped.IsCancellationRequested || fault.HasValue) { ++Rejected; return PushOutcome.Dropped; }
            if(result.Frame is not ISourceStampedVideoFrame frame) {
                ++Rejected; SourceFault(SourceFaultKind.InvalidFrame); return PushOutcome.Dropped;
            }
            if(frame.SourceGeneration!=generation) { ++Rejected; return PushOutcome.Dropped; }
            if(frame.SourceTarget is null || !SameTarget(frame.SourceTarget)) {
                ++Rejected; SourceFault(SourceFaultKind.OutputChanged); return PushOutcome.Dropped;
            }
            if(queue.Count>=capacity) { ++QueueDrops; return PushOutcome.Dropped; }
            queue.Enqueue(frame); ++Accepted; Monitor.PulseAll(gate); return PushOutcome.Pushed;
        }
    }
    bool SameTarget(CaptureTarget actual)=>target.SameOutput(actual) && target.TopologyId==actual.TopologyId &&
        string.Equals(target.Adapter.DevicePath,actual.Adapter.DevicePath,StringComparison.OrdinalIgnoreCase);
    void DisposeFrame(ISourceStampedVideoFrame frame) { frame.Dispose(); ++RawFramesDisposed; }
    void ClearQueue() { while(queue.TryDequeue(out var frame)) DisposeFrame(frame); }
    public void SourceFault(SourceFaultKind kind)
    {
        lock(gate) {
            if(disposed || stopped.IsCancellationRequested) return;
            if(!fault.HasValue || (fault==SourceFaultKind.AccessLost && kind!=SourceFaultKind.AccessLost)) {
                fault=kind; recoveryStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            }
            ClearQueue(); Monitor.PulseAll(gate);
        }
    }
    // Explicit session/controller authorization only. No automatic device or
    // target replacement. First next frame must still use the bound native device.
    public void AuthorizeRecovery(CaptureTarget current,long nextGeneration)
    {
        lock(readGate) lock(gate) {
            if(fault!=SourceFaultKind.AccessLost || nextGeneration<=generation || !SameTarget(current) ||
                RecoveryExpired() || Recoveries>=1 || stopped.IsCancellationRequested || disposed)
                throw new InvalidOperationException("source_recovery_not_authorized");
            adapter.Dispose(); transition=generation; generation=nextGeneration; lastSequence=-1;
            adapter=new(target,generation,width,height,5000,color); fault=null; ++Recoveries;
            Monitor.PulseAll(gate);
        }
    }
    public void Complete() { lock(gate) { complete=true; Monitor.PulseAll(gate); } }
    public void Stop() { stopped.Cancel(); lock(gate) { ClearQueue(); Monitor.PulseAll(gate); } }
    public RoutedSourceFrame? Read(CancellationToken cancellation=default)
    {
        if(Interlocked.CompareExchange(ref reading,1,0)!=0) throw new InvalidOperationException("source_single_session_reader_required");
        try { while(true) { try { return ReadCore(cancellation); } catch(InterruptedFrame) { } } }
        finally { Volatile.Write(ref reading,0); }
    }
    RoutedSourceFrame? ReadCore(CancellationToken cancellation)
    {
        // Wait outside readGate so recovery/shutdown can proceed while idle.
        ISourceStampedVideoFrame raw;
        var idle=System.Diagnostics.Stopwatch.StartNew();
        lock(gate) {
            while(true) {
                cancellation.ThrowIfCancellationRequested(); stopped.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed,this);
                if(fault.HasValue) {
                    if(fault!=SourceFaultKind.AccessLost || RecoveryExpired())
                        throw new InvalidOperationException("source_fault:"+fault);
                } else if(queue.TryDequeue(out raw!)) break;
                else if(complete) return null;
                if(idle.ElapsedMilliseconds>=waitMs) throw new TimeoutException("source_frame_wait_deadline");
                Monitor.Wait(gate,10);
            }
        }
        lock(readGate) {
            OwnedNv12Frame? cpu=null;
            try {
                using var cancel=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stopped.Token);
                cancel.Token.ThrowIfCancellationRequested();
                lock(gate) {
                    if(fault==SourceFaultKind.AccessLost || raw.SourceGeneration!=generation) throw new InterruptedFrame();
                    if(fault.HasValue) throw new InvalidOperationException("source_stale_inflight_frame");
                }
                if(raw.NativeTexture is not ID3D11Texture2D texture) throw new InvalidOperationException("source_missing_texture");
                using(var owner=texture.Device.QueryInterface<ID3D11Device>()) {
                    if(boundDevice is null) { boundDevice=owner.QueryInterface<ID3D11Device>(); Volatile.Write(ref deviceBound,1); }
                    else if(boundDevice.NativePointer!=owner.NativePointer) throw new InvalidOperationException("source_device_changed_requires_new_session");
                }
                long qpc=raw.Diagnostics.CaptureTimeTicks, sequence=raw.Diagnostics.Sequence;
                if(sequence<=lastSequence || qpc<=lastQpc) { ++DuplicateFrames; throw new InvalidOperationException("source_duplicate_or_reordered_frame"); }
                if(qpc<0) throw new InvalidOperationException("source_invalid_absolute_qpc");
                if(lastSequence>=0) SequenceGaps+=sequence-lastSequence-1;
                cpu=adapter.Convert(raw,raw.SourceTarget,raw.SourceGeneration,cancel.Token);
                // Preserve source QPC/sequence unchanged. Only the session router
                // owns epoch selection, CFR slot validation and encoder PTS.
                lock(gate) {
                    cancel.Token.ThrowIfCancellationRequested();
                    if(fault==SourceFaultKind.AccessLost || generation!=raw.SourceGeneration) throw new InterruptedFrame();
                    if(fault.HasValue) throw new InvalidOperationException("source_fault_during_conversion");
                    lastSequence=sequence; lastQpc=qpc; ++routed;
                    var value=new RoutedSourceFrame(cpu,target,generation,transition); transition=0; cpu=null; return value;
                }
            } catch(OperationCanceledException) { throw; }
            catch(InterruptedFrame) { throw; }
            catch { SourceFault(SourceFaultKind.InvalidFrame); throw; }
            finally { cpu?.Dispose(); lock(gate) DisposeFrame(raw); }
        }
    }
    public void Dispose()
    {
        Stop(); lock(readGate) lock(gate) {
            if(disposed) return; disposed=true; adapter.Dispose(); boundDevice?.Dispose(); boundDevice=null;
        }
    }
}
