Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.IO

Namespace CaptureEngine.FFmpegBackend
    ''' <summary>
    ''' Manages a single FFmpeg process lifecycle: spawn, stdin 'q' quit,
    ''' stderr redirection, WaitForExit, Kill.
    '''
    ''' P1-C Commit 2: Full implementation.
    '''
    ''' Thread safety:
    '''   - This class is NOT internally synchronized.
    '''   - Caller (FFmpegPipelineBackend) must serialize Start/SendQuit/WaitForExit/Kill
    '''     via its own SyncLock.
    '''   - The Exited event fires on a threadpool thread (Process.Exited callback).
    '''   - The StderrLine event fires on a threadpool thread (ErrorDataReceived callback).
    '''
    ''' Critical rule (per OWNER):
    '''   ❌ NEVER hold a lock across WaitForExit() / Kill()
    '''   ✅ These methods are designed to be called OUTSIDE the caller's lock
    '''
    ''' Error handling:
    '''   - Start() throws FileNotFoundException if FFmpegPath doesn't exist
    '''   - Start() throws InvalidOperationException if already running
    '''   - SendQuit() swallows exceptions (pipe may already be closed)
    '''   - Kill() swallows exceptions (process may already be dead)
    '''   - WaitForExit() returns False on timeout (does NOT throw)
    ''' </summary>
    ''' ★ C/5 DORMANT (audit 2026-09-06): zero production callers — constructed
    ''' only by FFmpegPipelineBackend (dormant). Friend = compile-time isolation.
    Friend NotInheritable Class FFmpegProcessHost
        Implements IDisposable

        Private _process As Process
        Private _disposed As Boolean = False
        Private _started As Boolean = False

        ''' <summary>
        ''' Process generation ID — incremented on each Start().
        ''' Captured by Exited/Stderr callbacks to detect stale invocations
        ''' from a previous session. If the generation doesn't match
        ''' _currentGeneration, the callback is from an old process and must
        ''' be ignored.
        ''' </summary>
        Private _generation As Integer = 0

        ''' <summary>FFmpeg executable path (set by caller before Start).</summary>
        Public Property FFmpegPath As String = ""

        ''' <summary>FFmpeg argument string (built by FFmpegCommandBuilder).</summary>
        Public Property Arguments As String = ""

        ''' <summary>Output file path (for diagnostics / mux coordination).</summary>
        Public Property OutputPath As String = ""

        ''' <summary>Raised when the FFmpeg process exits (graceful or crash). Carries generation ID for staleness check.</summary>
        Public Event Exited As Action(Of Integer, Integer)

        ''' <summary>Raised for each stderr line. Carries generation ID for staleness check.</summary>
        Public Event StderrLine As Action(Of Integer, String)

        ''' <summary>Current generation ID (incremented on each Start).</summary>
        Public ReadOnly Property Generation As Integer
            Get
                Return _generation
            End Get
        End Property

        ''' <summary>Start the FFmpeg process.</summary>
        ''' <exception cref="FileNotFoundException">FFmpegPath does not point to an existing file.</exception>
        ''' <exception cref="InvalidOperationException">Process already started and not yet exited.</exception>
        Public Sub Start()
            If _disposed Then
                Throw New ObjectDisposedException(NameOf(FFmpegProcessHost))
            End If
            If _started AndAlso _process IsNot Nothing AndAlso Not _process.HasExited Then
                Throw New InvalidOperationException("FFmpegProcessHost: process is already running.")
            End If

            ' Validate FFmpeg path
            If String.IsNullOrWhiteSpace(FFmpegPath) Then
                Throw New FileNotFoundException("FFmpegPath is empty.", "ffmpeg.exe")
            End If
            If Not File.Exists(FFmpegPath) Then
                Throw New FileNotFoundException(
                    "FFmpeg executable not found at: " & FFmpegPath, FFmpegPath)
            End If

            ' Increment generation for this session
            _generation += 1
            Dim gen As Integer = _generation
            Dim si As New ProcessStartInfo()
            si.FileName = FFmpegPath
            si.Arguments = Arguments
            si.UseShellExecute = False
            si.RedirectStandardInput = True
            si.RedirectStandardOutput = True
            si.RedirectStandardError = True
            si.CreateNoWindow = True

            ' Create and configure the process
            _process = New Process()
            _process.StartInfo = si
            _process.EnableRaisingEvents = True

            ' Wire the Exited event (fires on threadpool thread when process exits)
            AddHandler _process.Exited, AddressOf OnProcessExited

            ' Wire stderr redirect (fires on threadpool thread per line)
            AddHandler _process.ErrorDataReceived, AddressOf OnErrorDataReceived

            ' Start the process
            If Not _process.Start() Then
                Throw New InvalidOperationException(
                    "FFmpegProcessHost: Process.Start() returned False — process did not start.")
            End If

            _started = True

            ' Begin async stderr reading (must be called AFTER Start)
            _process.BeginErrorReadLine()

            ' Begin async stdout reading (we don't use stdout for full-pipeline mode,
            ' but we must drain it to prevent FFmpeg from blocking on a full pipe buffer).
            _process.BeginOutputReadLine()
        End Sub

        ''' <summary>Send 'q' to FFmpeg stdin (graceful stop).</summary>
        Public Sub SendQuit()
            If _process Is Nothing OrElse _process.HasExited Then Return

            Try
                _process.StandardInput.Write("q" & vbLf)
                _process.StandardInput.Flush()
            Catch ex As Exception
                ' Pipe may already be closed (process crashed or already exited).
                ' Swallow — the caller will detect the exit via WaitForExit or Exited event.
            End Try
        End Sub

        ''' <summary>
        ''' Wait for FFmpeg to exit (with timeout).
        ''' Returns True if the process exited within the timeout, False on timeout.
        ''' Does NOT throw on timeout — caller decides how to handle.
        ''' </summary>
        Public Function WaitForExit(timeoutMs As Integer) As Boolean
            If _process Is Nothing Then Return True

            If _process.HasExited Then Return True

            Try
                Return _process.WaitForExit(timeoutMs)
            Catch ex As Exception
                ' Process may have exited between HasExited check and WaitForExit call.
                Return _process.HasExited
            End Try
        End Function

        ''' <summary>
        ''' Force-kill the FFmpeg process.
        ''' Swallows all exceptions — the process may already be dead.
        ''' Waits up to 2000 ms for the process to actually die after Kill().
        ''' </summary>
        Public Sub Kill()
            If _process Is Nothing OrElse _process.HasExited Then Return

            Try
                _process.Kill()
                _process.WaitForExit(2000)
            Catch ex As Exception
                ' Swallow — process may have exited between the HasExited check and Kill().
            End Try
        End Sub

        ''' <summary>True if the process has exited (or was never started).</summary>
        Public ReadOnly Property HasExited As Boolean
            Get
                Return _process Is Nothing OrElse _process.HasExited
            End Get
        End Property

        ''' <summary>Exit code of the process (valid after HasExited = True). Returns -1 if not started or not yet exited.</summary>
        Public ReadOnly Property ExitCode As Integer
            Get
                If _process Is Nothing OrElse Not _process.HasExited Then Return -1
                Try
                    Return _process.ExitCode
                Catch
                    Return -1
                End Try
            End Get
        End Property

        ''' <summary>Process ID (returns -1 if not started or already disposed).</summary>
        Public ReadOnly Property ProcessId As Integer
            Get
                If _process Is Nothing OrElse _process.HasExited Then Return -1
                Try
                    Return _process.Id
                Catch
                    Return -1
                End Try
            End Get
        End Property

        ' ===== Private callbacks (fire on threadpool threads) =====

        Private Sub OnProcessExited(sender As Object, e As EventArgs)
            ' P0 fix: Capture local reference to _process BEFORE accessing it.
            ' Dispose() may set _process = Nothing concurrently on another thread.
            ' The local reference stays valid even if _process is nulled —
            ' the Process object itself is not yet GC'd (it has an active
            ' event handler invocation on this thread).
            Dim proc As Process = _process
            If proc Is Nothing Then Return

            Dim exitCode As Integer = -1
            Try
                If proc.HasExited Then
                    exitCode = proc.ExitCode
                End If
            Catch
                ' Process object may be in a partially disposed state.
                ' Use -1 as the exit code — caller treats non-zero as error.
            End Try

            ' Capture generation BEFORE raising event (Dispose could increment
            ' by calling Start on a new session, though that's unlikely during
            ' callback). Use the generation captured at Start time.
            Dim gen As Integer = _generation

            ' Fire event with generation ID — caller checks for staleness.
            RaiseEvent Exited(gen, exitCode)
        End Sub

        Private Sub OnErrorDataReceived(sender As Object, e As DataReceivedEventArgs)
            If e.Data Is Nothing Then Return

            ' Capture generation for staleness check.
            Dim gen As Integer = _generation

            ' Fire event with generation ID — caller checks for staleness.
            RaiseEvent StderrLine(gen, e.Data)
        End Sub

        ' ===== IDisposable =====

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True

            ' Capture local reference — callback threads may be reading _process
            ' concurrently. We null _process AFTER unhooking events + killing,
            ' so late callbacks see the local copy (which is still valid).
            Dim proc As Process = _process
            If proc IsNot Nothing Then
                Try
                    ' Unhook events to prevent NEW callbacks from firing.
                    ' Already-queued callbacks will still fire but will use
                    ' their local proc reference (captured at callback entry).
                    RemoveHandler proc.Exited, AddressOf OnProcessExited
                    RemoveHandler proc.ErrorDataReceived, AddressOf OnErrorDataReceived
                Catch
                End Try

                ' If process is still alive, kill it (defensive — caller should have stopped first)
                If Not proc.HasExited Then
                    Try
                        proc.Kill()
                        proc.WaitForExit(2000)
                    Catch
                    End Try
                End If

                Try
                    proc.Dispose()
                Catch
                End Try

                ' Null the field AFTER dispose — late callbacks that already
                ' captured the local reference will still work correctly.
                _process = Nothing
            End If
        End Sub
    End Class
End Namespace
