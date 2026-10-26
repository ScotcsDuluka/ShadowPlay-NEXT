using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DulukaEngine.Video;

namespace DulukaEngine.Encoder.Qsv;

// Implements the shared packet contract. Never owns a source, MP4, audio or clock.
public sealed class QsvEncoderBackend : IEncoderRuntimeBackend, IEncoderSubmissionBackend, IEncoderDiagnostics
{
    readonly object gate = new();
    readonly AdapterIdentity requested;
    QsvHandle? native;
    EncoderConfig? config;
    EncoderState state = EncoderState.Created;
    readonly Dictionary<ulong, (long Pts, long Sequence)> pending = new();
    readonly Queue<EncodedPacket> ready = new();
    long submitted, packets, flushes, errors, sequence, lastInput = -1, lastOutput = -1;
    string lastError = "", lastType = "";
    bool drained;
    public QsvEncoderBackend(AdapterIdentity target)
    {
        requested = target ?? throw new ArgumentNullException(nameof(target));
        if (!target.HasLuid || !target.IsPhysical || target.Vendor != GpuVendor.Intel)
            throw new ArgumentException("qsv_physical_intel_adapter_required");
    }
    public AdapterIdentity ActualAdapter { get; private set; } = null!;
    public string RuntimeVersion { get; private set; } = "";
    public uint HardwareImplementation { get; private set; }
    public int FrameRateFps => config?.FrameRateFps ?? 0;
    public long BitrateBpsOutput => config?.BitrateBps ?? 0;
    public int EncodeWidthOutput => config?.EncodeWidth ?? 0;
    public int EncodeHeightOutput => config?.EncodeHeight ?? 0;
    public EncoderState CurrentState { get { lock(gate) return state; } }
    public IEncoderDiagnostics Diagnostics => this;
    public long SubmittedFrames { get { lock(gate) return submitted; } }
    public long EncodedPackets { get { lock(gate) return packets; } }
    public long DroppedFrames => 0;
    public long FlushCycles { get { lock(gate) return flushes; } }
    public long ErrorCount { get { lock(gate) return errors; } }
    public string LastErrorIfAny { get { lock(gate) return lastError; } }
    public string LastErrorType { get { lock(gate) return lastType; } }
    public ulong NativeLockedSurfaces { get { lock(gate) return native is null ? 0 : NativeMethods.Counter(native, 3); } }
    public ulong NativeOutstandingPackets { get { lock(gate) return native is null ? 0 : NativeMethods.Counter(native, 2); } }

    public static EncoderConfig ValidateConfiguration(EncoderConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cfg = source.Clone();
        if (cfg.CodecKey is not ("QUICKSYNC_H264" or "QSV_H264" or "h264_qsv"))
            throw new ArgumentException("qsv_h264_codec_required");
        cfg.RateControl = (cfg.RateControl ?? "").Trim().ToLowerInvariant();
        if (cfg.RateControl == "cbr") throw new NotSupportedException("intel_qsv_cbr_known_failure");
        if (cfg.RateControl is not ("vbr" or "cqp")) throw new ArgumentException("qsv_explicit_vbr_or_cqp_required");
        if (cfg.Preset != "veryfast") throw new NotSupportedException("qsv_preset_not_verified:veryfast_required");
        if (cfg.FrameRateFps < 1 || cfg.FrameRateFps > 120 || cfg.GopSize < 1 || cfg.GopSize > 600)
            throw new ArgumentException("qsv_invalid_fps_or_gop");
        if (cfg.ExpectedInputFormat != VideoPixelFormat.Nv12 || cfg.OutputPixelFormat != "nv12")
            throw new NotSupportedException("qsv_native_nv12_system_memory_required");
        if (cfg.ExpectedWidth < 16 || cfg.ExpectedHeight < 16 || cfg.ExpectedWidth > 1920 || cfg.ExpectedHeight > 1080 ||
            (cfg.ExpectedWidth & 1) != 0 || (cfg.ExpectedHeight & 1) != 0)
            throw new ArgumentException("qsv_invalid_dimensions");
        if (cfg.EncodeWidth == 0) cfg.EncodeWidth = cfg.ExpectedWidth;
        if (cfg.EncodeHeight == 0) cfg.EncodeHeight = cfg.ExpectedHeight;
        if (cfg.EncodeWidth != cfg.ExpectedWidth || cfg.EncodeHeight != cfg.ExpectedHeight)
            throw new NotSupportedException("qsv_scaling_not_implemented");
        if (cfg.RateControl == "vbr" && (cfg.BitrateBps < 1000 || cfg.BitrateBps > 30_000_000 || cfg.BitrateBps % 1000 != 0 ||
            cfg.MinrateBps != 0 || cfg.MaxrateBps != cfg.BitrateBps*2 || cfg.BufsizeBps != 0))
            throw new ArgumentException("qsv_vbr_kbps_range_required");
        if (cfg.RateControl == "cqp" && (cfg.Cq < 1 || cfg.Cq > 51)) throw new ArgumentException("qsv_cqp_qp_out_of_range");
        if (cfg.RateControl == "cqp" && (cfg.BitrateBps!=0 || cfg.MinrateBps!=0 || cfg.MaxrateBps!=0 || cfg.BufsizeBps!=0))
            throw new ArgumentException("qsv_cqp_has_no_bitrate_target");
        if (cfg.MaxInFlightFrames < 1 || cfg.MaxInFlightFrames > 8 || cfg.FlushTimeoutMs < 1 || cfg.FlushTimeoutMs > 30000 ||
            cfg.StopTimeoutMs < 1 || cfg.StopTimeoutMs > 30000) throw new ArgumentException("qsv_invalid_bounded_budget");
        return cfg;
    }
    void Require(params EncoderState[] allowed)
    {
        if(state == EncoderState.Disposed) throw new ObjectDisposedException(nameof(QsvEncoderBackend));
        if(!allowed.Contains(state)) throw new InvalidOperationException("qsv_invalid_state:" + state);
    }
    void NativeCheck(int status)
    {
        if(status < 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(NativeMethods.Error(native!)) ?? "qsv_native_failure");
    }
    void Fault(Exception ex, string type)
    { state = EncoderState.Faulted; ++errors; lastError = ex.Message; lastType = type; }
    public void Initialize(EncoderConfig source)
    {
        lock(gate)
        {
            Require(EncoderState.Created);
            var cfg = ValidateConfiguration(source); // invalid config is retryable; no GPU touched
            try
            {
                var error = new StringBuilder(2048);
                if(NativeMethods.AbiVersion()!=3) throw new InvalidOperationException("qsv_native_abi_mismatch");
                int status = NativeMethods.Create(requested.Low, requested.High, cfg.EncodeWidth, cfg.EncodeHeight,
                    cfg.FrameRateFps, cfg.GopSize, cfg.RateControl == "vbr" ? 1 : 2, (uint)cfg.BitrateBps, cfg.Cq,
                    cfg.MaxInFlightFrames, cfg.FlushTimeoutMs,cfg.Color is null?0:cfg.Color.FullRange?2:1, out var handle, error, error.Capacity);
                if(status != 0) throw new InvalidOperationException("qsv_init:" + error);
                native = new QsvHandle(handle);
                NativeCheck(NativeMethods.Identity(native, out uint low, out int high, out uint vendor, out uint device,
                    out uint major, out uint minor, out uint impl));
                ActualAdapter = new(low, high, vendor, device, false, requested.DevicePath);
                if(!requested.SameDevice(ActualAdapter) || !ActualAdapter.IsPhysical)
                    throw new InvalidOperationException("qsv_actual_adapter_identity_mismatch");
                RuntimeVersion = $"{major}.{minor}"; HardwareImplementation = impl;
                config = cfg; state = EncoderState.Initialized;
            }
            catch(Exception ex) { native?.Dispose(); native = null; Fault(ex,"runtime"); throw; }
        }
    }
    public void Start()
    {
        lock(gate)
        {
            Require(EncoderState.Initialized, EncoderState.Stopped, EncoderState.Running);
            if(state == EncoderState.Running) return;
            try {
                NativeCheck(NativeMethods.Begin(native!));
                if(pending.Count != 0 || ready.Count != 0) throw new InvalidOperationException("qsv_restart_with_pending_output");
                lastInput = lastOutput = -1; drained = false; state = EncoderState.Running;
            } catch(Exception ex) { Fault(ex,"runtime"); throw; }
        }
    }
    EncodedPacket? Receive(int wait)
    {
        byte[] output = new byte[1024 * 1024];
        int status = NativeMethods.Receive(native!,output,output.Length,out int length,out ulong pts,out long dts,out int key,wait);
        NativeCheck(status);
        if(status == 1) return null;
        if(status == 2)
        {
            if(length < 1 || length > 16*1024*1024) throw new InvalidOperationException("qsv_packet_size_limit");
            output = new byte[length];
            status = NativeMethods.Receive(native!,output,output.Length,out length,out pts,out dts,out key,wait); NativeCheck(status);
        }
        if(status != 0 || !pending.Remove(pts,out var input)) throw new InvalidOperationException("qsv_packet_pts_not_submitted");
        if(input.Pts <= lastOutput || (dts >= 0 && (ulong)dts != pts)) throw new InvalidOperationException("qsv_packet_reordering_not_supported");
        lastOutput = input.Pts;
        return new(new PacketMetadata(input.Sequence,input.Pts,input.Pts,10_000_000L/config!.FrameRateFps,
            key!=0,true,config.CodecKey,0),output,length);
    }
    EncodedPacket? Deliver(EncodedPacket? packet) { if(packet is not null) ++packets; return packet; }
    public EncoderSubmission Submit(IVideoFrame frame)
    {
        lock(gate)
        {
            Require(EncoderState.Running);
            ArgumentNullException.ThrowIfNull(frame);
            if(config!.Color is not null && (frame is not IColorVideoFrame tagged || !config.Color.SameColor(tagged.Color)))
                throw new ArgumentException("qsv_frame_color_descriptor_mismatch");
            if(frame is not INv12VideoFrame cpu || cpu.IsDisposed || frame.Origin != VideoFrameOrigin.CpuMemory ||
                frame.PixelFormat != VideoPixelFormat.Nv12 || frame.Dimensions.Width != config!.ExpectedWidth ||
                frame.Dimensions.Height != config.ExpectedHeight) throw new ArgumentException("qsv_input_frame_contract_mismatch");
            long pts100ns = frame.Diagnostics.PresentationTimestampTicks;
            if(pts100ns < 0 || pts100ns <= lastInput) throw new ArgumentException("qsv_input_pts_not_monotonic");
            ulong pts90k = checked((ulong)((decimal)pts100ns*90000/10_000_000));
            if(pending.ContainsKey(pts90k)) throw new ArgumentException("qsv_timestamp_precision_collision");
            try
            {
                if(drained) throw new InvalidOperationException("qsv_submit_after_flush_requires_restart");
                // Bound native jobs before submitting; retain earlier output for caller delivery.
                while(NativeMethods.Counter(native!,2) >= (ulong)config.MaxInFlightFrames)
                {
                    var prior = Receive(config.FlushTimeoutMs) ?? throw new TimeoutException("qsv_submit_sync_timeout");
                    ready.Enqueue(prior);
                }
                if(ready.Count > 32) throw new InvalidOperationException("qsv_managed_packet_queue_limit");
                byte[] copy = cpu.Data.ToArray();
                NativeCheck(NativeMethods.Submit(native!,copy,copy.Length,cpu.Stride,pts90k));
                pending.Add(pts90k,(pts100ns,sequence++)); ++submitted; lastInput=pts100ns;
                var packet = ready.Count > 0 ? ready.Dequeue() : Receive(0);
                return new(true,Deliver(packet));
            }
            catch(Exception ex) { Fault(ex,"runtime"); throw; }
        }
    }
    public bool Encode(IVideoFrame frame, ref EncodedPacket packet)
    {
        var result = Submit(frame); packet = result.Packet;
        return packet is not null; // frame accepted even when delayed; use Submit for explicit evidence
    }
    public int Flush(Action<EncodedPacket> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock(gate)
        {
            Require(EncoderState.Running); state=EncoderState.Flushing;
            var clock=Stopwatch.StartNew(); int count=0;
            void Send(EncodedPacket packet) { Deliver(packet); ++count; sink(packet); }
            try
            {
                while(ready.TryDequeue(out var packet)) Send(packet);
                while(!drained || NativeMethods.Counter(native!,2)>0)
                {
                    if(clock.ElapsedMilliseconds>=config!.FlushTimeoutMs) throw new TimeoutException("qsv_flush_deadline");
                    // Clear outstanding sync operations before submitting another drain request.
                    if(NativeMethods.Counter(native!,2)>0)
                    {
                        var p=Receive(Math.Max(1,config.FlushTimeoutMs-(int)clock.ElapsedMilliseconds));
                        if(p is not null) Send(p);
                        continue;
                    }
                    int status=NativeMethods.Drain(native!); NativeCheck(status); drained=status==1;
                }
                if(pending.Count!=0 || NativeMethods.Counter(native!,3)!=0) throw new InvalidOperationException("qsv_flush_lost_frame_or_locked_surface");
                ++flushes; state=EncoderState.Running; return count;
            }
            catch(Exception ex) { Fault(ex,"shutdown"); throw; }
        }
    }
    public void Stop()
    {
        lock(gate)
        {
            if(state==EncoderState.Disposed) throw new ObjectDisposedException(nameof(QsvEncoderBackend));
            if(state!=EncoderState.Running) return;
            if(pending.Count>0 || ready.Count>0) {
                var ex=new InvalidOperationException("qsv_flush_to_session_sink_required_before_stop"); Fault(ex,"shutdown"); throw ex;
            }
            state=EncoderState.Stopped;
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(state==EncoderState.Disposed) return;
            while(ready.TryDequeue(out var p)) p.Dispose();
            native?.Dispose(); native=null; pending.Clear(); state=EncoderState.Disposed;
        }
    }
}
