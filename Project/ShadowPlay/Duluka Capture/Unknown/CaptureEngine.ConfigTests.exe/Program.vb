Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports CaptureEngine.Configuration
Imports CaptureEngine.Configuration.Schema
Imports CaptureEngine.ConfigTests.Regression

Namespace CaptureEngine.ConfigTests
    ''' <summary>
    ''' Phase 1 test runner for EngineConfig v2.
    '''
    ''' Covers:
    '''   - Test 1: V1 → V2 migration (bitrate conversion, encoder mapping,
    '''             audio mapping, paths)
    '''   - Test 2: V2 save → V2 load round-trip (no data loss)
    '''   - Test 3: Invalid config rejection (missing encoder, negative bitrate,
    '''             invalid codec, etc.)
    '''
    ''' Pattern follows CaptureEngine.Tests/Program.vb (custom console runner,
    ''' no xUnit/NUnit/MSTest).
    ''' Exit code: 0 = all passed, 1 = at least one failure.
    ''' </summary>
    Friend Module Program
        Private _passed As Integer = 0
        Private _failed As Integer = 0
        Private ReadOnly _failures As New List(Of String)()

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine EngineConfig v2 Tests (Phase 1)")
            Console.WriteLine(" Branch: Engine-Rebuild")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            RunTest("T1.1 Migration: bitrate conversion (kbps UI → bps V2)", AddressOf Test_Migration_BitrateConversion)
            RunTest("T1.2 Migration: encoder key → FFmpeg codec mapping", AddressOf Test_Migration_EncoderMapping)
            RunTest("T1.3 Migration: audio system + mic mapping", AddressOf Test_Migration_AudioMapping)
            RunTest("T1.4 Migration: FFmpegPath + OutputDirectory preserved", AddressOf Test_Migration_Paths)
            RunTest("T1.5 Migration: NvencPreset integer → Preset string", AddressOf Test_Migration_PresetMapping)
            RunTest("T1.6 Migration: bufsize = bitrate * 2 (FIX legacy bug)", AddressOf Test_Migration_BufsizeFix)
            RunTest("T1.7 Migration: GOP = FPS (1-second GOP)", AddressOf Test_Migration_GopFromFps)
            RunTest("T1.8 Migration: NVENC legacy hardcoded values preserved", AddressOf Test_Migration_LegacyDefaults)

            RunTest("T2.1 Round-trip: save → load → identical fields", AddressOf Test_RoundTrip_NoDataLoss)
            RunTest("T2.2 Round-trip: serialize → deserialize → identical", AddressOf Test_RoundTrip_JsonRoundTrip)
            RunTest("T2.3 Round-trip: defaults populated on new instance", AddressOf Test_RoundTrip_Defaults)

            RunTest("T3.1 Invalid: missing encoder key → reject", AddressOf Test_Invalid_MissingEncoderKey)
            RunTest("T3.2 Invalid: missing FFmpeg codec → reject", AddressOf Test_Invalid_MissingFFmpegCodec)
            RunTest("T3.3 Invalid: negative bitrate → reject", AddressOf Test_Invalid_NegativeBitrate)
            RunTest("T3.4 Invalid: bitrate above 200 Mbps → reject", AddressOf Test_Invalid_BitrateTooHigh)
            RunTest("T3.5 Invalid: invalid capture method → reject", AddressOf Test_Invalid_CaptureMethod)
            RunTest("T3.6 Invalid: invalid rate control → reject", AddressOf Test_Invalid_RateControl)
            RunTest("T3.7 Invalid: invalid fps_mode → reject", AddressOf Test_Invalid_FpsMode)
            RunTest("T3.8 Invalid: invalid audio codec → reject", AddressOf Test_Invalid_AudioCodec)
            RunTest("T3.9 Invalid: invalid pixel format → reject", AddressOf Test_Invalid_PixelFormat)
            RunTest("T3.10 Invalid: bufsize < bitrate → reject", AddressOf Test_Invalid_BufsizeBelowBitrate)
            RunTest("T3.11 Invalid: minrate > maxrate → reject", AddressOf Test_Invalid_MinrateExceedsMaxrate)
            RunTest("T3.12 Invalid: custom resolution without W/H → reject", AddressOf Test_Invalid_CustomResMissingDims)
            RunTest("T3.13 Invalid: odd resolution (H.264 macroblock) → reject", AddressOf Test_Invalid_OddResolution)
            RunTest("T3.14 Invalid: CQ without valid range → reject", AddressOf Test_Invalid_CqOutOfRange)
            RunTest("T3.15 Invalid: Realtime priority forbidden → reject", AddressOf Test_Invalid_RealtimePriority)
            RunTest("T3.16 Invalid: nothing config → reject", AddressOf Test_Invalid_NothingConfig)
            RunTest("T3.17 Valid: default config passes validation", AddressOf Test_Valid_DefaultConfig)

            Console.WriteLine()
            Console.WriteLine("==================================================")
            Console.WriteLine(" Phase 6 — Regression Tests (lifecycle + FFmpeg snapshot)")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            RegressionTests.Reset()
            RegressionTests.RunAll()
            Dim p6Passed As Integer = RegressionTests.Passed
            Dim p6Failed As Integer = RegressionTests.Failed
            _passed += p6Passed
            _failed += p6Failed
            For Each f As String In RegressionTests.Failures
                _failures.Add(f)
            Next

            Console.WriteLine()
            Console.WriteLine("==================================================")
            Console.WriteLine(" Phase 5 — Stabilization Hardening Tests")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            RegressionTests.Reset()
            RegressionTests.RunPhase5Hardening()
            _passed += RegressionTests.Passed
            _failed += RegressionTests.Failed
            For Each f As String In RegressionTests.Failures
                _failures.Add(f)
            Next

            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine(" Result: " & _passed & " passed, " & _failed & " failed, " & (_passed + _failed) & " total")
            Console.WriteLine("--------------------------------------------------")
            If _failed > 0 Then
                Console.WriteLine()
                Console.WriteLine("Failures:")
                For Each f As String In _failures
                    Console.WriteLine("  - " & f)
                Next
            End If
            Return If(_failed > 0, 1, 0)
        End Function

        Private Sub RunTest(name As String, test As Action)
            Console.Write("[" & name & "] ")
            Dim pad As Integer = Math.Max(0, 65 - name.Length - 2)
            Console.Write(New String(" "c, pad))
            Try
                test()
                _passed += 1
                Console.WriteLine("PASS")
            Catch ex As Exception
                _failed += 1
                _failures.Add(name & ": " & ex.GetType().Name & ": " & ex.Message)
                Console.WriteLine("FAIL")
                Console.WriteLine("        " & ex.GetType().Name & ": " & ex.Message)
            End Try
        End Sub

        Private Sub Assert(condition As Boolean, message As String)
            If Not condition Then
                Throw New InvalidOperationException("Assertion failed: " & message)
            End If
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' TEST 1: V1 → V2 Migration
        ' ════════════════════════════════════════════════════════════

        Private Sub Test_Migration_BitrateConversion()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.BitrateBps = 17000000L ' = 17 Mbps (Engine's bps form)

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Video.Encoder.BitrateBps = 17000000L, "BitrateBps should be 17000000, got " & v2.Video.Encoder.BitrateBps)
            Assert(v2.Video.Encoder.MinrateBps = 17000000L, "MinrateBps should = BitrateBps for CBR")
            Assert(v2.Video.Encoder.MaxrateBps = 17000000L, "MaxrateBps should = BitrateBps for CBR")
        End Sub

        Private Sub Test_Migration_EncoderMapping()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.EncoderKey = "NVENC_HEVC"
            ' FFmpegCodec left empty — migrator must map via MapEncoderKeyToFfmpeg

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Video.Encoder.Key = "NVENC_HEVC", "Key should be NVENC_HEVC")
            Assert(v2.Video.Encoder.FFmpegCodec = "hevc_nvenc", "FFmpegCodec should be hevc_nvenc, got " & v2.Video.Encoder.FFmpegCodec)
        End Sub

        Private Sub Test_Migration_AudioMapping()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.SystemAudioCapture = True
            v1.SystemAudioVolume = 0.5F
            v1.MicCapture = True
            v1.MicVolume = 1.5F
            v1.MicDeviceName = "USB Mic"
            v1.MicDeviceId = "mic-id-123"
            v1.AudioTrackMode = 1 ' SeparateTrack

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Audio.System.Enabled, "System audio should be enabled")
            Assert(v2.Audio.System.Volume = 0.5F, "System volume should be 0.5")
            Assert(v2.Audio.Microphone.Enabled, "Mic should be enabled")
            Assert(v2.Audio.Microphone.Volume = 1.5F, "Mic volume should be 1.5")
            Assert(v2.Audio.Microphone.DeviceName = "USB Mic", "Mic device name preserved")
            Assert(v2.Audio.Microphone.DeviceId = "mic-id-123", "Mic device id preserved")
            Assert(v2.Audio.Encoding.TrackMode = "separate", "TrackMode should be 'separate' for AudioTrackMode=1")
        End Sub

        Private Sub Test_Migration_Paths()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.FFmpegPath = "C:\ffmpeg\ffmpeg.exe"
            v1.OutputDirectory = "C:\Videos\Shadowplay"
            v1.FileFormat = "mp4"

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Runtime.FFmpegPath = "C:\ffmpeg\ffmpeg.exe", "FFmpegPath should be preserved")
            Assert(v2.Output.Directory = "C:\Videos\Shadowplay", "OutputDirectory should be preserved")
            Assert(v2.Output.Container = "mp4", "Container should be mp4")
        End Sub

        Private Sub Test_Migration_PresetMapping()
            ' Test 1: V1 has Preset string "p6" — should be preserved as-is
            Dim v1a As New ConfigMigrator.V1CaptureSettings()
            v1a.Preset = "p6"
            Dim v2a As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1a)
            Assert(v2a.Video.Encoder.Preset = "p6", "Preset 'p6' should be preserved, got " & v2a.Video.Encoder.Preset)

            ' Test 2: V1 has only NvencPreset integer 5 — should convert to "p5"
            Dim v1b As New ConfigMigrator.V1CaptureSettings()
            v1b.Preset = "" ' force fallback to integer
            v1b.NvencPreset = 5
            Dim v2b As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1b)
            Assert(v2b.Video.Encoder.Preset = "p5", "NvencPreset 5 should convert to 'p5', got " & v2b.Video.Encoder.Preset)
        End Sub

        Private Sub Test_Migration_BufsizeFix()
            ' Legacy bug: declared buf = bitrate*2 but used bitrate (1×).
            ' V2 fix: BufsizeBps = BitrateBps * 2 (the correct, intended value).
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.BitrateBps = 20000000L

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Video.Encoder.BufsizeBps = 40000000L,
                   "BufsizeBps should be BitrateBps * 2 = 40000000 (FIX legacy bug), got " & v2.Video.Encoder.BufsizeBps)
        End Sub

        Private Sub Test_Migration_GopFromFps()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.FPS = 144

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Video.Capture.Framerate = 144, "Framerate should be 144")
            Assert(v2.Video.Encoder.GopSize = 144,
                   "GopSize should = Framerate (1-second GOP, matches legacy), got " & v2.Video.Encoder.GopSize)
        End Sub

        Private Sub Test_Migration_LegacyDefaults()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            ' Use defaults — should produce legacy-hardcoded NVENC values

            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2.Video.Encoder.Tune = "ll", "Tune should be 'll' (legacy hardcoded)")
            Assert(v2.Video.Encoder.SpatialAQ = 1, "SpatialAQ should be 1 (legacy hardcoded)")
            Assert(v2.Video.Encoder.TemporalAQ = 1, "TemporalAQ should be 1 (legacy hardcoded)")
            Assert(v2.Video.Encoder.LookAhead = 0, "LookAhead should be 0 (legacy hardcoded)")
            Assert(v2.Video.Encoder.ZeroLatency = False, "ZeroLatency should be False (legacy hardcoded)")
            Assert(v2.Video.Encoder.Cq = 0, "Cq should be 0 (legacy hardcoded)")
            Assert(v2.Video.Encoder.FpsMode = "cfr", "FpsMode should be 'cfr' (legacy hardcoded)")
            Assert(v2.Video.Encoder.PixelFormat = "nv12", "PixelFormat should be 'nv12' (legacy default)")
            Assert(v2.Runtime.ProcessPriority = "AboveNormal", "ProcessPriority should be AboveNormal (legacy)")
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' TEST 2: V2 round-trip (save → load → no data loss)
        ' ════════════════════════════════════════════════════════════

        Private Sub Test_RoundTrip_NoDataLoss()
            Dim original As New EngineConfigV2()
            original.Video.Capture.Framerate = 144
            original.Video.Encoder.Key = "NVENC_HEVC"
            original.Video.Encoder.FFmpegCodec = "hevc_nvenc"
            original.Video.Encoder.BitrateBps = 40000000L
            original.Video.Encoder.BufsizeBps = 80000000L
            original.Video.Encoder.GopSize = 144
            original.Video.Encoder.Preset = "p7"
            original.Video.Encoder.Tune = "ull"
            original.Video.Encoder.SpatialAQ = 0
            original.Video.Encoder.TemporalAQ = 0
            original.Audio.System.Enabled = False
            original.Audio.Microphone.Enabled = True
            original.Audio.Microphone.Volume = 1.5F
            original.Audio.Encoding.Codec = "opus"
            original.Audio.Encoding.SampleRate = 96000
            original.Output.Container = "mkv"
            original.Output.Directory = "D:\Test"
            original.Runtime.FFmpegPath = "C:\ffmpeg.exe"
            original.Runtime.LogLevel = "debug"

            Dim tmpFile As String = Path.Combine(Path.GetTempPath(), "engineconfig_v2_test_" & Guid.NewGuid().ToString("N") & ".json")
            Try
                ConfigLoader.Save(original, tmpFile)
                Dim loaded As EngineConfigV2 = ConfigLoader.Load(tmpFile)

                Assert(loaded IsNot Nothing, "Loaded config should not be Nothing")
                Assert(loaded.Video.Capture.Framerate = 144, "Framerate preserved")
                Assert(loaded.Video.Encoder.Key = "NVENC_HEVC", "Encoder Key preserved")
                Assert(loaded.Video.Encoder.FFmpegCodec = "hevc_nvenc", "FFmpegCodec preserved")
                Assert(loaded.Video.Encoder.BitrateBps = 40000000L, "BitrateBps preserved")
                Assert(loaded.Video.Encoder.BufsizeBps = 80000000L, "BufsizeBps preserved")
                Assert(loaded.Video.Encoder.GopSize = 144, "GopSize preserved")
                Assert(loaded.Video.Encoder.Preset = "p7", "Preset preserved")
                Assert(loaded.Video.Encoder.Tune = "ull", "Tune preserved")
                Assert(loaded.Video.Encoder.SpatialAQ = 0, "SpatialAQ preserved")
                Assert(loaded.Video.Encoder.TemporalAQ = 0, "TemporalAQ preserved")
                Assert(Not loaded.Audio.System.Enabled, "System audio disabled preserved")
                Assert(loaded.Audio.Microphone.Enabled, "Mic enabled preserved")
                Assert(loaded.Audio.Microphone.Volume = 1.5F, "Mic volume preserved")
                Assert(loaded.Audio.Encoding.Codec = "opus", "Audio codec preserved")
                Assert(loaded.Audio.Encoding.SampleRate = 96000, "Sample rate preserved")
                Assert(loaded.Output.Container = "mkv", "Container preserved")
                Assert(loaded.Output.Directory = "D:\Test", "Output directory preserved")
                Assert(loaded.Runtime.FFmpegPath = "C:\ffmpeg.exe", "FFmpegPath preserved")
                Assert(loaded.Runtime.LogLevel = "debug", "LogLevel preserved")
            Finally
                If File.Exists(tmpFile) Then File.Delete(tmpFile)
            End Try
        End Sub

        Private Sub Test_RoundTrip_JsonRoundTrip()
            Dim original As New EngineConfigV2()
            original.Video.Encoder.BitrateBps = 25000000L
            original.Video.Encoder.Preset = "p5"

            Dim json As String = ConfigLoader.SerializeToJson(original)
            Dim restored As EngineConfigV2 = ConfigLoader.DeserializeFromJson(json)

            Assert(restored.Video.Encoder.BitrateBps = 25000000L, "BitrateBps after JSON round-trip")
            Assert(restored.Video.Encoder.Preset = "p5", "Preset after JSON round-trip")
        End Sub

        Private Sub Test_RoundTrip_Defaults()
            Dim cfg As New EngineConfigV2()

            Assert(cfg.Version = EngineConfigV2.SchemaVersion, "Default Version = SchemaVersion")
            Assert(cfg.Video.Capture.Method = "ddagrab", "Default Capture.Method = ddagrab")
            Assert(cfg.Video.Capture.Framerate = 60, "Default Framerate = 60")
            Assert(cfg.Video.Encoder.Key = "NVENC_H264", "Default Encoder.Key = NVENC_H264")
            Assert(cfg.Video.Encoder.FFmpegCodec = "h264_nvenc", "Default FFmpegCodec = h264_nvenc")
            Assert(cfg.Video.Encoder.Preset = "p4", "Default Preset = p4")
            Assert(cfg.Video.Encoder.RateControl = "cbr", "Default RateControl = cbr")
            Assert(cfg.Video.Encoder.BitrateBps = 20000000L, "Default BitrateBps = 20 Mbps")
            Assert(cfg.Video.Encoder.BufsizeBps = 40000000L, "Default BufsizeBps = 40 Mbps (= bitrate * 2)")
            Assert(cfg.Video.Encoder.GopSize = 60, "Default GopSize = 60 (= framerate)")
            Assert(cfg.Audio.Encoding.Codec = "aac", "Default Audio.Codec = aac")
            Assert(cfg.Audio.Encoding.SampleRate = 48000, "Default SampleRate = 48000")
            Assert(cfg.Output.Container = "mp4", "Default Container = mp4")
            Assert(cfg.Output.FastStart, "Default FastStart = true")
            Assert(cfg.Runtime.ProcessPriority = "AboveNormal", "Default ProcessPriority = AboveNormal")
            Assert(cfg.Runtime.LogLevel = "info", "Default LogLevel = info")
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' TEST 3: Invalid config rejection
        ' ════════════════════════════════════════════════════════════

        Private Sub Test_Invalid_MissingEncoderKey()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.Key = ""
            cfg.Video.Encoder.FFmpegCodec = "h264_nvenc"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasKeyError As Boolean = False
            For Each e As String In errors
                If e.Contains("Encoder.Key") Then hasKeyError = True
            Next
            Assert(hasKeyError, "Should reject missing encoder Key. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_MissingFFmpegCodec()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.Key = "NVENC_H264"
            cfg.Video.Encoder.FFmpegCodec = ""

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasCodecError As Boolean = False
            For Each e As String In errors
                If e.Contains("FFmpegCodec") Then hasCodecError = True
            Next
            Assert(hasCodecError, "Should reject missing FFmpegCodec. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_NegativeBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.BitrateBps = -1000L
            cfg.Video.Encoder.MinrateBps = -1000L
            cfg.Video.Encoder.MaxrateBps = -1000L
            cfg.Video.Encoder.BufsizeBps = -1000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasBitrateError As Boolean = False
            For Each e As String In errors
                If e.Contains("BitrateBps") Then hasBitrateError = True
            Next
            Assert(hasBitrateError, "Should reject negative bitrate. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_BitrateTooHigh()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.BitrateBps = 500_000_000L ' 500 Mbps — over 200 Mbps cap
            cfg.Video.Encoder.MinrateBps = cfg.Video.Encoder.BitrateBps
            cfg.Video.Encoder.MaxrateBps = cfg.Video.Encoder.BitrateBps
            cfg.Video.Encoder.BufsizeBps = cfg.Video.Encoder.BitrateBps * 2

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("BitrateBps") AndAlso e.Contains("200") Then hasError = True
            Next
            Assert(hasError, "Should reject bitrate above 200 Mbps. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_CaptureMethod()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Capture.Method = "x11grab"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("Capture.Method") Then hasError = True
            Next
            Assert(hasError, "Should reject unknown capture method. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_RateControl()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "cqp"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("RateControl") Then hasError = True
            Next
            Assert(hasError, "Should reject unknown rate control. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_FpsMode()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.FpsMode = "auto"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("FpsMode") Then hasError = True
            Next
            Assert(hasError, "Should reject unknown fps_mode. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_AudioCodec()
            Dim cfg As New EngineConfigV2()
            cfg.Audio.Encoding.Codec = "mp3"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("Audio.Encoding.Codec") Then hasError = True
            Next
            Assert(hasError, "Should reject unknown audio codec. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_PixelFormat()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.PixelFormat = "rgb24"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("PixelFormat") Then hasError = True
            Next
            Assert(hasError, "Should reject unknown pixel format. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_BufsizeBelowBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.BufsizeBps = 10000000L ' below bitrate

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("BufsizeBps") AndAlso e.Contains("BitrateBps") Then hasError = True
            Next
            Assert(hasError, "Should reject bufsize < bitrate. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_MinrateExceedsMaxrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 30000000L ' > maxrate
            cfg.Video.Encoder.MaxrateBps = 20000000L
            cfg.Video.Encoder.BufsizeBps = 40000000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("MinrateBps") AndAlso e.Contains("MaxrateBps") Then hasError = True
            Next
            Assert(hasError, "Should reject minrate > maxrate. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_CustomResMissingDims()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Resolution.Mode = "custom"
            cfg.Video.Resolution.Width = 0
            cfg.Video.Resolution.Height = 0

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("Resolution") Then hasError = True
            Next
            Assert(hasError, "Should reject custom resolution with zero dimensions. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_OddResolution()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Resolution.Mode = "custom"
            cfg.Video.Resolution.Width = 1921 ' odd — H.264 macroblock requirement
            cfg.Video.Resolution.Height = 1081

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("even") Then hasError = True
            Next
            Assert(hasError, "Should reject odd dimensions (H.264 macroblock). Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_CqOutOfRange()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "cq"
            cfg.Video.Encoder.Cq = 100 ' out of 1-51 range

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("Cq") AndAlso e.Contains("1-51") Then hasError = True
            Next
            Assert(hasError, "Should reject CQ out of 1-51 range. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_RealtimePriority()
            Dim cfg As New EngineConfigV2()
            cfg.Runtime.ProcessPriority = "Realtime"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("ProcessPriority") AndAlso e.Contains("Realtime") Then hasError = True
            Next
            Assert(hasError, "Should reject Realtime priority (scheduler headroom rule). Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_Invalid_NothingConfig()
            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(Nothing)
            Assert(errors.Count > 0, "Should reject Nothing config")
            Assert(errors(0).Contains("Nothing"), "First error should mention Nothing")
        End Sub

        Private Sub Test_Valid_DefaultConfig()
            Dim cfg As New EngineConfigV2()
            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Assert(errors.Count = 0, "Default config should be valid. Errors: " & String.Join("; ", errors))
            Assert(ConfigValidator.IsValid(cfg), "IsValid should return True for default config")
        End Sub
    End Module
End Namespace
