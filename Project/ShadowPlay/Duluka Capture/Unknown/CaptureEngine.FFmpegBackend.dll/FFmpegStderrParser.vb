Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Globalization

Namespace CaptureEngine.FFmpegBackend
    ''' <summary>
    ''' Parses FFmpeg stderr output lines into structured diagnostics.
    '''
    ''' P1-C Commit 3: Full implementation.
    '''
    ''' Parses lines like:
    '''   frame= 6540 fps=143 q=8.0 size= 94278KiB time=00:00:45.41 bitrate=17005.4kbits/s dup=760 drop=1 speed=0.997x
    '''   [error] Could not open output file
    '''   Lsize= 94300KiB time=00:00:60.00 bitrate=12870.0kbits/s speed=1.01x
    '''
    ''' Thread safety:
    '''   - ProcessLine is called from Process.ErrorDataReceived (threadpool thread).
    '''   - GetSnapshot / HasError / LastError may be called from any thread.
    '''   - All mutations use SyncLock on _sync.
    ''' </summary>
    Public NotInheritable Class FFmpegStderrParser
        Private ReadOnly _sync As New Object()

        ' Parsed stats (updated on each stderr line)
        Private _frameCount As Long = 0
        Private _fps As Double = 0
        Private _dupCount As Long = 0
        Private _dropCount As Long = 0
        Private _speed As Double = 0
        Private _lastSizeBytes As Long = 0
        Private _lastError As String = ""
        Private _hasError As Boolean = False

        ' Error detection keywords (case-insensitive substring match)
        Private Shared ReadOnly ErrorKeywords As String() = {
            "[error]", "conversion failed", "could not open",
            "no such file or directory", "invalid argument",
            "device not found", "unknown encoder",
            "not currently supported in output",
            "av_interleaved_write_header", "no space left on device",
            "errno 28", "disk full"
        }

        ''' <summary>
        ''' Process a single stderr line from FFmpeg.
        ''' Thread-safe — uses SyncLock on all mutations.
        ''' Never throws — parsing errors are silently ignored (partial parse is OK).
        ''' </summary>
        Public Sub ProcessLine(line As String)
            If String.IsNullOrEmpty(line) Then Return

            Try
                Dim low As String = line.ToLowerInvariant()

                ' ── Error detection ──
                If DetectError(line, low) Then
                    SyncLock _sync
                        _hasError = True
                        _lastError = line.Trim()
                    End SyncLock
                    Return
                End If

                ' ── Progress line parsing ──
                ' FFmpeg emits two kinds of progress lines:
                '   1. "frame= 6540 fps=143 q=8.0 size= 94278KiB ..."
                '   2. "Lsize= 94300KiB time=00:00:60.00 ..."  (L = final/last)
                ' Both have the same field format — only the prefix differs.

                If low.Contains("frame=") OrElse low.Contains("lsize=") Then
                    ParseProgressLine(line, low)
                End If

            Catch
                ' Swallow all parsing errors — FFmpeg stderr format varies
                ' across versions and we must not crash the stderr thread.
            End Try
        End Sub

        ''' <summary>
        ''' Get a snapshot of all parsed stats (thread-safe, never throws).
        ''' Returns a dictionary with keys: frame, fps, dup, drop, speed, size_bytes, error_count.
        ''' speed is stored as speed × 1000 (integer; e.g. 997 = 0.997×).
        ''' </summary>
        Public Function GetSnapshot() As IReadOnlyDictionary(Of String, Long)
            SyncLock _sync
                Return New Dictionary(Of String, Long) From {
                    {"frame", _frameCount},
                    {"fps", CLng(_fps)},
                    {"dup", _dupCount},
                    {"drop", _dropCount},
                    {"speed", CLng(_speed * 1000)},
                    {"size_bytes", _lastSizeBytes}
                }
            End SyncLock
        End Function

        ''' <summary>True if an [error] line was detected.</summary>
        Public ReadOnly Property HasError As Boolean
            Get
                SyncLock _sync
                    Return _hasError
                End SyncLock
            End Get
        End Property

        ''' <summary>Last error message from stderr (empty if no error).</summary>
        Public ReadOnly Property LastError As String
            Get
                SyncLock _sync
                    Return _lastError
                End SyncLock
            End Get
        End Property

        ' ===== Private parsing helpers =====

        ''' <summary>
        ''' Detect if a stderr line is an error.
        ''' Uses case-insensitive substring matching against ErrorKeywords.
        ''' </summary>
        Private Shared Function DetectError(line As String, low As String) As Boolean
            ' Fast check: lines starting with "Error" (without bracket)
            If low.StartsWith("error ") OrElse low.StartsWith("error:") Then
                Return True
            End If

            ' Check against known error keywords
            For Each kw As String In ErrorKeywords
                If low.Contains(kw) Then
                    Return True
                End If
            Next

            Return False
        End Function

        ''' <summary>
        ''' Parse a progress line containing frame=/fps=/dup=/drop=/speed=/size=.
        ''' Each field is optional — we parse what's present and skip what's missing.
        ''' </summary>
        Private Sub ParseProgressLine(line As String, low As String)
            Dim frame As Long = 0
            Dim fps As Double = 0
            Dim dup As Long = 0
            Dim drop As Long = 0
            Dim speed As Double = 0
            Dim sizeBytes As Long = 0

            ' frame= or Lsize= line (Lsize lines don't have frame= but we still parse other fields)
            ParseLongField(low, "frame=", frame)
            ParseDoubleField(low, "fps=", fps)
            ParseLongField(low, "dup=", dup)
            ParseLongField(low, "drop=", drop)
            ParseDoubleField(low, "speed=", speed)
            ParseSizeField(low, "size=", sizeBytes)

            ' Update state under lock
            SyncLock _sync
                If frame > 0 Then _frameCount = frame
                If fps > 0 Then _fps = fps
                If dup > 0 Then _dupCount = dup
                If drop > 0 Then _dropCount = drop
                If speed > 0 Then _speed = speed
                If sizeBytes > 0 Then _lastSizeBytes = sizeBytes
            End SyncLock
        End Sub

        ''' <summary>
        ''' Parse a long integer field from a stderr line.
        ''' Example: "frame= 6540" → extracts 6540.
        ''' </summary>
        Private Shared Sub ParseLongField(low As String, key As String, ByRef result As Long)
            Dim idx As Integer = low.IndexOf(key)
            If idx < 0 Then Return

            ' Skip past the key
            idx += key.Length

            ' Skip leading whitespace
            Do While idx < low.Length AndAlso low(idx) = " "c
                idx += 1
            Loop

            ' Read digits
            Dim sb As New System.Text.StringBuilder()
            Do While idx < low.Length AndAlso (Char.IsDigit(low(idx)) OrElse low(idx) = "-"c)
                sb.Append(low(idx))
                idx += 1
            Loop

            If sb.Length > 0 Then
                Long.TryParse(sb.ToString(), result)
            End If
        End Sub

        ''' <summary>
        ''' Parse a double field from a stderr line.
        ''' Example: "fps=143" → extracts 143.0
        ''' Example: "speed=0.997x" → extracts 0.997 (strips trailing 'x')
        ''' </summary>
        Private Shared Sub ParseDoubleField(low As String, key As String, ByRef result As Double)
            Dim idx As Integer = low.IndexOf(key)
            If idx < 0 Then Return

            idx += key.Length

            ' Skip leading whitespace
            Do While idx < low.Length AndAlso low(idx) = " "c
                idx += 1
            Loop

            ' Read number (digits, '.', '-')
            Dim sb As New System.Text.StringBuilder()
            Do While idx < low.Length AndAlso (Char.IsDigit(low(idx)) OrElse low(idx) = "."c OrElse low(idx) = "-"c)
                sb.Append(low(idx))
                idx += 1
            Loop

            If sb.Length > 0 Then
                Double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, result)
            End If
        End Sub

        ''' <summary>
        ''' Parse a size field from a stderr line.
        ''' Examples: "size= 94278KiB" → 96572672 bytes
        '''            "size= 1234kB" → 1234000 bytes
        '''            "size= 56B" → 56 bytes
        ''' </summary>
        Private Shared Sub ParseSizeField(low As String, key As String, ByRef result As Long)
            Dim idx As Integer = low.IndexOf(key)
            If idx < 0 Then Return

            idx += key.Length

            ' Skip leading whitespace
            Do While idx < low.Length AndAlso low(idx) = " "c
                idx += 1
            Loop

            ' Read number part
            Dim numStr As New System.Text.StringBuilder()
            Do While idx < low.Length AndAlso (Char.IsDigit(low(idx)) OrElse low(idx) = "."c)
                numStr.Append(low(idx))
                idx += 1
            Loop

            If numStr.Length = 0 Then Return

            ' Read unit part (non-digit characters until whitespace or end)
            Dim unitStr As New System.Text.StringBuilder()
            Do While idx < low.Length AndAlso Not Char.IsWhiteSpace(low(idx))
                unitStr.Append(low(idx))
                idx += 1
            Loop

            Dim num As Double = 0
            If Not Double.TryParse(numStr.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, num) Then
                Return
            End If

            Dim unit As String = unitStr.ToString().Trim().ToUpperInvariant()
            Select Case unit
                Case "B"
                    result = CLng(num)
                Case "KB", "KIB"
                    result = CLng(num * 1024)
                Case "MB", "MIB"
                    result = CLng(num * 1024 * 1024)
                Case "GB", "GIB"
                    result = CLng(num * 1024 * 1024 * 1024)
                Case Else
                    ' No unit or unknown unit — assume bytes
                    result = CLng(num)
            End Select
        End Sub
    End Class
End Namespace
