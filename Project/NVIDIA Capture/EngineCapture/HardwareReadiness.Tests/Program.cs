using System;
using System.IO;
using DulukaEngine.Recording;
using DulukaEngine.Diagnostics;

// Phase 7A: checks the real recording configuration and disk preflight.
// It NEVER constructs RecordingEngine, calls Initialize/StartSession,
// initializes D3D11/NVENC, opens WASAPI, or creates an MP4/audio file.
internal static class Program
{
    static int passes;
    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("P7A FAIL " + what);
        passes++;
        Console.WriteLine("P7A PASS " + what);
    }

    static SessionConfig SafeConfig(string output) => new()
    {
        OutputPath = output,
        DurationSeconds = 15,
        AudioSidecarMode = true,
        AudioEnabled = false,
        MicEnabled = false,
        MicSeparateTracks = false,
        DiskHeadroomOverrideBytes = 0
    };

    static int Main()
    {
        string output = Path.Combine(Path.GetTempPath(),
            "Phase7A-DoesNotCreate-" + Guid.NewGuid().ToString("N") + ".mp4");
        var logger = new EngineLogger("Phase7A-HardwareReadiness",
            EngineLogger.LogLevel.Error, _ => { });

        var videoOnly = SafeConfig(output);
        Check(!videoOnly.AudioEnabled && !videoOnly.MicEnabled,
            "readiness sample disables both real audio sources");
        Check(!videoOnly.RequestsAnyAudio,
            "production audio intent resolves to false for video-only");
        Check(!File.Exists(output),
            "never created user video output before disk evaluation");
        var muted = DiskHeadroom.Evaluate(videoOnly, 20_000_000L, logger);
        Check(muted.Ok, "production disk headroom accepts bounded 15s dry-run");
        Check(muted.RequiredBytes == 75_000_000L,
            "production 20 Mbps x 15s two-copy peak equals 75,000,000 bytes");
        Check(muted.FreeBytes >= muted.RequiredBytes,
            "actual test target drive reports enough disk space");
        Check(muted.DriveRoot == Path.GetPathRoot(Path.GetFullPath(output)),
            "space measured on exact target volume");

        var system = SafeConfig(output);
        system.AudioEnabled = true;
        var withSystem = DiskHeadroom.Evaluate(system, 20_000_000L, logger);
        Check(system.RequestsAnyAudio,
            "system-audio intent correctly indicates audio requested");
        Check(withSystem.RequiredBytes - muted.RequiredBytes == 2_880_000L,
            "sidecar system audio properly raises disk estimate");

        var mic = SafeConfig(output);
        mic.MicEnabled = true;
        var withMic = DiskHeadroom.Evaluate(mic, 20_000_000L, logger);
        Check(mic.RequestsAnyAudio,
            "mic-only intent correctly indicates audio requested");
        Check(withMic.RequiredBytes == withSystem.RequiredBytes,
            "mic-only session receives independent WAV sidecar allowance");

        var both = SafeConfig(output);
        both.AudioEnabled = true;
        both.MicEnabled = true;
        var withBoth = DiskHeadroom.Evaluate(both, 20_000_000L, logger);
        Check(withBoth.RequiredBytes == muted.RequiredBytes + 5_760_000L,
            "two audio sources count both sidecars in required bytes");

        var legacyMix = SafeConfig(output);
        legacyMix.AudioSidecarMode = false;
        legacyMix.AudioEnabled = true;
        legacyMix.MicEnabled = true;
        legacyMix.MicSeparateTracks = false;
        var mixed = DiskHeadroom.Evaluate(legacyMix, 20_000_000L, logger);
        Check(mixed.RequiredBytes == muted.RequiredBytes + 1_200_000L,
            "legacy mixed audio counts one AAC stream");
        legacyMix.MicSeparateTracks = true;
        var separate = DiskHeadroom.Evaluate(legacyMix, 20_000_000L, logger);
        Check(separate.RequiredBytes == muted.RequiredBytes + 2_400_000L,
            "legacy separate tracks count two AAC streams");

        var forcedFailure = SafeConfig(output);
        forcedFailure.DiskHeadroomOverrideBytes = long.MaxValue;
        var denied = DiskHeadroom.Evaluate(forcedFailure, 20_000_000L, logger);
        Check(!denied.Ok && denied.FailureReason.Contains(
            "insufficient disk headroom", StringComparison.OrdinalIgnoreCase),
            "simulated insufficient disk fails BEFORE creating files");
        Check(denied.FreeBytes >= 0 &&
              denied.RequiredBytes == long.MaxValue,
            "no disk filling needed to exercise insufficient-space guard");
        Check(!File.Exists(output), "dry-run did not create any MP4");
        Console.WriteLine($"HARDWARE_PREFLIGHT_DRYRUN_PASS checks={passes} " +
            $"disk_required={muted.RequiredBytes} disk_free={muted.FreeBytes} " +
            "gpu_opened=false recording_started=false audio_opened=false");
        return 0;
    }
}
