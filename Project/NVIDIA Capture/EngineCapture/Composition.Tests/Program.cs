using DulukaEngine.Diagnostics;
using DulukaEngine.Encoder;
using DulukaEngine.Recording;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;

// All device identities, backends and output handles in this program are synthetic.
// No production DXGI inventory, Initialize, FFmpeg or WASAPI path is executed.
static class Program
{
    static int passed;
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    static void Reject(Action action, string code, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        { Check(ex.Message.Contains(code, StringComparison.Ordinal), name); return; }
        throw new Exception("FAIL did not reject: " + name);
    }
    static AdapterIdentity Id(uint low, GpuVendor vendor, bool software = false, string? path = null, int high = 0) =>
        new(low, high, (uint)vendor, 0x1234, software,
            path ?? $@"\\?\PCI#VEN_{(uint)vendor:X4}&DEV_1234#synthetic");
    static CaptureTarget Target(AdapterIdentity adapter, int output = 0, int handle = 1, uint? tech = 5,
        bool attached = true, string monitorPath = "monitor-a", int width = 1920) =>
        new(adapter, output, "DISPLAY-TEST", new IntPtr(handle), width, 1080, 0, 0, 1, attached, tech, "synthetic", monitorPath);
    static DxgiDisplayInventory.Snapshot Snapshot(params CaptureTarget[] targets) =>
        new(targets.Select(t => t.Adapter).Distinct().Select((a, i) => new DxgiDisplayInventory.AdapterEntry(a, i, "synthetic")).ToList(), targets.ToList());
    static EngineStartupConfig Startup(CaptureTarget? target = null, string codec = "auto") =>
        new() { CaptureTarget = target, CodecKey = codec, Fps = 30, RateControl = "vbr" };
    static EngineLogger Logger() => new("I1-offline", EngineLogger.LogLevel.Error, _ => { });

    static void IdentityAndPolicy()
    {
        var intel = Id(11, GpuVendor.Intel);
        var nvidia = Id(22, GpuVendor.Nvidia);
        var ti = Target(intel, 2);
        var tn = Target(nvidia);
        Check(intel.IsPhysical && intel.Vendor == GpuVendor.Intel, "Intel PCI identity classified from its adapter");
        Check(nvidia.IsPhysical && nvidia.Vendor == GpuVendor.Nvidia, "NVIDIA PCI identity classified from its adapter");
        Check(!Id(0, GpuVendor.Intel).HasLuid, "zero LUID unavailable");
        Check(Id(1, GpuVendor.Intel, high: -7).SameDevice(Id(1, GpuVendor.Intel, high: -7)), "signed high LUID preserved");
        Check(!intel.SameDevice(Id(11, GpuVendor.Intel, high: 1)), "high LUID mismatch rejected");
        Check(!intel.SameDevice(nvidia), "different GPU identity rejected");
        Check(!intel.SameDevice(Id(11, GpuVendor.Nvidia)), "same LUID cannot hide vendor mismatch");
        Check(!Id(1, GpuVendor.Intel, software: true).IsPhysical, "software flag rejected despite PCI path");
        var parsec = Id(33, GpuVendor.Intel, path: @"\\?\ROOT#DISPLAY#PARSEC");
        Check(!parsec.IsPhysical, "Parsec ROOT path rejected despite Intel vendor metadata");
        Check(!Id(1, GpuVendor.Intel, path: "").IsPhysical, "unavailable adapter path fails closed");
        Check(!Id(1, GpuVendor.Intel, path: @"\\?\PCI#VEN_10DE&DEV_1234").IsPhysical, "PCI path must match actual vendor/device");
        Check(ti.Eligible, "Intel nonzero output eligible");
        using (var backend = new DdagrabBackend(Logger()))
        {
            backend.RequestedTarget = ti;
            Reject(() => backend.RequestedOutputIndex = 0, "index_conflict", "DXGI setter cannot discard explicit target identity");
            Check(backend.RequestedTarget.SameOutput(ti), "DXGI requested target retained before initialization");
        }
        Check(!Target(parsec).Eligible, "virtual display cannot become capture target");
        foreach (uint tech in new uint[] { 15, 16, 17, uint.MaxValue })
            Check(!Target(intel, tech: tech).Eligible, $"indirect or ambiguous technology {tech} rejected");
        Check(!Target(intel, tech: null).Eligible, "missing topology evidence rejected");
        Check(!Target(intel, attached: false).Eligible, "disconnected output rejected");
        Check(!Target(intel, handle: 0).Eligible, "missing HMONITOR rejected");
        Check(!Target(intel, width: 0).Eligible, "invalid dimensions rejected");
        Check(!Target(intel, monitorPath: "").Eligible, "missing monitor device identity rejected");
        Check(!Target(Id(0, GpuVendor.Intel)).Eligible, "zero adapter LUID cannot be eligible");
        var snapshot = Snapshot(ti, tn, Target(parsec));
        var factory = new RecordingBackendFactory(() => snapshot);
        var p = factory.Plan(Startup(ti));
        Check(p.Provider.Id == "intel:qsv-h264" && p.Target.OutputIndex == 2, "auto provider follows selected Intel LUID/output");
        Check(!p.RuntimeWired && p.Reason == "intel_native_live_input_not_verified", "Intel planned capability is never readiness");
        Reject(() => factory.CreateCapture(p, false, Logger()), "not_verified", "Intel blocked before capture object creation");
        Reject(() => factory.CreateEncoder(p, Logger()), "not_verified", "Intel blocked before encoder object creation");
        foreach (var alias in new[] { "QUICKSYNC_H264", "h264_qsv" })
            Check(factory.Plan(Startup(ti, alias)).Provider.Id == "intel:qsv-h264", "Intel codec alias " + alias);
        foreach (var alias in new[] { "NVENC_H264", "h264_nvenc" })
            Check(factory.Plan(Startup(tn, alias)).Provider.Id == "nvidia:nvenc-h264", "NVIDIA codec alias " + alias);
        Check(factory.Plan(Startup(tn)).RuntimeWired, "NVIDIA implementation remains registered (hardware proof still required)");
        Reject(() => factory.Plan(Startup(ti, "NVENC_H264")), "vendor_mismatch", "NVIDIA encoder cannot run on Intel capture target");
        Reject(() => factory.Plan(Startup(tn, "QUICKSYNC_H264")), "vendor_mismatch", "Intel encoder cannot run on NVIDIA capture target");
        Reject(() => factory.Plan(Startup(ti, "HEVC_QSV")), "unsupported_native_encoder", "unimplemented codec rejected");
        var wrongApi = Startup(tn); wrongApi.RequestedCaptureMethod = "gfxcapture";
        Reject(() => factory.Plan(wrongApi), "unsupported_native_capture_api", "WGC not promoted to native DXGI");
        var conflict = Startup(ti); conflict.CaptureOutputIndex = 0;
        Reject(() => factory.Plan(conflict), "index_conflict", "adapter-local output conflict rejected");
        Reject(() => new RecordingBackendFactory(() => Snapshot(tn)).Plan(Startup(ti)), "stale_or_ambiguous", "disappeared adapter never switches to NVIDIA");
        Reject(() => new RecordingBackendFactory(() => Snapshot(Target(intel, 0))).Plan(Startup(ti)), "stale_or_ambiguous", "output reorder never switches monitor");
        Reject(() => new RecordingBackendFactory(() => Snapshot(Target(intel, 2, handle: 5))).Plan(Startup(ti)), "stale_or_ambiguous", "monitor handle change requires re-resolution");
        Reject(() => new RecordingBackendFactory(() => Snapshot(Target(intel, 2, monitorPath: "replacement"))).Plan(Startup(ti)), "stale_or_ambiguous", "same-size monitor replacement rejected");
        Reject(() => new RecordingBackendFactory(() => Snapshot(Target(intel, 2, width: 1280))).Plan(Startup(ti)), "stale_or_ambiguous", "mode resize invalidates plan");
        Reject(() => new RecordingBackendFactory(() => Snapshot(ti, ti)).Plan(Startup(ti)), "stale_or_ambiguous", "duplicate output identity fails closed");
        Reject(() => factory.Plan(Startup()), "more than one", "auto default refuses two primary candidates");
        var amd = Target(Id(77, GpuVendor.Amd));
        Reject(() => new RecordingBackendFactory(() => Snapshot(amd)).Plan(Startup(amd)), "vendor_mismatch", "unregistered vendor cannot select another provider");
        Check(new RecordingBackendFactory(() => Snapshot(tn, ti)).Plan(new EngineStartupConfig()).Target.SameOutput(tn), "legacy NVENC default follows provider adapter");
        var adapter2 = Id(23, GpuVendor.Nvidia);
        var t2 = Target(adapter2, 3);
        var dual = new RecordingBackendFactory(() => Snapshot(tn, t2));
        Check(dual.Plan(Startup(t2, "NVENC_H264")).Target.Adapter.Low == 23, "explicit second NVIDIA LUID retained");
        Reject(() => dual.CreateCapture(dual.Plan(Startup(t2, "NVENC_H264")), false, Logger()),
            "explicit_adapter_selection_not_implemented", "second NVENC GPU rejected before capture because existing encoder selects first adapter");
        var headless = new DxgiDisplayInventory.Snapshot(new() { new(nvidia, 0, "headless"), new(adapter2, 1, "attached") }, new() { t2 });
        Reject(() => new RecordingBackendFactory(() => headless).Plan(new EngineStartupConfig()), "display_unavailable", "legacy default does not skip first headless provider adapter");
        Check(ReferenceEquals(CaptureTarget.RequireCurrent(ti, new[] { ti }), ti), "fresh target round trip");
        var reordered = new DxgiDisplayInventory.Snapshot(new() { new(nvidia, 0, "NV"), new(intel, 3, "Intel") }, new() { ti, tn });
        Check(DxgiDisplayInventory.ResolveAdapterIndex(ti, reordered) == 3, "child adapter index resolves from LUID after enumeration reorder");
        Check(DxgiDisplayInventory.CaptureDeviceArguments(ti, reordered).Contains("d3d11va=capture:3"), "FFmpeg capture binding uses resolved adapter rather than output index");
        Reject(() => DxgiDisplayInventory.CaptureDeviceArguments(ti, Snapshot(tn)), "stale", "stale child capture binding refused");
        Reject(() => DxgiDisplayInventory.ResolveAdapterIndex(ti, new(new() { new(intel, -1, "bad") }, new() { ti })),
            "ambiguous", "invalid child adapter index cannot become a default-device fallback");
        RecordingBackendFactory.RequireIdentity(p, ti, intel);
        Check(true, "matching planned capture and actual encoder accepted");
        Reject(() => RecordingBackendFactory.RequireIdentity(p, tn, intel), "capture_target_runtime_mismatch", "actual capture must equal planned display");
        Reject(() => RecordingBackendFactory.RequireIdentity(p, ti, nvidia), "adapter_mismatch", "actual encoder must match capture LUID");
        Reject(() => RecordingBackendFactory.RequireIdentity(p, ti, parsec), "adapter_mismatch", "actual encoder virtual identity rejected");
    }

    static void Orchestration()
    {
        var target = Target(Id(22, GpuVendor.Nvidia));
        var fake = new FakeFactory(target);
        using (var engine = new RecordingEngine(Logger(), fake))
        {
            var startup = Startup(target, "NVENC_H264");
            engine.Initialize(startup);
            Check(fake.Capture!.Initialized && fake.Encoders[0].Initialized, "engine uses injected capture and encoder factories");
            Check(engine.GetStatus().State == RecordingEngineState.Idle, "matching factory initialization reaches Idle");
            Check(engine.StartupEcho.CaptureTarget.SameOutput(target), "startup echo retains complete target");
            Check(engine.CaptureGeometry.StartsWith("1920x1080"), "generic capture geometry preserved");
            Check(engine.GetStatus().VideoIdentity.CaptureLuidLow == target.Adapter.Low &&
                engine.GetStatus().VideoIdentity.EncoderVendorId == (uint)GpuVendor.Nvidia, "status carries actual capture and encoder identities");
            fake.RebuildAdapter = Id(999, GpuVendor.Nvidia);
            var result = engine.StartSession(new SessionConfig { OutputPath = Path.Combine(Path.GetTempPath(), "I1-never-created.mp4"),
                AudioEnabled = false, MicEnabled = false, TargetFps = 60, DurationSeconds = 1, DiskHeadroomOverrideBytes = 1 });
            Check(!result.Pass && result.ErrorMessage.Contains("adapter_mismatch"), "rebuild mismatched actual adapter fails before session creation");
            Check(result.VideoIdentity.EncoderLuidLow == target.Adapter.Low, "failed rebuild result identifies retained encoder rather than rejected GPU");
            Check(fake.Encoders.Count == 2 && fake.Encoders[1].Disposed, "rejected rebuilt encoder disposed");
            Check(!fake.Encoders[0].Disposed, "previous valid encoder retained after rejected rebuild");
            Check(!fake.Capture.Started, "rejected rebuild never starts capture");
            Check(engine.GetStatus().State == RecordingEngineState.Idle && engine.GetStatus().CurrentSessionId is null, "failed rebuild leaves no phantom session");
            fake.Stale = true;
            result = engine.StartSession(new SessionConfig { OutputPath = Path.Combine(Path.GetTempPath(), "I1-never-created.mp4"),
                AudioEnabled = false, MicEnabled = false, TargetFps = 30, DurationSeconds = 1, DiskHeadroomOverrideBytes = 1 });
            Check(!result.Pass && result.ErrorMessage.Contains("stale"), "session revalidates topology before frame handoff");
        }
        Check(fake.Capture!.Disposed && fake.Encoders[0].Disposed, "engine disposes injected persistent backends");
        var mismatch = new FakeFactory(target) { InitialAdapter = Id(404, GpuVendor.Nvidia) };
        using (var engine = new RecordingEngine(Logger(), mismatch))
        {
            Reject(() => engine.Initialize(Startup(target, "NVENC_H264")), "adapter_mismatch", "initial runtime identity mismatch rejected");
            Check(engine.GetStatus().State == RecordingEngineState.Faulted, "initial mismatch faults engine");
            Check(mismatch.Capture!.Disposed && mismatch.Encoders[0].Disposed, "partial initialization resources cleaned up");
        }
        var intel = Target(Id(10, GpuVendor.Intel));
        var realFactory = new RecordingBackendFactory(() => Snapshot(intel));
        using (var engine = new RecordingEngine(Logger(), realFactory))
        {
            Reject(() => engine.Initialize(Startup(intel)), "intel_native_live_input_not_verified", "production engine rejects planned Intel before hardware creation");
            Check(engine.GetStatus().State == RecordingEngineState.Faulted, "unimplemented Intel engine never reports Idle");
        }
    }
    static int Main()
    {
        IdentityAndPolicy();
        Orchestration();
        Console.WriteLine($"I1_COMPOSITION_OFFLINE_PASS passed={passed} failed=0 skipped=0 gpu_initialized=false capture_started=false audio_opened=false");
        return 0;
    }

    sealed class FakeFactory(CaptureTarget target) : IRecordingBackendFactory
    {
        public FakeCapture? Capture;
        public List<FakeEncoder> Encoders = new();
        public AdapterIdentity? InitialAdapter, RebuildAdapter;
        public bool Stale;
        public CaptureCompositionPlan Plan(EngineStartupConfig config) => new(target, RecordingBackendFactory.Providers[0]);
        public void ValidateTarget(CaptureCompositionPlan plan)
        { if (Stale) throw new InvalidOperationException("capture_target_stale_or_ambiguous"); }
        public IVideoCaptureRuntime CreateCapture(CaptureCompositionPlan plan, bool cursor, EngineLogger logger) => Capture = new(target);
        public IEncoderRuntimeBackend CreateEncoder(CaptureCompositionPlan plan, EngineLogger logger)
        {
            var encoder = new FakeEncoder((Encoders.Count == 0 ? InitialAdapter : RebuildAdapter) ?? target.Adapter);
            Encoders.Add(encoder); return encoder;
        }
    }
    sealed class FakeCapture(CaptureTarget target) : IVideoCaptureRuntime, IVideoBackendDiagnostics
    {
        public bool Initialized, Started, Disposed;
        public CaptureTarget ActualTarget => target;
        public int OutputWidth => target.Width;
        public int OutputHeight => target.Height;
        public int OutputRefreshRate => 60;
        public IVideoBackendDiagnostics Diagnostics => this;
        public long EmittedFrames => 0; public long DroppedFrames => 0; public long ReplacedFrames => 0;
        public long NoFrameCount => 0; public long ErrorCount => 0; public long AccessLostCount => 0;
        public long TexturesCreated => 0; public long TexturesDisposed => 0; public long FramesPushed => 0;
        public void Initialize(IVideoBackendContext context) => Initialized = true;
        public void Start(IVideoFrameSink sink) { Started = true; throw new Exception("offline test must not start capture"); }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
    sealed class FakeEncoder(AdapterIdentity adapter) : IEncoderRuntimeBackend
    {
        readonly DulukaEngine.Encoder.Backends.Fake.FakeEncoderBackend backend = new(Logger());
        EncoderConfig? config;
        public bool Initialized, Disposed;
        public AdapterIdentity ActualAdapter => adapter;
        public int FrameRateFps => config!.FrameRateFps;
        public long BitrateBpsOutput => config!.BitrateBps;
        public int EncodeWidthOutput => config!.EncodeWidth;
        public int EncodeHeightOutput => config!.EncodeHeight;
        public IEncoderDiagnostics Diagnostics => backend.Diagnostics;
        public EncoderState CurrentState => backend.CurrentState;
        public void Initialize(EncoderConfig cfg) { config = cfg; Initialized = true; }
        public void Start() => throw new Exception("offline test must not encode");
        public bool Encode(IVideoFrame frame, ref EncodedPacket packet) => throw new Exception("offline test must not encode");
        public int Flush(Action<EncodedPacket> sink) => 0;
        public void Stop() { }
        public void Dispose() { Disposed = true; backend.Dispose(); }
    }
}
