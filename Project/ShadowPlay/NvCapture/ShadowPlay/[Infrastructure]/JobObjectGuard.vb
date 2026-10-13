' JobObjectGuard.vb
' Tie FFmpeg child process to a Win32 Job Object with KILL_ON_JOB_CLOSE.
'
' Problem (P1):
'   If the Engine process is killed (Task Manager, crash, logoff), the
'   ffmpeg.exe child keeps running as an orphan — still consuming GPU/CPU,
'   still holding the output file open, still writing frames to nowhere.
'
' Fix:
'   Create one Job Object per CaptureEngine instance, with
'   JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Assign every spawned ffmpeg to it.
'   When the Engine process dies for any reason, Windows automatically
'   kills all processes in the Job → ffmpeg dies with it.
'
'   The job handle is held alive by _jobHandle (a SafeHandle) for the
'   lifetime of the CaptureEngine instance. When CaptureEngine.Dispose
'   runs (or the Engine process exits and the SafeHandle finalizes),
'   the handle closes and Windows cleans up the children.
'
'   Note: We do NOT set JOB_OBJECT_LIMIT_BREAKAWAY_OK, so ffmpeg cannot
'   escape the job even if it tried.

Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports Microsoft.Win32.SafeHandles

Public NotInheritable Class JobObjectGuard
    Implements IDisposable

    Private _handle As SafeFileHandle = Nothing
    Private _disposed As Boolean = False

    ' ── P/Invoke ─────────────────────────────────────────────

    Private Const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE As UInteger = &H2000

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        Public BasicLimitInformation As JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public IoInfo As IO_COUNTERS
        Public ProcessMemoryLimit As UIntPtr
        Public JobMemoryLimit As UIntPtr
        Public PeakProcessMemoryUsed As UIntPtr
        Public PeakJobMemoryUsed As UIntPtr
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public PerProcessUserTimeLimit As Int64
        Public PerJobUserTimeLimit As Int64
        Public LimitFlags As UInteger
        Public MinimumWorkingSetSize As UIntPtr
        Public MaximumWorkingSetSize As UIntPtr
        Public ActiveProcessLimit As UInteger
        Public Affinity As UIntPtr
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

    Private Const JobObjectExtendedLimitInformation As Integer = 9
    Private Const JOB_OBJECT_MSG_EXIT_PROCESS As UInteger = 7

    <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Private Shared Function CreateJobObject(lpJobAttributes As IntPtr, lpName As String) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetInformationJobObject(hJob As IntPtr,
                                                    infoClass As Integer,
                                                    ByRef lpJobObjectInfo As JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                                                    cbJobObjectInfoLength As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function AssignProcessToJobObject(hJob As IntPtr, hProcess As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    ' ── Public API ───────────────────────────────────────────

    Public Sub New()
        Dim rawHandle As IntPtr = CreateJobObject(IntPtr.Zero, Nothing)
        If rawHandle = IntPtr.Zero Then
            Throw New Win32Exception(Marshal.GetLastWin32Error())
        End If

        ' Configure KILL_ON_JOB_CLOSE so children die when this handle is closed.
        Dim info As New JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

        If Not SetInformationJobObject(rawHandle, JobObjectExtendedLimitInformation, info, CUInt(Marshal.SizeOf(Of JOBOBJECT_EXTENDED_LIMIT_INFORMATION)())) Then
            Dim err As Integer = Marshal.GetLastWin32Error()
            CloseHandle(rawHandle)
            Throw New Win32Exception(err)
        End If

        ' Wrap in a SafeFileHandle so finalization closes the handle even if
        ' Dispose is never called (Engine crash). When the handle closes,
        ' Windows kills every process assigned to the job.
        _handle = New SafeFileHandle(rawHandle, ownsHandle:=True)
    End Sub

    ''' <summary>
    ''' Assign a process to this job. Must be called AFTER process.Start()
    ''' and BEFORE the process is allowed to spawn children (FFmpeg doesn't,
    ''' so order is not critical here).
    '''
    ''' ★ H2: returns True only when the process is ACTUALLY owned by this
    ''' job. A disposed/invalid guard, an AddRef failure, or a failed
    ''' AssignProcessToJobObject returns False — callers must treat the
    ''' process as UNOWNED and terminate it where the no-orphan contract
    ''' matters (see CaptureEngine.StartRecordingAsync). The old Sub
    ''' silently "succeeded" on a disposed guard, letting a started ffmpeg
    ''' escape lifetime ownership as a permanent orphan.
    ''' </summary>
    Public Function Assign(process As Process) As Boolean
        If process Is Nothing Then Return False

        ' Local copy closes the check→use gap with Dispose() nulling the
        ' field: the local keeps the SafeFileHandle object alive, and if
        ' Dispose already ran, DangerousAddRef below throws (caught → False).
        Dim handle As SafeFileHandle = _handle
        If handle Is Nothing OrElse handle.IsInvalid Then Return False

        ' Get a fresh raw handle to the process — do NOT use process.Handle
        ' directly because that's the handle the Process instance owns and
        ' we don't want to duplicate-close it.
        Dim hProc As IntPtr = process.Handle
        If hProc = IntPtr.Zero Then Return False

        ' ✅ M11 FIX: use DangerousAddRef/Release to prevent the handle from
        ' being closed by Dispose() on another thread mid-P/Invoke.
        ' Old code called DangerousGetHandle() which returns the raw value
        ' without incrementing the ref count — if Dispose ran concurrently,
        ' the handle could be closed before AssignProcessToJobObject executes.
        Dim success As Boolean = False
        Try
            handle.DangerousAddRef(success)
            If Not success Then Return False
            Dim rawHandle As IntPtr = handle.DangerousGetHandle()
            If Not AssignProcessToJobObject(rawHandle, hProc) Then Return False
        Catch
            ' ★ H2: any failure means NOT owned — report it instead of
            ' pretending the assignment succeeded (old behavior swallowed).
            Return False
        Finally
            If success Then
                Try
                    handle.DangerousRelease()
                Catch
                End Try
            End If

            ' ═══ AboveNormal Priority (per architecture spec) ═══
            ' Set FFmpeg process priority to AboveNormal so it gets more CPU
            ' for realtime capture. High/RealTime is intentionally avoided
            ' so Windows retains scheduler headroom for audio/UI/system work.
            Try
                process.PriorityClass = ProcessPriorityClass.AboveNormal
            Catch
                ' Best-effort — some systems may not allow priority changes
            End Try
        End Try
        Return True
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        If _handle IsNot Nothing Then
            _handle.Dispose()
            _handle = Nothing
        End If
    End Sub

End Class
