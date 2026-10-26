using System.Text.Json;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using DulukaEngine.Video.Transport.D3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

static partial class Program
{
    static void ProtectSourceContext(ID3D11Device device, bool enabled = true)
    {
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        using var protection = context.QueryInterface<ID3D11Multithread>();
        protection.SetMultithreadProtected(enabled); // test-owned device only
    }

    static D3D11VideoFrame CaptureWrapper(ID3D11Device device, int sequence,
        int width = TextureWidth, Format format = Format.B8G8R8A8_UNorm)
    {
        using var synthetic = new OwnedTextureFrame(device, sequence, format);
        // The production frame class owns a separate reference to SYNTHETIC data.
        // No DdagrabBackend construction, initialization or acquisition occurs.
        return new D3D11VideoFrame(((ID3D11Texture2D)synthetic.NativeTexture).QueryInterface<ID3D11Texture2D>(),
            width, TextureHeight, sequence, sequence * 10_000_000L / 30, sequence * 10_000_000L / 30);
    }

    static void I3FrameAdapter(CaptureTarget target)
    {
        foreach (var rc in new[] { "vbr", "cqp" }) AdapterSession(target, rc);
        using var source = new OwnedFrameSource(target, TextureWidth, TextureHeight);
        source.Initialize(null!);
        var device = source.OwnedDevice;
        using var good = CaptureWrapper(device, 0);
        using var adapter = new BgraFrameAdapter(target, 7, TextureWidth, TextureHeight);
        Reject(() => adapter.Convert(good, target, 7), "context_not_protected", "I3 adapter refuses unprotected source context without changing it");
        ProtectSourceContext(device);
        Reject(() => adapter.Convert(good, target, 6), "stale_source_generation", "I3 adapter rejects stale generation before binding");
        var wrongOutput = Retarget(target, output: target.OutputIndex + 1);
        Reject(() => adapter.Convert(good, wrongOutput, 7), "source_target_mismatch", "I3 adapter rejects wrong output envelope");
        using (var wrong = new BgraFrameAdapter(wrongOutput, 7, TextureWidth, TextureHeight))
            Reject(() => wrong.Convert(good, wrongOutput, 7), "actual_output_mismatch", "I3 adapter checks actual DXGI output before first binding");
        var wrongMonitor = Retarget(target, monitor: new IntPtr(target.Monitor.ToInt64() ^ 0x10000));
        using (var wrong = new BgraFrameAdapter(wrongMonitor, 7, TextureWidth, TextureHeight))
            Reject(() => wrong.Convert(good, wrongMonitor, 7), "actual_output_mismatch", "I3 adapter checks actual monitor before first binding");
        var wrongLuid = new AdapterIdentity(target.Adapter.Low ^ 0x40000000u, target.Adapter.High,
            target.Adapter.VendorId, target.Adapter.DeviceId, false, target.Adapter.DevicePath);
        var wrongTarget = Retarget(target, identity: wrongLuid);
        using (var wrong = new BgraFrameAdapter(wrongTarget, 7, TextureWidth, TextureHeight))
            Reject(() => wrong.Convert(good, wrongTarget, 7), "actual_device_luid_mismatch", "I3 adapter reads real device LUID instead of trusting frame envelope");
        var virtualIdentity = new AdapterIdentity(target.Adapter.Low, target.Adapter.High,
            target.Adapter.VendorId, target.Adapter.DeviceId, false, @"\\?\ROOT#DISPLAY#0000#{synthetic-parsec}");
        Reject(() => { using var bad = new BgraFrameAdapter(Retarget(target, identity: virtualIdentity), 7); },
            "eligible_unrotated_target_required", "I3 adapter rejects Parsec/root identity even with Intel vendor and LUID");
        Reject(() => adapter.Convert(new FrameView(good) { Shared = new IntPtr(1) }, target, 7),
            "borrowed_bgra_texture_required", "I3 adapter rejects shared NT handle without opening or closing it");
        Reject(() => adapter.Convert(new FrameView(good) { MissingTexture = true }, target, 7),
            "borrowed_bgra_texture_required", "I3 adapter rejects missing native texture");
        using (var bad = CaptureWrapper(device, 0, TextureWidth + 2))
            Reject(() => adapter.Convert(bad, target, 7), "borrowed_bgra_texture_required", "I3 adapter rejects wrong frame dimensions");
        using (var bad = CaptureWrapper(device, 0, TextureWidth + 2))
        using (var wrongSize = new BgraFrameAdapter(target, 7, TextureWidth + 2, TextureHeight))
            Reject(() => wrongSize.Convert(bad, target, 7), "descriptor_not_supported", "I3 adapter rejects actual texture size hidden by matching frame metadata");
        using (var bad = CaptureWrapper(device, 0, format: Format.R8G8B8A8_UNorm))
            Reject(() => adapter.Convert(bad, target, 7), "descriptor_not_supported", "I3 adapter checks real descriptor before binding");
        using (var bad = CaptureWrapper(device, 0)) {
            bad.Dispose();
            Reject(() => adapter.Convert(bad, target, 7), "borrowed_bgra_texture_required", "I3 adapter rejects disposed production frame wrapper");
        }
        using (var cancelled = new CancellationTokenSource()) {
            cancelled.Cancel();
            try { adapter.Convert(good, target, 7, cancelled.Token); throw new Exception("FAIL adapter accepted cancellation"); }
            catch (OperationCanceledException) { Check(adapter.TransportsCreated == 0 && !adapter.IsFaulted, "I3 pre-copy cancellation does not bind or fault adapter"); }
        }
        Check(adapter.TransportsCreated == 0, "I3 rejected first frames create no staging transport");
        using (var cpu = adapter.Convert(good, target, 7))
            Check(cpu.Data.Length == TextureWidth * TextureHeight * 3 / 2 && !good.IsDisposed, "I3 adapter binds from production frame texture's own device");
        using (var foreign = new OwnedFrameSource(target, TextureWidth, TextureHeight)) {
            foreign.Initialize(null!); ProtectSourceContext(foreign.OwnedDevice);
            Check(foreign.DeviceIdentity.SameDevice(source.DeviceIdentity) && foreign.OwnedDevice.NativePointer != device.NativePointer,
                "I3 negative input is a DIFFERENT native D3D11 device on SAME physical Intel adapter");
            using var frame = CaptureWrapper(foreign.OwnedDevice, 1);
            Reject(() => adapter.Convert(frame, target, 7), "foreign_device_texture_not_supported", "I3 adapter never rebinds cached transport to same-LUID foreign device");
        }
        Reject(() => adapter.Convert(good, target, 8), "stale_source_generation", "I3 adapter does not silently adopt new source generation");
        using (var cpu = adapter.Convert(good, target, 7))
            Check(adapter.TransportsCreated == 1 && !adapter.IsFaulted && !good.IsDisposed, "I3 adapter reuses healthy transport after rejected input");
        Check(((ID3D11Texture2D)good.NativeTexture).Device.NativePointer != IntPtr.Zero && device.ImmediateContext.NativePointer != IntPtr.Zero,
            "I3 repeated borrows preserve upstream Vortice cached device and context wrappers");
        using (var staging = new BgraStagingTransport(device, target, TextureWidth, TextureHeight, 7)) {
            using (var first = staging.Convert(good, target, 7)) { }
            using var second = staging.Convert(good, target, 7);
            Check(staging.Readbacks == 2 && staging.Maps == staging.Unmaps && !good.IsDisposed,
                "I3 direct staging supports repeated borrowing without disposing texture's cached device wrapper");
        }
        adapter.Dispose(); adapter.Dispose();
        Check(adapter.TransportsDisposed == 1 && !good.IsDisposed, "I3 adapter disposes cached transport once and never upstream frame");
        try { adapter.Convert(good, target, 7); throw new Exception("FAIL disposed adapter accepted frame"); }
        catch (ObjectDisposedException) { Check(true, "I3 disposed adapter rejects conversion"); }

        using (var terminal = new BgraFrameAdapter(target, 7, TextureWidth, TextureHeight)) {
            // Third diagnostics read is after Map/copy/conversion in the existing
            // transport. Deterministic source fault; no simulated GPU-loss claim.
            Reject(() => terminal.Convert(new FrameView(good) { FailFinalDiagnostics = true }, target, 7),
                "synthetic_source_fault", "I3 adapter propagates fault after GPU readback");
            Check(terminal.IsFaulted && !good.IsDisposed, "I3 adapter preserves terminal staging fault and source ownership");
            Reject(() => terminal.Convert(good, target, 7), "frame_adapter_faulted", "I3 faulted adapter cannot silently construct replacement transport");
            Check(terminal.TransportsCreated == 1, "I3 terminal fault creates no hidden recovery device/transport");
        }
        using (var lease = new BgraFrameAdapter(target, 7, TextureWidth, TextureHeight)) {
            using (var cpu = lease.Convert(good, target, 7)) { }
            source.Dispose();
            using var cpu2 = lease.Convert(good, target, 7);
            Check(cpu2.Data.Length > 0 && !good.IsDisposed, "I3 frame and transport COM references survive caller device-wrapper disposal");
        }
    }

    static CaptureTarget Retarget(CaptureTarget t, AdapterIdentity? identity = null, int? output = null, IntPtr? monitor = null) =>
        new(identity ?? t.Adapter, output ?? t.OutputIndex, t.DeviceName, monitor ?? t.Monitor, t.Width, t.Height,
            t.Left, t.Top, t.Rotation, t.Attached, t.OutputTechnology, t.TopologyId, t.MonitorDevicePath);

    sealed class FrameView(ID3D11VideoFrame frame) : ID3D11VideoFrame
    {
        int reads;
        public IntPtr Shared;
        public bool MissingTexture, FailFinalDiagnostics;
        public object NativeTexture => MissingTexture ? null! : frame.NativeTexture;
        public IntPtr SharedHandle => Shared;
        public VideoFrameOrigin Origin => frame.Origin;
        public VideoPixelFormat PixelFormat => frame.PixelFormat;
        public VideoFrameDimensions Dimensions => frame.Dimensions;
        public FrameDiagnostics Diagnostics => FailFinalDiagnostics && ++reads == 3
            ? throw new InvalidOperationException("synthetic_source_fault") : frame.Diagnostics;
        public void Dispose() => throw new Exception("FAIL adapter disposed borrowed frame");
    }

    static void AdapterSession(CaptureTarget target, string rc)
    {
        using var factory = new OwnedFrameFactory(target, TextureWidth, TextureHeight);
        using var engine = new RecordingEngine(I3Log(), factory);
        engine.Initialize(Startup(target, rc));
        ProtectSourceContext(factory.Source!.OwnedDevice);
        using var adapter = new BgraFrameAdapter(target, 1, TextureWidth, TextureHeight);
        var gpu = new List<D3D11VideoFrame>(); var cpu = new List<OwnedNv12Frame>();
        IEnumerable<SessionFrameInput> Inputs() {
            for (int i = 0; i < 60; ++i) {
                OwnedNv12Frame converted;
                using (var frame = CaptureWrapper(factory.Source.OwnedDevice, i)) {
                    gpu.Add(frame); converted = adapter.Convert(frame, target, 1);
                    Check(!frame.IsDisposed && frame.DisposeCount == 0, "I3 adapter borrows capture wrapper through converter completion " + i);
                }
                cpu.Add(converted);
                yield return new SessionFrameInput(converted, target, 1);
            }
        }
        var config = SessionConfig("I3-adapter-unused");
        config.OutputPath = Path.Combine(I3Evidence, "frame-adapter-" + rc + ".mp4");
        var result = engine.StartOwnedFrameSession(config, Inputs());
        File.WriteAllText(Path.Combine(I3Evidence, "frame-adapter-" + rc + "-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Check(result.Pass && result.FullDecodeVerified && result.FinalizationState == "Saved", "I3 frame adapter existing session/QSV/mux/full decode " + rc + ": " + result.ErrorMessage);
        Check(result.FramesSubmitted == 60 && result.EncoderPacketCount == 60 && result.MuxedPacketCount == 60 && result.DecodedFrameCount == 60,
            "I3 adapter submitted/encoded/muxed/decoded 60/60/60/60 " + rc);
        Check(result.FramesCaptured == 0 && result.SyntheticFramesConsumed == 60 && !factory.Source.Started, "I3 adapter session never starts acquisition");
        Check(gpu.Count == 60 && gpu.All(f => f.DisposeCount == 1) && cpu.Count == 60 && cpu.All(f => f.IsDisposed && f.DisposeCount == 1),
            "I3 adapter upstream GPU and returned NV12 owners each dispose exactly once");
        Check(result.FlushedPackets > 0 && factory.Encoders[0].NativeLockedSurfaces == 0 && factory.Encoders[0].NativeOutstandingPackets == 0,
            "I3 adapter delayed packets drained without retained native surfaces");
        VerifyI3MovingMarker(result.OutputPath, TextureWidth, TextureHeight);
        adapter.Dispose();
        Check(adapter.TransportsCreated == 1 && adapter.TransportsDisposed == 1, "I3 adapter reuses and disposes one staging transport for 60 frames");
        Console.WriteLine($"I3_FRAME_ADAPTER_SESSION_PASS rc={rc} decoded={result.DecodedFrameCount} session={result.SessionId}");
    }
}
