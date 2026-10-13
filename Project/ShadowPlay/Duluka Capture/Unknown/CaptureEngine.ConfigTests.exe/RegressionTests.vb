Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Reflection
Imports CaptureEngine.Backends
Imports CaptureEngine.Configuration
Imports CaptureEngine.Configuration.Schema
Imports CaptureEngine.FFmpeg
Imports CaptureEngine.Pipeline

Namespace CaptureEngine.ConfigTests.Regression
    ''' <summary>
    ''' Phase 6 regression tests — covers:
    '''
    '''   - Lifecycle: Create → Start → Stop → Dispose (and negative cases)
    '''     for ConfigMigrator + ConfigLoader + ConfigValidator + PipelineResolver
    '''   - FFmpeg command snapshot: V1 vs V2 parity for same logical config
    '''     (verify codec, fps, bitrate, preset, gop, aq, bufsize)
    '''
    ''' This module is invoked from Program.vb after Phase 1 tests run.
    ''' Pattern follows CaptureEngine.Tests/Program.vb (custom console runner,
    ''' no xUnit/NUnit/MSTest).
    ''' </summary>
    Friend Module RegressionTests
        Private _passed As Integer = 0
        Private _failed As Integer = 0
        Private ReadOnly _failures As New List(Of String)()

        Public ReadOnly Property Passed As Integer
            Get
                Return _passed
            End Get
        End Property

        Public ReadOnly Property Failed As Integer
            Get
                Return _failed
            End Get
        End Property

        Public ReadOnly Property Failures As IReadOnlyList(Of String)
            Get
                Return _failures
            End Get
        End Property

        Public Sub Reset()
            _passed = 0
            _failed = 0
            _failures.Clear()
        End Sub

        Public Sub RunAll()
            RunTest("L1.1 Lifecycle: V2 config can be constructed", AddressOf Test_Lifecycle_Construct)
            RunTest("L1.2 Lifecycle: V2 config → migrate → resolve → builder", AddressOf Test_Lifecycle_FullResolve)
            RunTest("L1.3 Lifecycle: pipeline resolve produces correct VideoBackend", AddressOf Test_Lifecycle_VideoBackendResolution)
            RunTest("L1.4 Lifecycle: pipeline resolve produces correct AudioBackend", AddressOf Test_Lifecycle_AudioBackendResolution)
            RunTest("L1.5 Lifecycle: pipeline resolve rejects unknown capture method", AddressOf Test_Lifecycle_RejectUnknownMethod)
            RunTest("L1.6 Lifecycle: pipeline builder rejects experimental DXGI (NotImplemented)", AddressOf Test_Lifecycle_DxgiNotImplemented)
            RunTest("L1.7 Lifecycle: resolver with audio enabled → wasapi_loopback", AddressOf Test_Lifecycle_WithAudio)
            RunTest("L1.8 Lifecycle: resolver with audio disabled → none", AddressOf Test_Lifecycle_NoAudio)
            RunTest("L1.9 Lifecycle: ConfigMigrator idempotent (migrate twice → same result)", AddressOf Test_Lifecycle_MigratorIdempotent)
            RunTest("L1.10 Lifecycle: V2 builder can be re-used (Build called twice)", AddressOf Test_Lifecycle_BuilderReusable)

            RunTest("S1.1 Snapshot: V1 default produces known command", AddressOf Test_Snapshot_V1Default)
            RunTest("S1.2 Snapshot: V2 default produces known command", AddressOf Test_Snapshot_V2Default)
            RunTest("S1.3 Snapshot: V2 fixes bufsize (2× bitrate)", AddressOf Test_Snapshot_V2BufsizeFix)
            RunTest("S1.4 Snapshot: V2 appends -pix_fmt for ddagrab+NVENC (V1 omits)", AddressOf Test_Snapshot_V2PixFmtAppended)
            RunTest("S1.5 Snapshot: V2 command contains -c:v h264_nvenc", AddressOf Test_Snapshot_V2Codec)
            RunTest("S1.6 Snapshot: V2 command contains -g 60 (GOP = FPS)", AddressOf Test_Snapshot_V2Gop)
            RunTest("S1.7 Snapshot: V2 command contains -preset p4", AddressOf Test_Snapshot_V2Preset)
            RunTest("S1.8 Snapshot: V2 command contains -b:v 20000000 (20 Mbps)", AddressOf Test_Snapshot_V2Bitrate)
            RunTest("S1.9 Snapshot: V2 command contains -fps_mode cfr", AddressOf Test_Snapshot_V2FpsMode)
            RunTest("S1.10 Snapshot: V2 command contains -spatial-aq 1 -temporal-aq 1", AddressOf Test_Snapshot_V2Aq)
            RunTest("S1.11 Snapshot: V2 command contains -movflags +faststart on final", AddressOf Test_Snapshot_V2FaststartFinal)
            RunTest("S1.12 Snapshot: V2 command SKIPS -movflags on .video.tmp.mp4", AddressOf Test_Snapshot_V2FaststartSkippedOnTemp)
            RunTest("S1.13 Snapshot: V2 command contains -y (overwrite)", AddressOf Test_Snapshot_V2Overwrite)
            RunTest("S1.14 Snapshot: V2 preserves custom resolution scale filter", AddressOf Test_Snapshot_V2CustomResolution)
            RunTest("S1.15 Snapshot: V2 supports non-default preset (p7)", AddressOf Test_Snapshot_V2CustomPreset)
            RunTest("S1.16 Snapshot: V2 supports custom framerate (144)", AddressOf Test_Snapshot_V2Fps144)
            RunTest("S1.17 Snapshot: V2 supports CQ mode (RateControl=cq, Cq=23)", AddressOf Test_Snapshot_V2CqMode)
            RunTest("S1.18 Snapshot: V2 supports ZeroLatency mode", AddressOf Test_Snapshot_V2ZeroLatency)
            RunTest("S1.19 Snapshot: V2 supports LookAhead > 0", AddressOf Test_Snapshot_V2LookAhead)
            RunTest("S1.20 Snapshot: V2 supports explicit Profile (high)", AddressOf Test_Snapshot_V2Profile)
            RunTest("S1.21 Snapshot: V2 -tune omitted when empty string", AddressOf Test_Snapshot_V2TuneOmittedWhenEmpty)
            RunTest("S1.22 Snapshot: V2 gdigrab command (no -vf hwdownload)", AddressOf Test_Snapshot_V2Gdigrab)
            RunTest("S1.23 Snapshot: V2 NVENC_HEVC encoder → hevc_nvenc", AddressOf Test_Snapshot_V2HevcEncoder)
            RunTest("S1.24 Snapshot: V2 contains expected OutputIndex in ddagrab (0)", AddressOf Test_Snapshot_V2OutputIndex)
            RunTest("S1.25 Snapshot: V2 default bitrate unit is bps (not kbps)", AddressOf Test_Snapshot_V2BitrateUnitBps)
            RunTest("S1.26 Snapshot: V2 ddagrab+QSV uses hwdownload (MFX session -9 regression)", AddressOf Test_Snapshot_V2QsvDdagrabHwDownload)
            RunTest("S1.27 Snapshot: V2 gfxcapture+QSV uses hwdownload (MFX session -9 regression)", AddressOf Test_Snapshot_V2QsvGfxcaptureHwDownload)
            RunTest("S1.28 Snapshot: V1 ddagrab+QSV uses hwdownload (MFX session -9 regression)", AddressOf Test_Snapshot_V1QsvDdagrabHwDownload)
            RunTest("S1.29 Snapshot: QSV capture commands contain no hwmap=derive_device=qsv", AddressOf Test_Snapshot_QsvNoHwmap)

            ' Parity delta: V1 vs V2 must differ ONLY in bufsize + pix_fmt
            RunTest("P1.1 Parity: V1 vs V2 same logical config differ only in bufsize + pix_fmt",
                    AddressOf Test_Parity_V1V2Delta)
            RunTest("P1.2 Parity: V1 vs V2 share identical encoder/fps/preset/gop/aq",
                    AddressOf Test_Parity_V1V2Shared)
        End Sub

        Private Sub RunTest(name As String, test As Action)
            Console.Write("[REG " & name & "] ")
            Dim pad As Integer = Math.Max(0, 70 - name.Length - 6)
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
        ' L1 — Lifecycle tests
        ' ════════════════════════════════════════════════════════════

        Private Sub Test_Lifecycle_Construct()
            Dim cfg As New EngineConfigV2()
            Assert(cfg IsNot Nothing, "V2 config should construct")
            Assert(cfg.Version = 2, "Version should be 2")
            Assert(cfg.Video.Capture.Method = "ddagrab", "Default capture method")
            Assert(cfg.Video.Encoder.Key = "NVENC_H264", "Default encoder key")
        End Sub

        Private Sub Test_Lifecycle_FullResolve()
            ' End-to-end: V1 → V2 → Resolve → Builder → Build
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)
            Dim pipeline As PipelineConfig = PipelineResolver.Resolve(v2)
            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("test.mp4")

            Assert(pipeline.VideoBackend = "ffmpeg_ddagrab", "Should resolve to ffmpeg_ddagrab")
            Assert(pipeline.AudioBackend = "wasapi_loopback", "Should resolve to wasapi_loopback (system audio on by default)")
            Assert(builder.BuilderLabel.Contains("V2"), "Should be V2 builder")
            Assert(cmd.Contains("ddagrab=output_idx=0:framerate=60"), "Should contain ddagrab input")
            Assert(cmd.Contains("h264_nvenc"), "Should contain h264_nvenc")
        End Sub

        Private Sub Test_Lifecycle_VideoBackendResolution()
            For Each method As String In {"ddagrab", "gdigrab", "gfxcapture"}
                Dim v2 As New EngineConfigV2()
                v2.Video.Capture.Method = method
                Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
                Assert(p.VideoBackend = "ffmpeg_" & method,
                       $"Method '{method}' should resolve to 'ffmpeg_{method}', got '{p.VideoBackend}'")
            Next
        End Sub

        Private Sub Test_Lifecycle_AudioBackendResolution()
            ' Both audio sources off → none
            Dim v2a As New EngineConfigV2()
            v2a.Audio.System.Enabled = False
            v2a.Audio.Microphone.Enabled = False
            Dim pa As PipelineConfig = PipelineResolver.Resolve(v2a)
            Assert(pa.AudioBackend = "none", "Both disabled should be 'none'")

            ' System on → wasapi_loopback
            Dim v2b As New EngineConfigV2()
            v2b.Audio.System.Enabled = True
            v2b.Audio.Microphone.Enabled = False
            Dim pb As PipelineConfig = PipelineResolver.Resolve(v2b)
            Assert(pb.AudioBackend = "wasapi_loopback", "System on should be 'wasapi_loopback'")

            ' Mic on → wasapi_loopback
            Dim v2c As New EngineConfigV2()
            v2c.Audio.System.Enabled = False
            v2c.Audio.Microphone.Enabled = True
            Dim pc As PipelineConfig = PipelineResolver.Resolve(v2c)
            Assert(pc.AudioBackend = "wasapi_loopback", "Mic on should be 'wasapi_loopback'")
        End Sub

        Private Sub Test_Lifecycle_RejectUnknownMethod()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Method = "x11grab"
            Try
                PipelineResolver.Resolve(v2)
                Throw New InvalidOperationException("Should have thrown for unknown method")
            Catch ex As InvalidOperationException
                Assert(ex.Message.Contains("x11grab"), "Error message should mention the bad method")
            End Try
        End Sub

        Private Sub Test_Lifecycle_DxgiNotImplemented()
            Dim v2 As New EngineConfigV2()
            v2.Experimental.EnableD3D11Interop = True
            v2.Video.Capture.Method = "ddagrab"

            ' Resolve should mark as DXGI
            Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p.VideoBackend = "dxgi", "Should resolve to 'dxgi' when experimental flag set")

            ' But builder should throw NotImplementedException
            Try
                PipelineResolver.BuildFFmpegCommandBuilder(v2)
                Throw New InvalidOperationException("Should have thrown NotImplementedException")
            Catch ex As NotImplementedException
                Assert(ex.Message.Contains("DXGI"), "Error should mention DXGI")
            End Try
        End Sub

        Private Sub Test_Lifecycle_WithAudio()
            Dim v2 As New EngineConfigV2()
            v2.Audio.System.Enabled = True
            v2.Audio.Microphone.Enabled = False
            Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p.AudioBackend = "wasapi_loopback", "Should be wasapi_loopback")
        End Sub

        Private Sub Test_Lifecycle_NoAudio()
            Dim v2 As New EngineConfigV2()
            v2.Audio.System.Enabled = False
            v2.Audio.Microphone.Enabled = False
            Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p.AudioBackend = "none", "Should be 'none'")
        End Sub

        Private Sub Test_Lifecycle_MigratorIdempotent()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            v1.BitrateBps = 25000000L

            Dim v2a As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)
            Dim v2b As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)

            Assert(v2a.Video.Encoder.BitrateBps = v2b.Video.Encoder.BitrateBps, "BitrateBps should match")
            Assert(v2a.Video.Encoder.BufsizeBps = v2b.Video.Encoder.BufsizeBps, "BufsizeBps should match")
            Assert(v2a.Video.Encoder.GopSize = v2b.Video.Encoder.GopSize, "GopSize should match")
        End Sub

        Private Sub Test_Lifecycle_BuilderReusable()
            Dim v2 As New EngineConfigV2()
            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)

            Dim cmd1 As String = builder.Build("a.mp4")
            Dim cmd2 As String = builder.Build("b.mp4")

            Assert(cmd1.Contains("a.mp4"), "First build should contain a.mp4")
            Assert(cmd2.Contains("b.mp4"), "Second build should contain b.mp4")
            Assert(cmd1.Length > 0 AndAlso cmd2.Length > 0, "Both should be non-empty")
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' S1 — FFmpeg command snapshot tests
        ' ════════════════════════════════════════════════════════════

        Private Function BuildDefaultV2Command(outputFile As String) As String
            Dim v2 As New EngineConfigV2()
            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Return builder.Build(outputFile)
        End Function

        Private Function BuildDefaultV1Command(outputFile As String) As String
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v1Settings As New FFmpegCommandBuilderV1.V1Settings() With {
                .Encoder = "h264_nvenc",
                .FPS = v1.FPS,
                .Bitrate = v1.BitrateBps,
                .NvencPreset = v1.NvencPreset,
                .CaptureMethod = v1.CaptureMethod,
                .UseNativeResolution = v1.UseNativeResolution,
                .PixelFormat = v1.PixelFormat
            }
            Dim builder As New FFmpegCommandBuilderV1(v1Settings)
            Return builder.Build(outputFile)
        End Function

        Private Sub Test_Snapshot_V1Default()
            Dim cmd As String = BuildDefaultV1Command("out.mp4")
            Assert(cmd.Contains("-c:v h264_nvenc"), "Should contain h264_nvenc")
            Assert(cmd.Contains("ddagrab=output_idx=0:framerate=60"), "Should contain ddagrab framerate")
            Assert(cmd.Contains("-preset p4 -tune ll -rc cbr"), "Should contain preset/tune/rc")
            Assert(cmd.Contains("-b:v 20000000 -minrate 20000000 -maxrate 20000000 -bufsize 20000000"), "Should contain CBR bitrate params")
            Assert(cmd.Contains("-g 60 -fps_mode cfr"), "Should contain GOP + fps_mode")
            Assert(cmd.Contains("-spatial-aq 1 -temporal-aq 1"), "Should contain AQ")
            Assert(cmd.Contains("-movflags +faststart"), "Should contain faststart on final")
            Assert(cmd.Contains("-y"), "Should contain -y overwrite")
            Assert(cmd.Contains("out.mp4"), "Should contain output filename")
        End Sub

        Private Sub Test_Snapshot_V2Default()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-c:v h264_nvenc"), "Should contain h264_nvenc")
            Assert(cmd.Contains("ddagrab=output_idx=0:framerate=60"), "Should contain ddagrab framerate")
            Assert(cmd.Contains("-preset p4 -tune ll -rc cbr"), "Should contain preset/tune/rc")
            Assert(cmd.Contains("-g 60 -fps_mode cfr"), "Should contain GOP + fps_mode")
            Assert(cmd.Contains("-spatial-aq 1 -temporal-aq 1"), "Should contain AQ")
            Assert(cmd.Contains("-movflags +faststart"), "Should contain faststart on final")
            Assert(cmd.Contains("-y"), "Should contain -y overwrite")
            Assert(cmd.Contains("out.mp4"), "Should contain output filename")
        End Sub

        Private Sub Test_Snapshot_V2BufsizeFix()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            ' V2 FIX: bufsize = bitrate * 2 = 40000000 (V1 used 1× = 20000000)
            Assert(cmd.Contains("-bufsize 40000000"),
                   "V2 bufsize should be 40000000 (= bitrate * 2). Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V2PixFmtAppended()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            ' V2 FIX: -pix_fmt nv12 is appended for ddagrab+NVENC (V1 skips it)
            Assert(cmd.Contains("-pix_fmt nv12"),
                   "V2 should append -pix_fmt nv12 for ddagrab+NVENC. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V2Codec()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-c:v h264_nvenc"), "Should contain -c:v h264_nvenc")
        End Sub

        Private Sub Test_Snapshot_V2Gop()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-g 60 "), "Should contain -g 60 (GOP = 60 = FPS)")
        End Sub

        Private Sub Test_Snapshot_V2Preset()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-preset p4 "), "Should contain -preset p4")
        End Sub

        Private Sub Test_Snapshot_V2Bitrate()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-b:v 20000000 "), "Should contain -b:v 20000000 (bps)")
            Assert(cmd.Contains("-minrate 20000000 "), "Should contain -minrate 20000000")
            Assert(cmd.Contains("-maxrate 20000000 "), "Should contain -maxrate 20000000")
        End Sub

        Private Sub Test_Snapshot_V2FpsMode()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-fps_mode cfr "), "Should contain -fps_mode cfr")
        End Sub

        Private Sub Test_Snapshot_V2Aq()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-spatial-aq 1 "), "Should contain -spatial-aq 1")
            Assert(cmd.Contains("-temporal-aq 1 "), "Should contain -temporal-aq 1")
        End Sub

        Private Sub Test_Snapshot_V2FaststartFinal()
            Dim cmd As String = BuildDefaultV2Command("final.mp4")
            Assert(cmd.Contains("-movflags +faststart "), "Final output should get +faststart")
        End Sub

        Private Sub Test_Snapshot_V2FaststartSkippedOnTemp()
            Dim cmd As String = BuildDefaultV2Command("rec.video.tmp.mp4")
            Assert(Not cmd.Contains("-movflags"), "Temp video should NOT get -movflags +faststart. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V2Overwrite()
            Dim cmd As String = BuildDefaultV2Command("out.mp4")
            Assert(cmd.Contains("-y "), "Should contain -y overwrite")
            Assert(Not cmd.Contains("-n "), "Should NOT contain -n")
        End Sub

        Private Sub Test_Snapshot_V2CustomResolution()
            Dim v2 As New EngineConfigV2()
            v2.Video.Resolution.Mode = "custom"
            v2.Video.Resolution.Width = 1280
            v2.Video.Resolution.Height = 720

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("scale=1280:720"), "Should contain scale=1280:720")
            Assert(cmd.Contains("hwdownload,format=bgra,scale=1280:720,hwupload"),
                   "Should contain hwdownload+scale+hwupload chain for NVENC + custom res")
        End Sub

        Private Sub Test_Snapshot_V2CustomPreset()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.Preset = "p7"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-preset p7 "), "Should contain -preset p7")
            Assert(Not cmd.Contains("-preset p4 "), "Should NOT contain -preset p4")
        End Sub

        Private Sub Test_Snapshot_V2Fps144()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Framerate = 144
            ' GOP must follow framerate for 1-second GOP
            v2.Video.Encoder.GopSize = 144

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("framerate=144"), "Should contain framerate=144 in ddagrab input")
            Assert(cmd.Contains("-g 144 "), "Should contain -g 144 (GOP)")
        End Sub

        Private Sub Test_Snapshot_V2CqMode()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.RateControl = "cq"
            v2.Video.Encoder.Cq = 23
            ' CQ mode requires non-zero maxrate for NVENC
            v2.Video.Encoder.MinrateBps = 0L
            v2.Video.Encoder.MaxrateBps = v2.Video.Encoder.BitrateBps

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-rc cq "), "Should contain -rc cq")
            Assert(cmd.Contains("-cq 23 "), "Should contain -cq 23")
            Assert(Not cmd.Contains("-minrate "), "Should NOT append -minrate when MinrateBps=0")
        End Sub

        Private Sub Test_Snapshot_V2ZeroLatency()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.ZeroLatency = True

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-zerolatency 1 "), "Should contain -zerolatency 1")
        End Sub

        Private Sub Test_Snapshot_V2LookAhead()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.LookAhead = 8

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-look_ahead 8 "), "Should contain -look_ahead 8")
        End Sub

        Private Sub Test_Snapshot_V2Profile()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.Profile = "high"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-profile:v high "), "Should contain -profile:v high")
        End Sub

        Private Sub Test_Snapshot_V2TuneOmittedWhenEmpty()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.Tune = ""

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(Not cmd.Contains("-tune "), "Should NOT contain -tune when empty")
        End Sub

        Private Sub Test_Snapshot_V2Gdigrab()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Method = "gdigrab"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-f gdigrab -framerate 60 -i desktop"), "Should contain gdigrab input")
            Assert(Not cmd.Contains("ddagrab"), "Should NOT contain ddagrab")
            Assert(Not cmd.Contains("hwdownload"), "gdigrab is CPU capture — no hwdownload")
        End Sub

        Private Sub Test_Snapshot_V2HevcEncoder()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.Key = "NVENC_HEVC"
            v2.Video.Encoder.FFmpegCodec = "hevc_nvenc"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("-c:v hevc_nvenc"), "Should contain -c:v hevc_nvenc")
            Assert(Not cmd.Contains("h264_nvenc"), "Should NOT contain h264_nvenc")
        End Sub

        Private Sub Test_Snapshot_V2OutputIndex()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.OutputIndex = 1

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("ddagrab=output_idx=1:framerate=60"),
                   "Should contain output_idx=1. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V2BitrateUnitBps()
            ' Default BitrateBps = 20000000 = 20 Mbps (NOT 20000 kbps)
            Dim v2 As New EngineConfigV2()
            Assert(v2.Video.Encoder.BitrateBps = 20000000L, "Default should be 20000000 (bps)")

                Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
                Dim cmd As String = builder.Build("out.mp4")
                Assert(cmd.Contains("-b:v 20000000 "), "Command should use 20000000 (bps)")
            End Sub

        ' ── S1.26-S1.29: IntelQSV hwmap regression (2026-09-05) ──
        ' hwmap=derive_device=qsv fails on Intel iGPU with "Error creating a
        ' MFX session: -9" — every ddagrab/gfxcapture+QSV recording died at
        ' init, forcing users onto gdigrab (GDI ~25-28 real fps → heavy dup
        ' stutter). QSV encoders accept SYSTEM-memory frames (gdigrab+qsv
        ' always worked), so the GPU-resident frames must be downloaded
        ' instead of mapped. See Engine FFmpegArgumentBuilder.vb.
        Private Sub Test_Snapshot_V2QsvDdagrabHwDownload()
            Dim v2 As New EngineConfigV2()
            v2.Video.Encoder.FFmpegCodec = "h264_qsv"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("hwdownload,format=bgra,format=nv12"),
                   "ddagrab+QSV must download to system memory (nv12). Cmd: " & cmd)
            Assert(Not cmd.Contains("hwmap"), "ddagrab+QSV must NOT use hwmap. Cmd: " & cmd)
            Assert(cmd.Contains("-c:v h264_qsv"), "Should encode with h264_qsv. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V2QsvGfxcaptureHwDownload()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Method = "gfxcapture"
            v2.Video.Encoder.FFmpegCodec = "hevc_qsv"

            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("hwdownload,format=bgra,format=nv12"),
                   "gfxcapture+QSV must download to system memory (nv12). Cmd: " & cmd)
            Assert(Not cmd.Contains("hwmap"), "gfxcapture+QSV must NOT use hwmap. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_V1QsvDdagrabHwDownload()
            Dim v1Settings As New FFmpegCommandBuilderV1.V1Settings() With {
                .Encoder = "h264_qsv",
                .FPS = 60,
                .Bitrate = 5000000,
                .CaptureMethod = "ddagrab",
                .UseNativeResolution = True
            }
            Dim builder As New FFmpegCommandBuilderV1(v1Settings)
            Dim cmd As String = builder.Build("out.mp4")

            Assert(cmd.Contains("hwdownload,format=bgra,format=nv12"),
                   "V1 ddagrab+QSV must download to system memory (nv12). Cmd: " & cmd)
            Assert(Not cmd.Contains("hwmap"), "V1 ddagrab+QSV must NOT use hwmap. Cmd: " & cmd)
        End Sub

        Private Sub Test_Snapshot_QsvNoHwmap()
            ' No capture method + QSV encoder may produce hwmap=derive_device=qsv.
            For Each method As String In {"ddagrab", "gfxcapture", "gdigrab"}
                Dim v1Settings As New FFmpegCommandBuilderV1.V1Settings() With {
                    .Encoder = "h264_qsv",
                    .FPS = 30,
                    .Bitrate = 7000000,
                    .CaptureMethod = method,
                    .UseNativeResolution = True
                }
                Dim v1Cmd As String = New FFmpegCommandBuilderV1(v1Settings).Build("out.mp4")
                Assert(Not v1Cmd.Contains("hwmap=derive_device=qsv"),
                       $"V1 {method}+QSV must not contain hwmap=derive_device=qsv. Cmd: " & v1Cmd)

                Dim v2 As New EngineConfigV2()
                v2.Video.Capture.Method = method
                v2.Video.Encoder.FFmpegCodec = "h264_qsv"
                Dim v2Cmd As String = PipelineResolver.BuildFFmpegCommandBuilder(v2).Build("out.mp4")
                Assert(Not v2Cmd.Contains("hwmap=derive_device=qsv"),
                       $"V2 {method}+QSV must not contain hwmap=derive_device=qsv. Cmd: " & v2Cmd)
            Next
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' P1 — V1 vs V2 parity delta
        ' ════════════════════════════════════════════════════════════

        Private Sub Test_Parity_V1V2Delta()
            Dim v1Cmd As String = BuildDefaultV1Command("out.mp4")

            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)
            Dim v2Cmd As String = PipelineResolver.BuildFFmpegCommandBuilder(v2).Build("out.mp4")

            ' Differences that MUST exist (V2 fixes):
            ' 1. bufsize: V1 has 20000000, V2 has 40000000
            Assert(v1Cmd.Contains("-bufsize 20000000 "), "V1 should have bufsize 20000000 (1× bitrate)")
            Assert(v2Cmd.Contains("-bufsize 40000000 "), "V2 should have bufsize 40000000 (2× bitrate = FIX)")

            ' 2. pix_fmt: V1 omits for ddagrab+NVENC, V2 appends
            Assert(Not v1Cmd.Contains("-pix_fmt "), "V1 should NOT contain -pix_fmt for ddagrab+NVENC")
            Assert(v2Cmd.Contains("-pix_fmt nv12 "), "V2 should append -pix_fmt nv12 for ddagrab+NVENC")
        End Sub

        Private Sub Test_Parity_V1V2Shared()
            Dim v1Cmd As String = BuildDefaultV1Command("out.mp4")

            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)
            Dim v2Cmd As String = PipelineResolver.BuildFFmpegCommandBuilder(v2).Build("out.mp4")

            ' Shared FFmpeg args (byte-identical):
            Assert(v1Cmd.Contains("-c:v h264_nvenc") AndAlso v2Cmd.Contains("-c:v h264_nvenc"), "Both: -c:v h264_nvenc")
            Assert(v1Cmd.Contains("ddagrab=output_idx=0:framerate=60") AndAlso v2Cmd.Contains("ddagrab=output_idx=0:framerate=60"), "Both: ddagrab input")
            Assert(v1Cmd.Contains("-preset p4 -tune ll -rc cbr") AndAlso v2Cmd.Contains("-preset p4 -tune ll -rc cbr"), "Both: preset/tune/rc")
            Assert(v1Cmd.Contains("-b:v 20000000 -minrate 20000000 -maxrate 20000000") AndAlso v2Cmd.Contains("-b:v 20000000 -minrate 20000000 -maxrate 20000000"), "Both: bitrate + minrate + maxrate")
            Assert(v1Cmd.Contains("-g 60 -fps_mode cfr") AndAlso v2Cmd.Contains("-g 60 -fps_mode cfr"), "Both: GOP + fps_mode")
            Assert(v1Cmd.Contains("-spatial-aq 1 -temporal-aq 1") AndAlso v2Cmd.Contains("-spatial-aq 1 -temporal-aq 1"), "Both: AQ")
            Assert(v1Cmd.Contains("-movflags +faststart") AndAlso v2Cmd.Contains("-movflags +faststart"), "Both: +faststart")
            Assert(v1Cmd.Contains("-y ") AndAlso v2Cmd.Contains("-y "), "Both: -y overwrite")
            Assert(v1Cmd.Contains("out.mp4") AndAlso v2Cmd.Contains("out.mp4"), "Both: output filename")
        End Sub

        ' ════════════════════════════════════════════════════════════
        ' Phase 5 — Stabilization hardening tests
        ' ════════════════════════════════════════════════════════════

        Public Sub RunPhase5Hardening()
            RunTest("H1.1 CBR invariant: minrate=bitrate enforced", AddressOf Test_H_CBR_MinrateEqualsBitrate)
            RunTest("H1.2 CBR invariant: maxrate=bitrate enforced", AddressOf Test_H_CBR_MaxrateEqualsBitrate)
            RunTest("H1.3 CBR invariant: bitrate=0 rejected", AddressOf Test_H_CBR_BitratePositive)
            RunTest("H1.4 VBR: minrate > bitrate rejected", AddressOf Test_H_VBR_MinrateExceedsBitrate)
            RunTest("H1.5 VBR: maxrate < bitrate rejected", AddressOf Test_H_VBR_MaxrateBelowBitrate)
            RunTest("H1.6 VBR: minrate <= bitrate <= maxrate accepted", AddressOf Test_H_VBR_ValidRange)
            RunTest("H2.1 Encoder.Key/FFmpegCodec mismatch rejected", AddressOf Test_H_EncoderKeyMismatch)
            RunTest("H2.2 Encoder.Key/FFmpegCodec match accepted", AddressOf Test_H_EncoderKeyMatch)
            RunTest("H2.3 Encoder.Key/FFmpegCodec case-insensitive match accepted", AddressOf Test_H_EncoderKeyCaseInsensitive)
            RunTest("H3.1 Hotkeys round-trip: case-insensitive lookup after load", AddressOf Test_H_HotkeysRoundTripCaseInsensitive)
            RunTest("H3.2 Hotkeys round-trip: no data loss after save+load", AddressOf Test_H_HotkeysRoundTripNoDataLoss)
            RunTest("H3.3 Hotkeys: empty value rejected by validator", AddressOf Test_H_HotkeysEmptyValueRejected)
            RunTest("H4.1 Invalid pipeline: empty capture method rejected", AddressOf Test_H_Pipeline_EmptyMethod)
            RunTest("H4.2 Invalid pipeline: case-insensitive capture method accepted", AddressOf Test_H_Pipeline_CaseInsensitiveMethod)
            RunTest("H4.3 Invalid pipeline: audio backend always resolves", AddressOf Test_H_Pipeline_AudioDeterministic)
            RunTest("H4.4 Invalid pipeline: experimental DXGI explicitly throws", AddressOf Test_H_Pipeline_DxgiExplicitThrow)
            RunTest("H5.1 V1 determinism: same config → same command (10 builds)", AddressOf Test_H_V1_Determinism10Builds)
            RunTest("H5.2 V2 determinism: same config → same command (10 builds)", AddressOf Test_H_V2_Determinism10Builds)
            RunTest("H5.3 V1/V2 byte-identical except for 2 documented diffs", AddressOf Test_H_V1V2_ByteIdenticalDelta)
            RunTest("H5.4 V2 builder produces empty output for empty path (reject)", AddressOf Test_H_V2_RejectEmptyPath)
            RunTest("H5.5 V1 builder produces empty output for empty path (reject)", AddressOf Test_H_V1_RejectEmptyPath)
            RunTest("H5.6 V2 default config validation passes (no errors)", AddressOf Test_H_V2_DefaultConfigValidates)
            RunTest("H6.1 IVideoBackend has [Stop] method (BC30183 regression)", AddressOf Test_H_IVideoBackend_StopMethodCompiles)
            RunTest("H6.2 IVideoBackend has Start, GetFrame, Dispose, CurrentState, GetDiagnostics", AddressOf Test_H_IVideoBackend_FullSurface)
            RunTest("H7.1 ConfigLoader.LoadDefault resolves System.IO.Path.Combine (BC30456 regression)", AddressOf Test_H_ConfigLoader_LoadDefaultResolvesPath)
            RunTest("H7.2 ConfigLoader.SaveDefault resolves System.IO.Path.Combine (BC30456 regression)", AddressOf Test_H_ConfigLoader_SaveDefaultResolvesPath)
        End Sub

        ' ── CBR/VBR invariant tests (Phase 5) ──

        Private Sub Test_H_CBR_MinrateEqualsBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "cbr"
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 15000000L  ' != bitrate
            cfg.Video.Encoder.MaxrateBps = 20000000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasCBRError As Boolean = False
            For Each e As String In errors
                If e.Contains("MinrateBps") AndAlso e.Contains("BitrateBps") AndAlso e.Contains("cbr") Then hasCBRError = True
            Next
            Assert(hasCBRError, "Should reject MinrateBps != BitrateBps for CBR. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_CBR_MaxrateEqualsBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "cbr"
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 20000000L
            cfg.Video.Encoder.MaxrateBps = 25000000L  ' != bitrate

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasCBRError As Boolean = False
            For Each e As String In errors
                If e.Contains("MaxrateBps") AndAlso e.Contains("BitrateBps") AndAlso e.Contains("cbr") Then hasCBRError = True
            Next
            Assert(hasCBRError, "Should reject MaxrateBps != BitrateBps for CBR. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_CBR_BitratePositive()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "cbr"
            cfg.Video.Encoder.BitrateBps = 0L
            cfg.Video.Encoder.MinrateBps = 0L
            cfg.Video.Encoder.MaxrateBps = 0L
            cfg.Video.Encoder.BufsizeBps = 0L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasBitrateError As Boolean = False
            For Each e As String In errors
                If e.Contains("BitrateBps") AndAlso (e.Contains("1 Mbps") OrElse e.Contains("> 0")) Then hasBitrateError = True
            Next
            Assert(hasBitrateError, "Should reject bitrate=0 for CBR. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_VBR_MinrateExceedsBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "vbr"
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 30000000L  ' > bitrate
            cfg.Video.Encoder.MaxrateBps = 40000000L
            cfg.Video.Encoder.BufsizeBps = 40000000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("MinrateBps") AndAlso e.Contains("vbr") Then hasError = True
            Next
            Assert(hasError, "Should reject minrate > bitrate for VBR. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_VBR_MaxrateBelowBitrate()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "vbr"
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 10000000L
            cfg.Video.Encoder.MaxrateBps = 15000000L  ' < bitrate
            cfg.Video.Encoder.BufsizeBps = 40000000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasError As Boolean = False
            For Each e As String In errors
                If e.Contains("MaxrateBps") AndAlso e.Contains("vbr") Then hasError = True
            Next
            Assert(hasError, "Should reject maxrate < bitrate for VBR. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_VBR_ValidRange()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.RateControl = "vbr"
            cfg.Video.Encoder.BitrateBps = 20000000L
            cfg.Video.Encoder.MinrateBps = 10000000L  ' <= bitrate
            cfg.Video.Encoder.MaxrateBps = 30000000L  ' >= bitrate
            cfg.Video.Encoder.BufsizeBps = 40000000L

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            ' Filter out non-VBR errors
            Dim vbrErrors As New List(Of String)()
            For Each e As String In errors
                If e.Contains("vbr") OrElse e.Contains("VBR") Then vbrErrors.Add(e)
            Next
            Assert(vbrErrors.Count = 0, "VBR with valid range should have no VBR errors. VBR errors: " & String.Join("; ", vbrErrors))
        End Sub

        ' ── Encoder.Key / FFmpegCodec consistency tests (Phase 5) ──

        Private Sub Test_H_EncoderKeyMismatch()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.Key = "NVENC_HEVC"
            cfg.Video.Encoder.FFmpegCodec = "h264_nvenc"  ' mismatch — should be hevc_nvenc

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasMismatch As Boolean = False
            For Each e As String In errors
                If e.Contains("does not match canonical") Then hasMismatch = True
            Next
            Assert(hasMismatch, "Should reject Key/FFmpegCodec mismatch. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_EncoderKeyMatch()
            Dim cfg As New EngineConfigV2()  ' default: NVENC_H264 + h264_nvenc
            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasMismatch As Boolean = False
            For Each e As String In errors
                If e.Contains("does not match canonical") Then hasMismatch = True
            Next
            Assert(Not hasMismatch, "Default Key/FFmpegCodec should match. Errors: " & String.Join("; ", errors))
        End Sub

        Private Sub Test_H_EncoderKeyCaseInsensitive()
            Dim cfg As New EngineConfigV2()
            cfg.Video.Encoder.Key = "nvenc_h264"  ' lowercase
            cfg.Video.Encoder.FFmpegCodec = "h264_nvenc"

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasMismatch As Boolean = False
            For Each e As String In errors
                If e.Contains("does not match canonical") Then hasMismatch = True
            Next
            Assert(Not hasMismatch, "Case-insensitive match should pass. Errors: " & String.Join("; ", errors))
        End Sub

        ' ── Hotkeys round-trip tests (Phase 5) ──

        Private Sub Test_H_HotkeysRoundTripCaseInsensitive()
            Dim original As New EngineConfigV2()
            original.Runtime.Hotkeys.Clear()
            original.Runtime.Hotkeys("ToggleOverlay") = "Alt+Z"

            Dim json As String = ConfigLoader.SerializeToJson(original)
            Dim loaded As EngineConfigV2 = ConfigLoader.DeserializeFromJson(json)

            ' Phase 1.F: after NormalizeHotkeysComparer, lookup must be case-insensitive
            Dim result As String = Nothing
            Dim found As Boolean = loaded.Runtime.Hotkeys.TryGetValue("toggleoverlay", result)
            Assert(found, "Case-insensitive lookup 'toggleoverlay' should find 'ToggleOverlay' after load")
            Assert(result = "Alt+Z", "Value should be Alt+Z, got " & result)

            ' Also verify with all-caps
            Dim found2 As Boolean = loaded.Runtime.Hotkeys.TryGetValue("TOGGLEOVERLAY", result)
            Assert(found2, "Case-insensitive lookup 'TOGGLEOVERLAY' should find 'ToggleOverlay' after load")
        End Sub

        Private Sub Test_H_HotkeysRoundTripNoDataLoss()
            Dim original As New EngineConfigV2()
            original.Runtime.Hotkeys.Clear()
            original.Runtime.Hotkeys("ToggleOverlay") = "Alt+Z"
            original.Runtime.Hotkeys("Screenshot") = "Alt+F1"
            original.Runtime.Hotkeys("ManualRecordToggle") = "Alt+F9"

            Dim json As String = ConfigLoader.SerializeToJson(original)
            Dim loaded As EngineConfigV2 = ConfigLoader.DeserializeFromJson(json)

            Assert(loaded.Runtime.Hotkeys.Count = 3, "Should have 3 hotkeys after round-trip, got " & loaded.Runtime.Hotkeys.Count)
            Assert(loaded.Runtime.Hotkeys("ToggleOverlay") = "Alt+Z", "ToggleOverlay preserved")
            Assert(loaded.Runtime.Hotkeys("Screenshot") = "Alt+F1", "Screenshot preserved")
            Assert(loaded.Runtime.Hotkeys("ManualRecordToggle") = "Alt+F9", "ManualRecordToggle preserved")
        End Sub

        Private Sub Test_H_HotkeysEmptyValueRejected()
            Dim cfg As New EngineConfigV2()
            cfg.Runtime.Hotkeys.Clear()
            cfg.Runtime.Hotkeys("ToggleOverlay") = ""  ' empty value

            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Dim hasEmptyError As Boolean = False
            For Each e As String In errors
                If e.Contains("Hotkeys") AndAlso e.Contains("empty") Then hasEmptyError = True
            Next
            Assert(hasEmptyError, "Should reject empty hotkey value. Errors: " & String.Join("; ", errors))
        End Sub

        ' ── Invalid pipeline tests (Phase 5) ──

        Private Sub Test_H_Pipeline_EmptyMethod()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Method = ""
            Try
                PipelineResolver.Resolve(v2)
                Throw New InvalidOperationException("Should have thrown for empty method")
            Catch ex As InvalidOperationException
                Assert(ex.Message.Contains("Unknown") OrElse ex.Message.Contains("Method"), "Error should mention method")
            End Try
        End Sub

        Private Sub Test_H_Pipeline_CaseInsensitiveMethod()
            Dim v2 As New EngineConfigV2()
            v2.Video.Capture.Method = "DDAGRAB"  ' uppercase
            Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p.VideoBackend = "ffmpeg_ddagrab", "Uppercase method should resolve to ffmpeg_ddagrab, got " & p.VideoBackend)
        End Sub

        Private Sub Test_H_Pipeline_AudioDeterministic()
            Dim v2 As New EngineConfigV2()
            v2.Audio.System.Enabled = True
            v2.Audio.Microphone.Enabled = False
            Dim p1 As PipelineConfig = PipelineResolver.Resolve(v2)
            Dim p2 As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p1.AudioBackend = p2.AudioBackend, "AudioBackend must be deterministic across resolves")
            Assert(p1.AudioBackend = "wasapi_loopback", "Should be wasapi_loopback")
        End Sub

        Private Sub Test_H_Pipeline_DxgiExplicitThrow()
            Dim v2 As New EngineConfigV2()
            v2.Experimental.EnableD3D11Interop = True
            v2.Video.Capture.Method = "ddagrab"

            Dim p As PipelineConfig = PipelineResolver.Resolve(v2)
            Assert(p.VideoBackend = "dxgi", "Should mark as dxgi intent")

            Try
                PipelineResolver.BuildFFmpegCommandBuilder(v2)
                Throw New InvalidOperationException("Should have thrown NotImplementedException")
            Catch ex As NotImplementedException
                Assert(ex.Message.Contains("DXGI"), "Should mention DXGI in error: " & ex.Message)
            End Try
        End Sub

        ' ── V1/V2 determinism tests (Phase 5) ──

        Private Sub Test_H_V1_Determinism10Builds()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v1Settings As New FFmpegCommandBuilderV1.V1Settings() With {
                .Encoder = "h264_nvenc",
                .FPS = v1.FPS,
                .Bitrate = v1.BitrateBps,
                .NvencPreset = v1.NvencPreset,
                .CaptureMethod = v1.CaptureMethod,
                .UseNativeResolution = v1.UseNativeResolution,
                .PixelFormat = v1.PixelFormat
            }
            Dim builder As New FFmpegCommandBuilderV1(v1Settings)

            Dim first As String = builder.Build("out.mp4")
            For i As Integer = 2 To 10
                Dim current As String = builder.Build("out.mp4")
                Assert(current = first, "V1 build #" & i & " differs from build #1 — not deterministic")
            Next
        End Sub

        Private Sub Test_H_V2_Determinism10Builds()
            Dim v2 As New EngineConfigV2()
            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)

            Dim first As String = builder.Build("out.mp4")
            For i As Integer = 2 To 10
                Dim current As String = builder.Build("out.mp4")
                Assert(current = first, "V2 build #" & i & " differs from build #1 — not deterministic")
            Next
        End Sub

        Private Sub Test_H_V1V2_ByteIdenticalDelta()
            ' Verify V1 and V2 are byte-identical except for exactly 2 documented diffs:
            '   1. bufsize (V1=1× bitrate, V2=2× bitrate)
            '   2. -pix_fmt (V1 omits, V2 appends for ddagrab+NVENC)
            '
            ' APPROACH: Instead of positional token comparison (which breaks when
            ' V2 inserts extra tokens, shifting all subsequent positions), we use
            ' a normalization-based comparison:
            '   a. Tokenize both commands
            '   b. In V1's token list, replace bufsize value "20000000" → "40000000"
            '      (normalize the documented bufsize fix)
            '   c. In V2's token list, remove the 2 extra tokens "-pix_fmt" and "nv12"
            '      (normalize the documented pix_fmt addition)
            '   d. Now V1 and V2 should be byte-identical token lists
            ' This proves the ONLY differences are the 2 documented ones.
            Dim v1Cmd As String = BuildDefaultV1Command("out.mp4")

            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v2 As EngineConfigV2 = ConfigMigrator.MigrateFromV1(v1)
            Dim v2Cmd As String = PipelineResolver.BuildFFmpegCommandBuilder(v2).Build("out.mp4")

            ' Tokenize both commands into space-separated tokens (filter empties)
            Dim v1Tokens As New List(Of String)()
            For Each t As String In v1Cmd.Split(" "c)
                If t.Length > 0 Then v1Tokens.Add(t)
            Next
            Dim v2Tokens As New List(Of String)()
            For Each t As String In v2Cmd.Split(" "c)
                If t.Length > 0 Then v2Tokens.Add(t)
            Next

            ' Step 1: Normalize V1 bufsize "20000000" → "40000000"
            '         (V1 uses 1× bitrate for bufsize; V2 uses 2× bitrate — documented fix)
            Dim v1BufsizeIndex As Integer = v1Tokens.IndexOf("-bufsize")
            Assert(v1BufsizeIndex >= 0, "V1 must contain -bufsize token")
            Assert(v1Tokens(v1BufsizeIndex + 1) = "20000000",
                   "V1 bufsize value should be 20000000 (1× bitrate), got " & v1Tokens(v1BufsizeIndex + 1))
            v1Tokens(v1BufsizeIndex + 1) = "40000000"

            ' Step 2: Remove V2's extra -pix_fmt nv12 tokens
            '         (V2 appends -pix_fmt nv12 for ddagrab+NVENC — documented fix)
            Dim v2PixfmtIndex As Integer = v2Tokens.IndexOf("-pix_fmt")
            Assert(v2PixfmtIndex >= 0, "V2 must contain -pix_fmt token")
            Assert(v2Tokens(v2PixfmtIndex + 1) = "nv12",
                   "V2 pix_fmt value should be nv12, got " & v2Tokens(v2PixfmtIndex + 1))
            v2Tokens.RemoveAt(v2PixfmtIndex + 1)  ' remove "nv12" first (higher index)
            v2Tokens.RemoveAt(v2PixfmtIndex)     ' then remove "-pix_fmt"

            ' Step 3: Now V1 and V2 should be identical token lists
            Assert(v1Tokens.Count = v2Tokens.Count,
                   $"After normalization, V1 has {v1Tokens.Count} tokens but V2 has {v2Tokens.Count}. " &
                   $"V1=[{String.Join(", ", v1Tokens)}] V2=[{String.Join(", ", v2Tokens)}]")

            For i As Integer = 0 To v1Tokens.Count - 1
                Assert(v1Tokens(i) = v2Tokens(i),
                       $"Token {i} differs after normalization: V1='{v1Tokens(i)}' V2='{v2Tokens(i)}'. " &
                       $"This means V1 and V2 differ in ways OTHER than the 2 documented changes " &
                       $"(bufsize + pix_fmt). V1=[{String.Join(", ", v1Tokens)}] V2=[{String.Join(", ", v2Tokens)}]")
            Next
        End Sub

        Private Sub Test_H_V2_RejectEmptyPath()
            Dim v2 As New EngineConfigV2()
            Dim builder As IFFmpegCommandBuilder = PipelineResolver.BuildFFmpegCommandBuilder(v2)
            Try
                builder.Build("")
                Throw New InvalidOperationException("Should have thrown for empty path")
            Catch ex As ArgumentException
                Assert(ex.Message.Contains("outputFile"), "Should mention outputFile in error")
            End Try
        End Sub

        Private Sub Test_H_V1_RejectEmptyPath()
            Dim v1 As New ConfigMigrator.V1CaptureSettings()
            Dim v1Settings As New FFmpegCommandBuilderV1.V1Settings() With {
                .Encoder = "h264_nvenc",
                .FPS = v1.FPS,
                .Bitrate = v1.BitrateBps,
                .NvencPreset = v1.NvencPreset,
                .CaptureMethod = v1.CaptureMethod,
                .UseNativeResolution = v1.UseNativeResolution,
                .PixelFormat = v1.PixelFormat
            }
            Dim builder As New FFmpegCommandBuilderV1(v1Settings)
            Try
                builder.Build("")
                Throw New InvalidOperationException("Should have thrown for empty path")
            Catch ex As ArgumentException
                Assert(ex.Message.Contains("outputFile"), "Should mention outputFile in error")
            End Try
        End Sub

        Private Sub Test_H_V2_DefaultConfigValidates()
            Dim cfg As New EngineConfigV2()
            Dim errors As IReadOnlyList(Of String) = ConfigValidator.Validate(cfg)
            Assert(errors.Count = 0, "Default V2 config should validate cleanly. Errors: " & String.Join("; ", errors))
        End Sub

        ' ── IVideoBackend compile-time contract tests (Phase 6 — BC30183 regression) ──
        '
        ' These tests prove the IVideoBackend interface compiles. If the interface
        ' signature has a VB.NET keyword conflict (like the original Sub Stop()
        ' that triggered BC30183), the StubVideoBackend class below will fail to
        ' compile, which fails the entire test project's build (not just one test).
        '
        ' Additionally, H6.1 + H6.2 use reflection to verify the interface surface
        ' so the tests can give a clear failure message if a method disappears.

        ''' <summary>
        ''' Stub implementation of IVideoBackend. EXISTS ONLY to prove the interface
        ''' compiles. If IVideoBackend has a keyword-as-identifier error (BC30183),
        ''' this class will not compile and the whole project build fails.
        ''' </summary>
        Private NotInheritable Class StubVideoBackend
            Implements IVideoBackend

            Private _state As VideoBackendState = VideoBackendState.Created
            Private _disposed As Boolean = False

            Public ReadOnly Property CurrentState As VideoBackendState Implements IVideoBackend.CurrentState
                Get
                    Return _state
                End Get
            End Property

            Public Sub Start() Implements IVideoBackend.Start
                _state = VideoBackendState.Running
            End Sub

            ' NOTE: implements [Stop] — VB.NET requires bracket-escape at the
            ' interface declaration AND at the implementation site. The
            ' Implements clause uses the bracketed name.
            ' (Earlier comment incorrectly said brackets were not needed at
            ' the implementation site — that was wrong. BC30183 fires on
            ' `Sub Stop()` regardless of context because 'Stop' is a
            ' reserved keyword.)
            Public Sub [Stop]() Implements IVideoBackend.Stop
                _state = VideoBackendState.Stopped
            End Sub

            Public Function GetFrame() As VideoFrame Implements IVideoBackend.GetFrame
                Return Nothing  ' stub — no frames
            End Function

            Public Function GetDiagnostics() As IReadOnlyDictionary(Of String, Long) Implements IVideoBackend.GetDiagnostics
                Return New Dictionary(Of String, Long)()
            End Function

            Public Sub Dispose() Implements IDisposable.Dispose
                If _disposed Then Return
                _disposed = True
                _state = VideoBackendState.Disposed
            End Sub
        End Class

        Private Sub Test_H_IVideoBackend_StopMethodCompiles()
            ' PROOF 1: StubVideoBackend compiles (if this test runs, the class compiled).
            '          If IVideoBackend.Stop is broken, the project won't build.
            Dim backend As New StubVideoBackend()
            Assert(backend IsNot Nothing, "StubVideoBackend should instantiate")

            ' PROOF 2: Start + Stop + Dispose all callable
            Assert(backend.CurrentState = VideoBackendState.Created, "Initial state should be Created")
            backend.Start()
            Assert(backend.CurrentState = VideoBackendState.Running, "After Start: Running")
            backend.Stop()
            Assert(backend.CurrentState = VideoBackendState.Stopped, "After Stop: Stopped")
            backend.Dispose()
            Assert(backend.CurrentState = VideoBackendState.Disposed, "After Dispose: Disposed")

            ' PROOF 3: Reflection check — IVideoBackend interface must have a method named "Stop"
            ' (the bracketed identifier [Stop] is exposed as plain "Stop" in reflection metadata).
            Dim stopMethod As MethodInfo = GetType(IVideoBackend).GetMethod("Stop")
            Assert(stopMethod IsNot Nothing,
                   "IVideoBackend must have a method named 'Stop' (bracketed identifier [Stop] should " &
                   "appear as 'Stop' in reflection). If this fails, the bracket escape was lost.")
            Assert(stopMethod.ReturnType Is GetType(Void), "Stop() must return Void")
        End Sub

        Private Sub Test_H_IVideoBackend_FullSurface()
            ' Verify the complete IVideoBackend contract surface via reflection.
            ' If any member is missing or renamed incorrectly, this test fails
            ' with a clear message instead of a confusing BC30183 build error.
            Dim t As Type = GetType(IVideoBackend)

            ' Required methods
            Assert(t.GetMethod("Start") IsNot Nothing, "IVideoBackend must have Start()")
            Assert(t.GetMethod("Stop") IsNot Nothing, "IVideoBackend must have Stop() — bracketed as [Stop] in source")
            Assert(t.GetMethod("GetFrame") IsNot Nothing, "IVideoBackend must have GetFrame()")
            Assert(t.GetMethod("GetDiagnostics") IsNot Nothing, "IVideoBackend must have GetDiagnostics()")

            ' Required property
            Dim stateProp As PropertyInfo = t.GetProperty("CurrentState")
            Assert(stateProp IsNot Nothing, "IVideoBackend must have CurrentState property")
            Assert(stateProp.PropertyType Is GetType(VideoBackendState),
                   "CurrentState must be of type VideoBackendState, got " & stateProp.PropertyType.Name)

            ' Required base interface (IDisposable → must have Dispose)
            Assert(GetType(IDisposable).IsAssignableFrom(t),
                   "IVideoBackend must inherit IDisposable (so Dispose is part of contract)")

            ' GetFrame return type
            Dim getFrameMethod As MethodInfo = t.GetMethod("GetFrame")
            Assert(getFrameMethod.ReturnType Is GetType(VideoFrame),
                   "GetFrame must return VideoFrame, got " & getFrameMethod.ReturnType.Name)

            ' GetDiagnostics return type
            Dim diagMethod As MethodInfo = t.GetMethod("GetDiagnostics")
            Assert(diagMethod.ReturnType Is GetType(IReadOnlyDictionary(Of String, Long)),
                   "GetDiagnostics must return IReadOnlyDictionary(Of String, Long), got " & diagMethod.ReturnType.Name)
        End Sub

        ' ── ConfigLoader name-resolution regression tests (BC30456 + BC42104) ──
        '
        ' The original code declared a local variable named 'path' which
        ' shadowed the System.IO.Path type. VB.NET's case-insensitive name
        ' resolution then interpreted 'Path.Combine(...)' as a method call
        ' on the local String variable → BC30456 "Combine is not a member
        ' of String" + BC42104 "Variable used before assigned".
        '
        ' The fix renamed the local variable from 'path' to 'filePath'.
        ' These tests prove LoadDefault + SaveDefault resolve
        ' System.IO.Path.Combine correctly (the methods must not throw
        ' MissingMethodException at runtime — though the failure was at
        ' compile time, a reflection check also verifies the call resolves
        ' to the correct type).

        Private Sub Test_H_ConfigLoader_LoadDefaultResolvesPath()
            ' PROOF 1: LoadDefault + SaveDefault compile (if this test runs,
            '          the project built successfully, proving the local
            '          variable rename fixed BC30456).
            '
            ' PROOF 2: Verify System.IO.Path type is resolvable from inside
            '          the ConfigLoader namespace — if Imports System.IO is
            '          accidentally removed, this throws CompilationException
            '          (cannot test compile errors at runtime, but we can
            '          verify the type is reachable).
            Dim pathType As Type = GetType(System.IO.Path)
            Assert(pathType IsNot Nothing, "System.IO.Path type must be reachable")
            Assert(pathType.Name = "Path", "Type name should be 'Path', got " & pathType.Name)

            ' PROOF 3: Verify Path.Combine method exists with the expected signature
            Dim combineMethod As MethodInfo = pathType.GetMethod("Combine", New Type() {GetType(String), GetType(String)})
            Assert(combineMethod IsNot Nothing,
                   "System.IO.Path.Combine(String, String) must exist. " &
                   "If this fails, the Imports System.IO was lost.")
            Assert(combineMethod.ReturnType Is GetType(String),
                   "Path.Combine must return String, got " & combineMethod.ReturnType.Name)

            ' PROOF 4: Actually invoke LoadDefault with a temp directory to verify
            ' it doesn't throw MissingMethodException or similar runtime failure
            ' (the original bug was compile-time, but this proves the method
            ' actually works end-to-end after the fix).
            Dim tempDir As String = Path.Combine(Path.GetTempPath(), "configloader_test_" & Guid.NewGuid().ToString("N"))
            Try
                Directory.CreateDirectory(tempDir)
                Dim cfg As New EngineConfigV2()
                ConfigLoader.SaveDefault(cfg, tempDir)
                Dim expectedFile As String = Path.Combine(tempDir, "engine-config.v2.json")
                Assert(File.Exists(expectedFile), "SaveDefault should write to <appBaseDir>/engine-config.v2.json")

                Dim loaded As EngineConfigV2 = ConfigLoader.LoadDefault(tempDir)
                Assert(loaded IsNot Nothing, "LoadDefault should read back the saved config")
                Assert(loaded.Version = EngineConfigV2.SchemaVersion, "Loaded Version should match schema")
            Finally
                If Directory.Exists(tempDir) Then Directory.Delete(tempDir, recursive:=True)
            End Try
        End Sub

        Private Sub Test_H_ConfigLoader_SaveDefaultResolvesPath()
            ' SaveDefault-specific regression test (separate from LoadDefault
            ' because they're independent methods — a fix to one must NOT
            ' mask a regression in the other).
            '
            ' SaveDefault internally calls:
            '   Path.Combine(appBaseDir, DefaultFileName)  ← was 'path' local
            '   Save(cfg, filePath)                       ← now 'filePath' local
            '
            ' If the local variable rename is reverted in SaveDefault but not
            ' LoadDefault, this test will fail (BC30456 prevents build).

            Dim tempDir As String = Path.Combine(Path.GetTempPath(), "configloader_save_test_" & Guid.NewGuid().ToString("N"))
            Try
                Directory.CreateDirectory(tempDir)
                Dim cfg As New EngineConfigV2()
                cfg.Video.Encoder.BitrateBps = 25000000L  ' non-default to verify round-trip

                ' If Path.Combine is shadowed by a local variable, this would
                ' throw at runtime (or fail to compile). Calling SaveDefault
                ' exercises the renamed local 'filePath'.
                ConfigLoader.SaveDefault(cfg, tempDir)

                Dim expectedFile As String = Path.Combine(tempDir, "engine-config.v2.json")
                Assert(File.Exists(expectedFile), "SaveDefault should create the file")

                ' Verify file actually contains valid JSON (not garbage from a
                ' broken path concatenation)
                Dim fileContent As String = File.ReadAllText(expectedFile)
                Assert(fileContent.Length > 0, "Saved file should be non-empty")
                Assert(fileContent.Contains("""Version"""), "Saved JSON should have Version field")
                Assert(fileContent.Contains("""BitrateBps"""), "Saved JSON should have BitrateBps field")
                Assert(fileContent.Contains("25000000"), "Saved JSON should have the non-default bitrate we set")
            Finally
                If Directory.Exists(tempDir) Then Directory.Delete(tempDir, recursive:=True)
            End Try
        End Sub
    End Module
End Namespace
