Imports System
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading.Tasks

' Spawns and owns the node backend process. A Windows job object with
' KILL_ON_JOB_CLOSE is the no-orphan guarantee: if the host dies for any
' reason (crash, hard kill, console close), the kernel closes the job and
' the backend cannot outlive its host. The backend is crash-safe by design
' (Backend\index.js: uncaughtException -> keep serving; store writes are
' atomic tmp+rename), so a kernel-level terminate on our death is safe.
Friend Class BackendProcess
    Implements IDisposable

    Private ReadOnly _lock As New Object()
    Private _process As Process
    Private _jobHandle As IntPtr = IntPtr.Zero

    Public ReadOnly Property Process As Process
        Get
            Return _process
        End Get
    End Property

    Public Sub Start(nodeExe As String, entryJs As String, baseDir As String)
        _jobHandle = CreateJobObjectW(IntPtr.Zero, Nothing)
        If _jobHandle = IntPtr.Zero Then
            Throw New InvalidOperationException("CreateJobObject failed (win32 " & Marshal.GetLastWin32Error() & ")")
        End If
        Dim limits As New JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        If Not SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, limits,
                                       Marshal.SizeOf(GetType(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))) Then
            Dim err As Integer = Marshal.GetLastWin32Error()
            CloseHandle(_jobHandle)
            _jobHandle = IntPtr.Zero
            Throw New InvalidOperationException("SetInformationJobObject failed (win32 " & err & ")")
        End If

        ' node runs hidden: its output is piped into NVIDIA Web Helper.log.
        Dim psi As New ProcessStartInfo With {
            .FileName = nodeExe,
            .Arguments = """" & entryJs & """",
            .WorkingDirectory = baseDir,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8}

        _process = Process.Start(psi)
        If Not AssignProcessToJobObject(_jobHandle, _process.Handle) Then
            Dim err As Integer = Marshal.GetLastWin32Error()
            Try : _process.Kill() : Catch : End Try
            CloseHandle(_jobHandle)
            _jobHandle = IntPtr.Zero
            Throw New InvalidOperationException("AssignProcessToJobObject failed (win32 " & err & ")")
        End If

        ' Interactive Ctrl+C shares this console with the child, so node's
        ' own SIGINT shutdown handler gets the grace window first (see TryStop).
        AddHandler _process.OutputDataReceived,
            Sub(sender, e)
                If e.Data IsNot Nothing Then HostLog.NodeOut(e.Data)
            End Sub
        AddHandler _process.ErrorDataReceived,
            Sub(sender, e)
                If e.Data IsNot Nothing Then HostLog.NodeErr(e.Data)
            End Sub
        _process.BeginOutputReadLine()
        _process.BeginErrorReadLine()
    End Sub

    Public Function HasExited As Boolean
        Return _process.HasExited
    End Function

    Public Function WaitForExitTask() As Task
        _process.EnableRaisingEvents = True
        Dim tcs As New TaskCompletionSource(Of Object)(TaskCreationOptions.RunContinuationsAsynchronously)
        If _process.HasExited Then
            tcs.TrySetResult(Nothing)
        Else
            AddHandler _process.Exited, Sub() tcs.TrySetResult(Nothing)
        End If
        Return tcs.Task
    End Function

    Public Function SafeExitCode() As Integer
        Try
            Return _process.ExitCode
        Catch
            Return -1
        End Try
    End Function

    ' Orderly stop: grace window for a natural exit (interactive Ctrl+C —
    ' node runs its SIGINT shutdown path), then hard-terminate the job so
    ' nothing outlives the host. Returns True when the child is confirmed dead.
    Public Function TryStop(graceMs As Integer, waitAfterKillMs As Integer) As Boolean
        If _process.HasExited Then Return True

        Dim exitTask = WaitForExitTask()
        Dim finished = Task.WhenAny(exitTask, Task.Delay(graceMs)).GetAwaiter().GetResult()
        If finished IsNot exitTask Then
            HostLog.Log("backend did not exit within the " & graceMs & "ms grace — terminating job")
            If _jobHandle <> IntPtr.Zero Then TerminateJobObject(_jobHandle, 1UI)
        End If
        Try
            Return _process.WaitForExit(waitAfterKillMs)
        Catch
            Return _process.HasExited
        End Try
    End Function

    ' Kernel safety net: closing the job handle kills any survivor
    ' (KILL_ON_JOB_CLOSE) — covers paths that never reach TryStop.
    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _lock
            If _process IsNot Nothing Then
                Try : _process.Dispose() : Catch : End Try
                _process = Nothing
            End If
            If _jobHandle <> IntPtr.Zero Then
                CloseHandle(_jobHandle)
                _jobHandle = IntPtr.Zero
            End If
        End SyncLock
    End Sub

    ' ── job object P/Invoke (kill-on-close) ─────────────────────────────

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CreateJobObjectW(lpJobAttributes As IntPtr, lpName As String) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetInformationJobObject(hJob As IntPtr, infoClass As Integer,
                                                    ByRef lpInfo As JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                                                    cbInfo As Integer) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function AssignProcessToJobObject(hJob As IntPtr, hProcess As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function TerminateJobObject(hJob As IntPtr, uExitCode As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    Private Const JobObjectExtendedLimitInformation As Integer = 9
    Private Const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE As UInteger = &H2000UI

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public PerProcessUserTimeLimit As Long
        Public PerJobUserTimeLimit As Long
        Public LimitFlags As UInteger
        Public MinimumWorkingSetSize As IntPtr
        Public MaximumWorkingSetSize As IntPtr
        Public ActiveProcessLimit As UInteger
        Public Affinity As IntPtr
        Public PriorityClass As UInteger
        Public SchedulingClass As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure IO_COUNTERS
        Public ReadOperationCount As ULong
        Public WriteOperationCount As ULong
        Public OtherOperationCount As ULong
        Public ReadTransferCount As ULong
        Public WriteTransferCount As ULong
        Public OtherTransferCount As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        Public BasicLimitInformation As JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public IoInfo As IO_COUNTERS
        Public ProcessMemoryLimit As IntPtr
        Public JobMemoryLimit As IntPtr
        Public PeakProcessMemoryUsed As IntPtr
        Public PeakJobMemoryUsed As IntPtr
    End Structure

End Class
