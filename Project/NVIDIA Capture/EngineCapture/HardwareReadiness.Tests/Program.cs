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

    static int Main(string[] args)
    {
        if (args.Length == 1 &&
            string.Equals(args[0], "--dxgi-only", StringComparison.Ordinal))
        {
            var adapters = DxgiInventory.Collect(Console.WriteLine);
            var nvidia = Array.FindAll(adapters, a => a.VendorId == 0x10DEu);
            Check(nvidia.Length > 0, "DXGI metadata exposes NVIDIA adapter");
            Check(adapters.Length >= nvidia.Length, "enumeration within available adapters");
            // Both Ddagrab and NVENC currently pick the first NVIDIA DXGI
            // adapter, NOT the first one with an attached display.
            var chosen = nvidia[0];
            var luidUsable = chosen.LuidLow != 0 || chosen.LuidHigh != 0;
            Check(luidUsable, "first NVIDIA adapter has an identifiable LUID");
            // DdagrabBackend currently selects the first NVIDIA adapter
            // and the first output attached to desktop. A detached-only
            // adapter cannot be certified, even if it has a valid LUID.
            Check(chosen.AttachedCount > 0,
                  "first NVIDIA adapter owns an attached desktop output");
            Check(chosen.FirstAttachedIndex >= 0,
                  "selected output index is physically present and attached");
            if (!chosen.FirstAttachedIsPrimary)
                Console.WriteLine("DXGI_CAUTION current first-attached selection is NOT Windows primary screen");
            else
                Check(chosen.PrimaryAttachedCount == 1,
                      "selected first attached output is unique primary at origin");
            Console.WriteLine($"DXGI_METADATA_ONLY_PASS total_adapters={adapters.Length} " +
                              $"nvidia={nvidia.Length} " +
                              $"capture_expected_adapter={chosen.Index} " +
                              $"capture_attached_outputs={chosen.AttachedCount} " +
                              $"first_output={chosen.FirstAttachedIndex} " +
                              $"first_output_primary={chosen.FirstAttachedIsPrimary} " +
                              "screen_pixels_read=false video_devices_initialized=false");
            return 0;
        }
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

        // Phase 7B: deterministic multi-GPU adapter identity guard. These
        // values are synthetic LUIDs; the test never creates a D3D11 device.
        static bool IsRejected(uint captureLow, int captureHigh,
                               uint encoderLow, int encoderHigh,
                               string reason)
        {
            try
            {
                RecordingEngine.RequireMatchingAdapterLuid(
                    captureLow, captureHigh, encoderLow, encoderHigh);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains(reason, StringComparison.OrdinalIgnoreCase);
            }
        }
        RecordingEngine.RequireMatchingAdapterLuid(0x12345678u, 0,
                                                   0x12345678u, 0);
        Check(true, "same valid adapter LUID passes");
        RecordingEngine.RequireMatchingAdapterLuid(0x11111111u, -7,
                                                   0x11111111u, -7);
        Check(true, "signed high-part of same LUID passes");
        Check(IsRejected(0x11111111u, 7, 0x22222222u, 7, "mismatch"),
              "different low-part refuses cross-GPU handoff");
        Check(IsRejected(0x11111111u, 7, 0x11111111u, 8, "mismatch"),
              "different high-part refuses cross-GPU handoff");
        Check(IsRejected(0u, 0, 0x11111111u, 7, "unavailable"),
              "unavailable capture LUID cannot be mistaken for adapter match");
        Check(IsRejected(0x11111111u, 7, 0u, 0, "unavailable"),
              "unavailable encoder LUID cannot be mistaken for adapter match");
        Check(IsRejected(0u, 0, 0u, 0, "unavailable"),
              "both missing LUIDs do not count as the same GPU");
        Console.WriteLine($"HARDWARE_PREFLIGHT_DRYRUN_PASS checks={passes} " +
            $"disk_required={muted.RequiredBytes} disk_free={muted.FreeBytes} " +
            "gpu_opened=false recording_started=false audio_opened=false");
        return 0;
    }
}
