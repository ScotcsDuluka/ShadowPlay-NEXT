Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Runtime.Loader
Imports System.Text

Module Program
    Private _passed As Integer
    Private _assemblyDir As String = ""

    Private Function ResolveDependency(context As AssemblyLoadContext,
                                       requested As AssemblyName) As Assembly
        Dim candidate = IO.Path.Combine(_assemblyDir, requested.Name & ".dll")
        If IO.File.Exists(candidate) Then Return context.LoadFromAssemblyPath(candidate)
        Return Nothing
    End Function

    Private Sub Check(name As String, success As Boolean)
        If Not success Then Throw New Exception("FAIL: " & name)
        _passed += 1
        Console.WriteLine("PASS: " & name)
    End Sub

    Private Sub RunProcess(exe As String, args As IEnumerable(Of String))
        Dim psi As New ProcessStartInfo(exe) With {
            .UseShellExecute = False, .CreateNoWindow = True,
            .RedirectStandardOutput = True, .RedirectStandardError = True
        }
        For Each arg In args
            psi.ArgumentList.Add(arg)
        Next
        RunProcess(psi)
    End Sub

    Private Sub RunProcess(psi As ProcessStartInfo)
        Using child As Process = System.Diagnostics.Process.Start(psi)
            If child Is Nothing Then Throw New Exception("Failed to launch: " & psi.FileName)
            Dim errors = child.StandardError.ReadToEndAsync()
            Dim output = child.StandardOutput.ReadToEndAsync()
            If Not child.WaitForExit(30000) Then
                Try : child.Kill(True) : Catch : End Try
                Throw New Exception("Timed out: " & psi.FileName)
            End If
            Dim errorText = errors.GetAwaiter().GetResult()
            output.GetAwaiter().GetResult()
            If child.ExitCode <> 0 Then
                Throw New Exception("FFmpeg failed: " & child.ExitCode & " " &
                    errorText.Substring(0, Math.Min(1800, errorText.Length)))
            End If
        End Using
    End Sub

    Private Sub Generate(ffmpeg As String, folder As String)
        RunProcess(ffmpeg, {"-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "testsrc2=size=320x180:rate=30", "-t", "2",
            "-an", "-c:v", "libx264", "-threads", "1",
            "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-y", IO.Path.Combine(folder, "video.mp4")})
        RunProcess(ffmpeg, {"-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "sine=frequency=440:sample_rate=48000", "-t", "1.3",
            "-ac", "2", "-c:a", "pcm_s16le", "-y",
            IO.Path.Combine(folder, "system.wav")})
        RunProcess(ffmpeg, {"-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "sine=frequency=880:sample_rate=48000", "-t", "1.7",
            "-ac", "1", "-c:a", "pcm_s16le", "-y",
            IO.Path.Combine(folder, "mic.wav")})
    End Sub

    Private Function MuxArgs(engineType As Type, settingsType As Type,
                              separate As Boolean,
                              folder As String, hasSystem As Boolean,
                              hasMic As Boolean,
                              sysOffset As Double, micOffset As Double,
                              output As String) As String
        Dim settings = Activator.CreateInstance(settingsType)
        Dim trackProperty = settingsType.GetProperty("AudioTrackMode")
        trackProperty.SetValue(settings, [Enum].ToObject(trackProperty.PropertyType, If(separate, 1, 0)))
        settingsType.GetProperty("SystemAudioVolume").SetValue(settings, CSng(0.8))
        settingsType.GetProperty("MicVolume").SetValue(settings, CSng(0.6))
        Dim instance = RuntimeHelpers.GetUninitializedObject(engineType)
        engineType.GetField("_settings", BindingFlags.NonPublic Or BindingFlags.Instance).
            SetValue(instance, settings)
        Dim method = engineType.GetMethod("BuildMuxArguments",
            BindingFlags.NonPublic Or BindingFlags.Instance)
        If method Is Nothing Then Throw New Exception("Real engine BuildMuxArguments not found")
        Return CStr(method.Invoke(instance, New Object() {
            IO.Path.Combine(folder, "video.mp4"),
            IO.Path.Combine(folder, "system.wav"), hasSystem,
            IO.Path.Combine(folder, "mic.wav"), hasMic,
            output, sysOffset, micOffset, 2.0R
        }))
    End Function

    Private Sub OneCase(engineType As Type, settingsType As Type,
                        ffmpeg As String, ffprobe As String, folder As String,
                        name As String, hasSys As Boolean, hasMic As Boolean,
                        separate As Boolean, expectedAudio As Integer,
                        sysOffset As Double, micOffset As Double)
        Dim output = IO.Path.Combine(folder, name & ".mp4")
        Dim commandLine As String = MuxArgs(engineType, settingsType, separate, folder,
                                             hasSys, hasMic, sysOffset, micOffset, output)
        Check(name & " args map video", commandLine.Contains("-map 0:v", StringComparison.Ordinal))
        If separate AndAlso hasSys AndAlso hasMic Then
            Check(name & " separate streams configured",
                  commandLine.Contains("-c:a:1 aac", StringComparison.Ordinal))
        End If
        Dim psi As New ProcessStartInfo(ffmpeg) With {
            .Arguments = commandLine,
            .UseShellExecute = False, .CreateNoWindow = True,
            .RedirectStandardOutput = True, .RedirectStandardError = True
        }
        RunProcess(psi)
        Dim evidence = CaptureMediaInspector.Inspect(output, ffprobe)
        Check(name & " verified video and audio streams",
              evidence.Verified AndAlso evidence.VideoStreams = 1 AndAlso
              evidence.AudioStreams = expectedAudio)
        Check(name & " duration near 2 seconds",
              evidence.DurationSec.HasValue AndAlso
              Math.Abs(evidence.DurationSec.Value - 2.0) < 0.15)
        If expectedAudio > 0 Then
            Check(name & " audio codec AAC",
                  evidence.AudioCodecs.Length = expectedAudio AndAlso
                  Array.TrueForAll(evidence.AudioCodecs,
                    Function(codec) codec = "aac"))
        End If
    End Sub

    Sub Main(args As String())
        If args.Length <> 4 Then Throw New ArgumentException(
            "Usage: CaptureMux.Tests <NvCapture.dll> <ffmpeg.exe> <ffprobe.exe> <output_dir>")
        _assemblyDir = IO.Path.GetDirectoryName(IO.Path.GetFullPath(args(0)))
        AddHandler AssemblyLoadContext.Default.Resolving, AddressOf ResolveDependency
        Dim assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(IO.Path.GetFullPath(args(0)))
        Dim engineType = assembly.GetType("NVIDIA_Capture.LegacyCaptureEngine", True)
        Dim settingsType = assembly.GetType("NVIDIA_Capture.CaptureSettings", True)
        IO.Directory.CreateDirectory(args(3))
        Generate(args(1), args(3))
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "mute", False, False, False, 0, 0, 0)
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "system-only", True, False, False, 1, 0, 0)
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "mic-only", False, True, False, 1, 0, 0)
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "mixed", True, True, False, 1, 0, 0)
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "separate", True, True, True, 2, 0, 0)
        OneCase(engineType, settingsType, args(1), args(2), args(3),
                "offsets", True, True, True, 2, 0.20, -0.15)
        Console.WriteLine("PASS: " & _passed & " REAL engine mux regression checks")
    End Sub
End Module
