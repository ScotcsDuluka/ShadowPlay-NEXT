using System;
using System.IO;
using System.Reflection;
using DulukaEngine.Diagnostics;
using DulukaEngine.Recording;

// Phase 6C: Exercise real RecordingEngine state transitions without Initialize().
// Intentionally fault a fake Idle engine before any GPU, audio or FFmpeg object
// can be created: _capture is null, so startup throws after the state flips.
// We never start desktop duplication, WASAPI, NVENC, or the live worker.
internal static class Program
{
    private static int _passes;

    private static void Check(bool actual, string name)
    {
        if (!actual) throw new Exception("FAIL " + name);
        _passes++;
        Console.WriteLine("PASS " + name);
    }

    private static void SetField(object obj, string field, object? val)
    {
        var info = obj.GetType().GetField(field,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("Production field missing: " + field);
        info.SetValue(obj, val);
    }

    private static RecordingEngine NewFaultInjectedEngine()
    {
        var engine = new RecordingEngine(new EngineLogger("recovery-offline",
            EngineLogger.LogLevel.Error, _ => {}));
        SetField(engine, "_state", RecordingEngineState.Idle);
        SetField(engine, "_startupEcho", new EngineStartupConfig());
        return engine;
    }

    private static SessionConfig FakeConfig() => new()
    {
        OutputPath = Path.Combine(Path.GetTempPath(), "capture-phase6c-not-created.mp4"),
        DurationSeconds = 1,
        DiskHeadroomOverrideBytes = 1,
        AudioEnabled = false,
        MicEnabled = false
    };

    private static void RunFaultRecovery()
    {
        using var engine = NewFaultInjectedEngine();
        Check(engine.GetStatus().State == RecordingEngineState.Idle,
              "test engine seeded Idle without GPU initialization");
        var first = engine.StartSession(FakeConfig());
        Check(!first.Pass && !string.IsNullOrEmpty(first.ErrorMessage),
              "startup fault returns failure evidence");
        var status = engine.GetStatus();
        Check(status.State == RecordingEngineState.Idle,
              "startup fault releases Recording state");
        Check(status.CurrentSessionId == null,
              "startup fault leaves no phantom active session");
        Check(ReferenceEquals(status.LastSessionResult, first),
              "failure result published to status API");
        engine.Stop();
        Check(engine.GetStatus().State == RecordingEngineState.Idle,
              "Stop after failed start is harmless");

        var second = engine.StartSession(FakeConfig());
        Check(!second.Pass && !string.IsNullOrEmpty(second.ErrorMessage),
              "subsequent start re-enters and handles failure");
        var status2 = engine.GetStatus();
        Check(status2.State == RecordingEngineState.Idle &&
              status2.CurrentSessionId == null,
              "repeated start failure releases state");
        Check(ReferenceEquals(status2.LastSessionResult, second),
              "subsequent failure replaces last result");

        engine.Dispose();
        Check(engine.GetStatus().State == RecordingEngineState.Disposed,
              "Dispose transitions failed engine to Disposed");
        var rejects = false;
        try { engine.StartSession(FakeConfig()); }
        catch (ObjectDisposedException) { rejects = true; }
        Check(rejects, "disposed engine rejects retry");
    }

    private static void RunPublishedSessionFaultCleanup()
    {
        // Reproduce the state left behind when a session had been published
        // but Run() subsequently threw: the failed-session finalizer must
        // withdraw the active marker even if the run never finished normally.
        using var engine = NewFaultInjectedEngine();
        using var fakeSession = new CaptureSession(null!, null!, FakeConfig(),
            new EngineLogger("recovery-offline", EngineLogger.LogLevel.Error, _ => {}));
        SetField(engine, "_currentSession", fakeSession);
        Check(engine.GetStatus().CurrentSessionId != null,
              "synthetic in-flight session initially visible");
        var failed = engine.StartSession(FakeConfig());
        Check(!failed.Pass && !string.IsNullOrWhiteSpace(failed.ErrorMessage),
              "injected start failure returns error");
        var after = engine.GetStatus();
        Check(after.CurrentSessionId == null && after.State == RecordingEngineState.Idle,
              "failed session marker removed before Idle");
        Check(ReferenceEquals(after.LastSessionResult, failed),
              "latest failure observable after cleanup");
    }

    static int Main()
    {
        RunFaultRecovery();
        RunPublishedSessionFaultCleanup();
        Console.WriteLine($"RECOVERY_NO_HARDWARE_PASS: {_passes} checks; " +
                          "no screen capture, microphone, speakers or worker restart");
        return 0;
    }
}