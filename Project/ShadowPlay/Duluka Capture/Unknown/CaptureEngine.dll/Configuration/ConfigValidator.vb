Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.Configuration
    ''' <summary>
    ''' Validates an EngineConfigV2 instance. Returns a list of human-readable
    ''' validation errors. Empty list = valid.
    '''
    ''' Validation rules cover:
    '''   - Required fields (encoder key, codec, container)
    '''   - Numeric ranges (FPS, bitrate, resolution, sample rate)
    '''   - Enumerated string values (capture method, rate control, fps_mode,
    '''     pixel format, audio codec, track mode, log level, process priority)
    '''   - Cross-field invariants (bufsize ≥ bitrate, minrate ≤ maxrate,
    '''     GOP > 0, custom resolution must have positive W×H)
    ''' </summary>
    Public NotInheritable Class ConfigValidator
        Private Sub New()
            ' Static helper class — no instances.
        End Sub

        Public Shared Function Validate(cfg As EngineConfigV2) As IReadOnlyList(Of String)
            Dim errors As New List(Of String)
            If cfg Is Nothing Then
                errors.Add("Configuration is Nothing.")
                Return errors
            End If

            ValidateVideo(cfg, errors)
            ValidateAudio(cfg, errors)
            ValidateOutput(cfg, errors)
            ValidateRuntime(cfg, errors)

            Return errors
        End Function

        ''' <summary>Convenience: returns True if no validation errors.</summary>
        Public Shared Function IsValid(cfg As EngineConfigV2) As Boolean
            Return Validate(cfg).Count = 0
        End Function

        ' ──────────────────────────────────────────────────────────
        ' Video
        ' ──────────────────────────────────────────────────────────

        Private Shared Sub ValidateVideo(cfg As EngineConfigV2, errors As List(Of String))
            Dim v As EngineConfigV2.VideoSection = cfg.Video

            ' Capture
            Dim validMethods As String() = {"ddagrab", "gdigrab", "gfxcapture"}
            If Array.IndexOf(validMethods, v.Capture.Method.ToLowerInvariant()) < 0 Then
                errors.Add($"Video.Capture.Method '{v.Capture.Method}' is not one of: {String.Join(", ", validMethods)}.")
            End If
            If v.Capture.OutputIndex < 0 Then
                errors.Add($"Video.Capture.OutputIndex {v.Capture.OutputIndex} must be >= 0.")
            End If
            If v.Capture.Framerate < 1 OrElse v.Capture.Framerate > 240 Then
                errors.Add($"Video.Capture.Framerate {v.Capture.Framerate} must be 1-240.")
            End If

            ' Resolution
            If v.Resolution.Mode <> "native" AndAlso v.Resolution.Mode <> "custom" Then
                errors.Add($"Video.Resolution.Mode '{v.Resolution.Mode}' must be 'native' or 'custom'.")
            End If
            If v.Resolution.Mode = "custom" Then
                If v.Resolution.Width <= 0 Then
                    errors.Add($"Video.Resolution.Width {v.Resolution.Width} must be > 0 when Mode=custom.")
                End If
                If v.Resolution.Height <= 0 Then
                    errors.Add($"Video.Resolution.Height {v.Resolution.Height} must be > 0 when Mode=custom.")
                End If
                If v.Resolution.Width Mod 2 <> 0 OrElse v.Resolution.Height Mod 2 <> 0 Then
                    errors.Add($"Video.Resolution {v.Resolution.Width}x{v.Resolution.Height} — both dimensions must be even (H.264 macroblock requirement).")
                End If
            End If

            ' Encoder
            If String.IsNullOrWhiteSpace(v.Encoder.Key) Then
                errors.Add("Video.Encoder.Key is missing (must be e.g. 'NVENC_H264').")
            End If
            If String.IsNullOrWhiteSpace(v.Encoder.FFmpegCodec) Then
                errors.Add("Video.Encoder.FFmpegCodec is missing (must be e.g. 'h264_nvenc').")
            End If

            ' ── Encoder.Key ↔ FFmpegCodec consistency ──
            ' STABILIZATION FIX (Phase 1.A): previously both fields were stored
            ' independently with no consistency check. A user could set
            ' Key="NVENC_HEVC" + FFmpegCodec="h264_nvenc" and validation
            ' would pass, producing a config that lies about its encoder.
            '
            ' Now we enforce that FFmpegCodec must equal the canonical mapping
            ' of Key via ConfigMigrator.MapEncoderKeyToFfmpeg(Key). The two
            ' fields are still both stored (FFmpegCodec is the authoritative
            ' value used by builders; Key is the symbolic UI label).
            If Not String.IsNullOrWhiteSpace(v.Encoder.Key) AndAlso
               Not String.IsNullOrWhiteSpace(v.Encoder.FFmpegCodec) Then
                Dim expectedCodec As String = ConfigMigrator.MapEncoderKeyToFfmpeg(v.Encoder.Key)
                If Not String.Equals(expectedCodec, v.Encoder.FFmpegCodec, StringComparison.OrdinalIgnoreCase) Then
                    errors.Add($"Video.Encoder.FFmpegCodec '{v.Encoder.FFmpegCodec}' does not match canonical " &
                               $"mapping for Key '{v.Encoder.Key}' (expected '{expectedCodec}'). " &
                               "Either set Key to match FFmpegCodec, or set FFmpegCodec to match the canonical mapping.")
                End If
            End If

            ' Preset — NVENC accepts p1..p7, libx264/libx265 accept named presets,
            ' QSV accepts veryfast..veryslow. We do NOT enforce per-codec preset lists
            ' here (FFmpeg will reject invalid presets at runtime with a clear error).
            If String.IsNullOrWhiteSpace(v.Encoder.Preset) Then
                errors.Add("Video.Encoder.Preset is missing.")
            End If

            ' Rate control
            Dim validRc As String() = {"cbr", "vbr", "cq"}
            If Array.IndexOf(validRc, v.Encoder.RateControl.ToLowerInvariant()) < 0 Then
                errors.Add($"Video.Encoder.RateControl '{v.Encoder.RateControl}' must be one of: {String.Join(", ", validRc)}.")
            End If

            ' Bitrate / minrate / maxrate / bufsize
            If v.Encoder.BitrateBps < 1_000_000L Then
                errors.Add($"Video.Encoder.BitrateBps {v.Encoder.BitrateBps} must be >= 1,000,000 (1 Mbps).")
            End If
            If v.Encoder.BitrateBps > 200_000_000L Then
                errors.Add($"Video.Encoder.BitrateBps {v.Encoder.BitrateBps} must be <= 200,000,000 (200 Mbps).")
            End If
            If v.Encoder.MinrateBps < 0L Then
                errors.Add($"Video.Encoder.MinrateBps {v.Encoder.MinrateBps} must be >= 0.")
            End If
            If v.Encoder.MaxrateBps < 0L Then
                errors.Add($"Video.Encoder.MaxrateBps {v.Encoder.MaxrateBps} must be >= 0.")
            End If
            If v.Encoder.MinrateBps > v.Encoder.MaxrateBps Then
                errors.Add($"Video.Encoder.MinrateBps ({v.Encoder.MinrateBps}) > MaxrateBps ({v.Encoder.MaxrateBps}).")
            End If
            If v.Encoder.BufsizeBps < v.Encoder.BitrateBps Then
                errors.Add($"Video.Encoder.BufsizeBps {v.Encoder.BufsizeBps} must be >= BitrateBps {v.Encoder.BitrateBps}.")
            End If

            ' ── CBR invariant (Phase 1.B) ──
            ' For RateControl=cbr, FFmpeg requires strict CBR:
            '   minrate = maxrate = bitrate
            ' If minrate/maxrate are set but differ from bitrate, FFmpeg's behavior
            ' is undefined (may produce VBR with cbr flag, may error out).
            ' Reject this configuration deterministically.
            '
            ' Allowed:
            '   - BitrateBps > 0
            '   - MinrateBps = BitrateBps (strict CBR)
            '   - MaxrateBps = BitrateBps (strict CBR)
            '   - BufsizeBps >= BitrateBps (checked above)
            If v.Encoder.RateControl.Equals("cbr", StringComparison.OrdinalIgnoreCase) Then
                If v.Encoder.BitrateBps <= 0 Then
                    errors.Add($"Video.Encoder.BitrateBps {v.Encoder.BitrateBps} must be > 0 when RateControl=cbr.")
                End If
                If v.Encoder.MinrateBps <> v.Encoder.BitrateBps Then
                    errors.Add($"Video.Encoder.MinrateBps ({v.Encoder.MinrateBps}) must equal BitrateBps ({v.Encoder.BitrateBps}) when RateControl=cbr (strict CBR). " &
                               "Use RateControl=vbr if you need asymmetric min/max rate.")
                End If
                If v.Encoder.MaxrateBps <> v.Encoder.BitrateBps Then
                    errors.Add($"Video.Encoder.MaxrateBps ({v.Encoder.MaxrateBps}) must equal BitrateBps ({v.Encoder.BitrateBps}) when RateControl=cbr (strict CBR). " &
                               "Use RateControl=vbr if you need asymmetric min/max rate.")
                End If
            End If

            ' ── VBR sanity check (Phase 1.B) ──
            ' For RateControl=vbr, FFmpeg accepts:
            '   minrate <= bitrate <= maxrate
            ' If minrate > bitrate OR bitrate > maxrate, that's VBR with absurd
            ' constraints — reject it.
            If v.Encoder.RateControl.Equals("vbr", StringComparison.OrdinalIgnoreCase) Then
                If v.Encoder.MinrateBps > v.Encoder.BitrateBps Then
                    errors.Add($"Video.Encoder.MinrateBps ({v.Encoder.MinrateBps}) > BitrateBps ({v.Encoder.BitrateBps}) when RateControl=vbr — minrate must be <= bitrate.")
                End If
                If v.Encoder.MaxrateBps < v.Encoder.BitrateBps Then
                    errors.Add($"Video.Encoder.MaxrateBps ({v.Encoder.MaxrateBps}) < BitrateBps ({v.Encoder.BitrateBps}) when RateControl=vbr — maxrate must be >= bitrate.")
                End If
            End If

            ' GOP
            If v.Encoder.GopSize < 1 Then
                errors.Add($"Video.Encoder.GopSize {v.Encoder.GopSize} must be >= 1.")
            End If
            If v.Encoder.GopSize > 1000 Then
                errors.Add($"Video.Encoder.GopSize {v.Encoder.GopSize} must be <= 1000 (suspiciously large GOP).")
            End If

            ' fps_mode
            Dim validFpsMode As String() = {"cfr", "vfr", "cfr_with_drop", "passthrough"}
            If Array.IndexOf(validFpsMode, v.Encoder.FpsMode.ToLowerInvariant()) < 0 Then
                errors.Add($"Video.Encoder.FpsMode '{v.Encoder.FpsMode}' must be one of: {String.Join(", ", validFpsMode)}.")
            End If

            ' AQ
            If v.Encoder.SpatialAQ <> 0 AndAlso v.Encoder.SpatialAQ <> 1 Then
                errors.Add($"Video.Encoder.SpatialAQ {v.Encoder.SpatialAQ} must be 0 or 1.")
            End If
            If v.Encoder.TemporalAQ <> 0 AndAlso v.Encoder.TemporalAQ <> 1 Then
                errors.Add($"Video.Encoder.TemporalAQ {v.Encoder.TemporalAQ} must be 0 or 1.")
            End If

            ' Look-ahead
            If v.Encoder.LookAhead < 0 OrElse v.Encoder.LookAhead > 32 Then
                errors.Add($"Video.Encoder.LookAhead {v.Encoder.LookAhead} must be 0-32.")
            End If

            ' CQ
            If v.Encoder.RateControl = "cq" Then
                If v.Encoder.Cq < 1 OrElse v.Encoder.Cq > 51 Then
                    errors.Add($"Video.Encoder.Cq {v.Encoder.Cq} must be 1-51 when RateControl=cq.")
                End If
            ElseIf v.Encoder.Cq <> 0 Then
                errors.Add($"Video.Encoder.Cq {v.Encoder.Cq} must be 0 when RateControl={v.Encoder.RateControl}.")
            End If

            ' Pixel format
            Dim validPixFmt As String() = {"nv12", "yuv420p", "yuv444p", "p010le", "yuv420p10le"}
            If Array.IndexOf(validPixFmt, v.Encoder.PixelFormat.ToLowerInvariant()) < 0 Then
                errors.Add($"Video.Encoder.PixelFormat '{v.Encoder.PixelFormat}' must be one of: {String.Join(", ", validPixFmt)}.")
            End If
        End Sub

        ' ──────────────────────────────────────────────────────────
        ' Audio
        ' ──────────────────────────────────────────────────────────

        Private Shared Sub ValidateAudio(cfg As EngineConfigV2, errors As List(Of String))
            Dim a As EngineConfigV2.AudioSection = cfg.Audio

            If a.System.Volume < 0.0F OrElse a.System.Volume > 2.0F Then
                errors.Add($"Audio.System.Volume {a.System.Volume} must be 0.0-2.0.")
            End If
            If a.Microphone.Volume < 0.0F OrElse a.Microphone.Volume > 2.0F Then
                errors.Add($"Audio.Microphone.Volume {a.Microphone.Volume} must be 0.0-2.0.")
            End If

            ' Encoding
            Dim validCodec As String() = {"aac", "opus", "pcm_s16le"}
            If Array.IndexOf(validCodec, a.Encoding.Codec.ToLowerInvariant()) < 0 Then
                errors.Add($"Audio.Encoding.Codec '{a.Encoding.Codec}' must be one of: {String.Join(", ", validCodec)}.")
            End If
            If a.Encoding.BitrateBps < 32_000L Then
                errors.Add($"Audio.Encoding.BitrateBps {a.Encoding.BitrateBps} must be >= 32,000.")
            End If
            If a.Encoding.BitrateBps > 1_000_000L Then
                errors.Add($"Audio.Encoding.BitrateBps {a.Encoding.BitrateBps} must be <= 1,000,000 (1 Mbps).")
            End If
            Dim validSr As Integer() = {22050, 32000, 44100, 48000, 96000, 192000}
            If Array.IndexOf(validSr, a.Encoding.SampleRate) < 0 Then
                errors.Add($"Audio.Encoding.SampleRate {a.Encoding.SampleRate} must be one of: {String.Join(", ", validSr)}.")
            End If
            If a.Encoding.Channels < 1 OrElse a.Encoding.Channels > 8 Then
                errors.Add($"Audio.Encoding.Channels {a.Encoding.Channels} must be 1-8.")
            End If
            Dim validTm As String() = {"single", "separate"}
            If Array.IndexOf(validTm, a.Encoding.TrackMode.ToLowerInvariant()) < 0 Then
                errors.Add($"Audio.Encoding.TrackMode '{a.Encoding.TrackMode}' must be one of: {String.Join(", ", validTm)}.")
            End If

            ' Sync
            If a.Sync.MinOffsetSec < -60.0 OrElse a.Sync.MinOffsetSec > 0.0 Then
                errors.Add($"Audio.Sync.MinOffsetSec {a.Sync.MinOffsetSec} must be -60..0.")
            End If
            If a.Sync.MaxOffsetSec < 0.0 OrElse a.Sync.MaxOffsetSec > 60.0 Then
                errors.Add($"Audio.Sync.MaxOffsetSec {a.Sync.MaxOffsetSec} must be 0..60.")
            End If
            If a.Sync.AVSyncToleranceMs < 1 OrElse a.Sync.AVSyncToleranceMs > 5000 Then
                errors.Add($"Audio.Sync.AVSyncToleranceMs {a.Sync.AVSyncToleranceMs} must be 1-5000.")
            End If
        End Sub

        ' ──────────────────────────────────────────────────────────
        ' Output
        ' ──────────────────────────────────────────────────────────

        Private Shared Sub ValidateOutput(cfg As EngineConfigV2, errors As List(Of String))
            Dim o As EngineConfigV2.OutputSection = cfg.Output

            Dim validContainer As String() = {"mp4", "mov", "mkv", "m4v"}
            If Array.IndexOf(validContainer, o.Container.ToLowerInvariant()) < 0 Then
                errors.Add($"Output.Container '{o.Container}' must be one of: {String.Join(", ", validContainer)}.")
            End If

            ' Directory may be empty (resolved at runtime) but if present must be absolute.
            If o.Directory.Length > 0 AndAlso Not Path.IsPathRooted(o.Directory) Then
                errors.Add($"Output.Directory '{o.Directory}' must be an absolute path or empty.")
            End If

            If String.IsNullOrWhiteSpace(o.FilenamePattern) Then
                errors.Add("Output.FilenamePattern is missing.")
            ElseIf Not o.FilenamePattern.Contains("{ext}") AndAlso Not o.FilenamePattern.Contains(".") Then
                errors.Add($"Output.FilenamePattern '{o.FilenamePattern}' must include a file extension or the '{{ext}}' token.")
            End If
        End Sub

        ' ──────────────────────────────────────────────────────────
        ' Runtime
        ' ──────────────────────────────────────────────────────────

        Private Shared Sub ValidateRuntime(cfg As EngineConfigV2, errors As List(Of String))
            Dim r As EngineConfigV2.RuntimeSection = cfg.Runtime

            Dim validPriority As String() = {"Normal", "AboveNormal", "High"}
            If Array.IndexOf(validPriority, r.ProcessPriority) < 0 Then
                errors.Add($"Runtime.ProcessPriority '{r.ProcessPriority}' must be one of: {String.Join(", ", validPriority)}. Realtime is intentionally forbidden.")
            End If

            Dim validLogLevel As String() = {"quiet", "error", "warning", "info", "verbose", "debug"}
            If Array.IndexOf(validLogLevel, r.LogLevel.ToLowerInvariant()) < 0 Then
                errors.Add($"Runtime.LogLevel '{r.LogLevel}' must be one of: {String.Join(", ", validLogLevel)}.")
            End If

            ' Hotkeys dictionary may be empty but cannot contain null values.
            If r.Hotkeys IsNot Nothing Then
                For Each kvp As KeyValuePair(Of String, String) In r.Hotkeys
                    If String.IsNullOrWhiteSpace(kvp.Value) Then
                        errors.Add($"Runtime.Hotkeys['{kvp.Key}'] has an empty value.")
                    End If
                Next
            End If

            ' Shutdown timeouts
            If r.ShutdownTimeoutMs.FFmpegQuit < 1000 Then
                errors.Add($"Runtime.ShutdownTimeoutMs.FFmpegQuit {r.ShutdownTimeoutMs.FFmpegQuit} must be >= 1000.")
            End If
            If r.ShutdownTimeoutMs.MuxWait < 1000 Then
                errors.Add($"Runtime.ShutdownTimeoutMs.MuxWait {r.ShutdownTimeoutMs.MuxWait} must be >= 1000.")
            End If
            If r.ShutdownTimeoutMs.FFprobeWait < 500 Then
                errors.Add($"Runtime.ShutdownTimeoutMs.FFprobeWait {r.ShutdownTimeoutMs.FFprobeWait} must be >= 500.")
            End If
        End Sub
    End Class
End Namespace
