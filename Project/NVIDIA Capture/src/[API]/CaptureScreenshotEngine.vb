Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports DulukaEngine.Video.Backends.Ddagrab

' Screenshot is a one-frame display capture, completely independent of
' RecordingEngine and NVENC. No audio sources are opened. The source
' monitor comes from CaptureDisplayCatalog, not a guessed primary screen.
Public NotInheritable Class CaptureScreenshotEngine
    Private Sub New()
    End Sub

    Public NotInheritable Class Outcome
        Public Property Saved As Boolean
        Public Property File As String = ""
        Public Property ErrorCode As String = ""
        Public Property DisplayIndex As Integer = -1
    End Class

    Public Shared Function BuildArguments(outputIndex As Integer, pathValue As String) As String()
        If outputIndex < 0 OrElse outputIndex > 31 Then
            Throw New ArgumentOutOfRangeException(NameOf(outputIndex))
        End If
        If Not String.Equals(Path.GetExtension(pathValue), ".png",
                             StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("png_output_required")
        End If
        Return {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i",
            $"ddagrab=output_idx={outputIndex}:draw_mouse=1:framerate=1:dup_frames=0",
            "-vf", "hwdownload,format=bgra",
            "-frames:v", "1", "-an", "-c:v", "png",
            "-y", pathValue
        }
    End Function

    Public Shared Function HasCompletePngSignature(bytes As Byte()) As Boolean
        If bytes Is Nothing OrElse bytes.Length < 45 Then Return False
        Dim png = New Byte() {137, 80, 78, 71, 13, 10, 26, 10}
        If Not png.SequenceEqual(bytes.Take(8)) Then Return False
        ' Verify IHDR dimensions and that the final IEND chunk is present.
        ' This is structural verification, not a full PNG decode or CRC check.
        Dim trailer = New Byte() {0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130}
        Return bytes(12) = 73 AndAlso bytes(13) = 72 AndAlso
               bytes(14) = 68 AndAlso bytes(15) = 82 AndAlso
               (bytes(16) <> 0 OrElse bytes(17) <> 0 OrElse bytes(18) <> 0 OrElse bytes(19) <> 0) AndAlso
               (bytes(20) <> 0 OrElse bytes(21) <> 0 OrElse bytes(22) <> 0 OrElse bytes(23) <> 0) AndAlso
               trailer.SequenceEqual(bytes.Skip(bytes.Length - trailer.Length))
    End Function

    Public Shared Function TakeOne(display As CaptureDisplayCatalog.Display,
                                   ffmpegPath As String) As Outcome
        Dim result As New Outcome()
        If display Is Nothing Then
            result.ErrorCode = "capture_display_unavailable"
            Return result
        End If
        result.DisplayIndex = display.OutputIndex
        If String.IsNullOrWhiteSpace(ffmpegPath) OrElse Not IO.File.Exists(ffmpegPath) Then
            result.ErrorCode = "screenshot_ffmpeg_unavailable"
            Return result
        End If
        Dim dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "ShadowPlay NEXT", "Screenshots")
        Dim filename = "Screenshot_" & DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") &
                       "_" & Guid.NewGuid().ToString("N").Substring(0, 8) & ".png"
        Dim destination = Path.Combine(dir, filename)
        Dim tempfile = Path.Combine(dir, "pending_" & filename)
        Try
            Directory.CreateDirectory(dir)
            Dim psi As New ProcessStartInfo(ffmpegPath) With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            Dim binding = DxgiDisplayInventory.CaptureDeviceArguments(display.Target, DxgiDisplayInventory.Collect())
            For Each arg In binding.Concat(BuildArguments(display.OutputIndex, tempfile))
                psi.ArgumentList.Add(arg)
            Next
            Using child = Process.Start(psi)
                If child Is Nothing Then
                    result.ErrorCode = "screenshot_encoder_spawn_failed"
                    Return result
                End If
                Dim errTask = child.StandardError.ReadToEndAsync()
                Dim outTask = child.StandardOutput.ReadToEndAsync()
                If Not child.WaitForExit(10000) Then
                    Try
                        child.Kill(True)
                        child.WaitForExit(2000)
                    Catch
                    End Try
                    result.ErrorCode = "screenshot_capture_timeout"
                    Return result
                End If
                Dim errorText = errTask.GetAwaiter().GetResult()
                outTask.GetAwaiter().GetResult()
                If child.ExitCode <> 0 Then
                    ' DXGI may refuse exclusive fullscreen, protected content,
                    ' or output duplication after a display mode transition.
                    result.ErrorCode = If(errorText.Contains("ACCESS_LOST", StringComparison.OrdinalIgnoreCase),
                                          "screenshot_dxgi_access_lost", "screenshot_dxgi_failed")
                    Return result
                End If
            End Using
            If Not IO.File.Exists(tempfile) OrElse New FileInfo(tempfile).Length < 100 Then
                result.ErrorCode = "screenshot_empty_frame"
                Return result
            End If
            Dim bytes As Byte() = IO.File.ReadAllBytes(tempfile)
            If Not HasCompletePngSignature(bytes) Then
                result.ErrorCode = "screenshot_invalid_png"
                Return result
            End If
            IO.File.Move(tempfile, destination)
            result.Saved = True
            result.File = destination
            Return result
        Catch
            result.ErrorCode = "screenshot_io_or_capture_error"
            Return result
        Finally
            Try
                If IO.File.Exists(tempfile) Then IO.File.Delete(tempfile)
            Catch
            End Try
        End Try
    End Function
End Class
