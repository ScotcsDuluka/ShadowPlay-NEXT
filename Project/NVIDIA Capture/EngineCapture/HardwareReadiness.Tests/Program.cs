using System;
using System.IO;
using DulukaEngine.Recording;
using DulukaEngine.Diagnostics;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;
using System.Linq;

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
            var snapshot = DxgiDisplayInventory.Collect();
            DxgiInventory.Write(snapshot, Console.WriteLine);
            Check(snapshot.Adapters.Count > 0, "DXGI metadata exposes adapters");
            var eligible = snapshot.Outputs.Where(t => t.Eligible).ToArray();
            Check(eligible.Length > 0, "physical attached output with CCD identity exists");
            Check(eligible.All(t => t.Adapter.HasLuid && t.Adapter.IsPhysical && !t.Adapter.IsSoftware),
                  "eligible outputs have nonzero physical hardware adapter identity");
            Check(eligible.All(t => snapshot.Adapters.Count(a => a.Identity.SameDevice(t.Adapter)) == 1),
                  "every output maps to one exact DXGI adapter LUID and PCI identity");
            Check(eligible.Select(t => $"{t.Adapter}:{t.OutputIndex}").Distinct().Count() == eligible.Length,
                  "adapter LUID and output index are unique");
            Check(snapshot.Outputs.Where(t => !t.Adapter.IsPhysical || t.OutputTechnology is 15 or 16 or 17)
                    .All(t => !t.Eligible), "software and indirect displays cannot pass eligibility");
            var factory = new RecordingBackendFactory(() => snapshot);
            foreach (var target in eligible)
            {
                var provider = RecordingBackendFactory.Providers.SingleOrDefault(p => p.Vendor == target.Adapter.Vendor);
                if (provider is null)
                {
                    Console.WriteLine($"DXGI_UNSUPPORTED_VENDOR {target.Adapter.VendorId:x4}");
                    continue;
                }
                var plan = factory.Plan(new EngineStartupConfig { CaptureTarget = target,
                    CaptureOutputIndex = target.OutputIndex, CodecKey = provider.CodecKeys[0] });
                Check(plan.Target.SameOutput(target), "factory preserves exact display identity");
                Check(plan.Provider.Vendor == target.Adapter.Vendor, "provider matches actual adapter vendor");
                Console.WriteLine($"COMPOSITION_METADATA provider={plan.Provider.Id} wired={plan.RuntimeWired} " +
                    $"reason={plan.Reason} actual_capture_proven=false actual_encoder_proven=false");
            }
            Console.WriteLine($"DXGI_METADATA_ONLY_PASS checks={passes} total_adapters={snapshot.Adapters.Count} " +
                $"eligible_outputs={eligible.Length} screen_pixels_read=false video_devices_initialized=false");
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
