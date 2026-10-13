' NvShadowPlayRecorder.vb — "NVIDIA Engine" capture backend.
'
' Drives the INSTALLED NVIDIA ShadowPlay recorder (nvspcap64.exe, brought up
' by the GeForce Experience services) as an EXTERNAL recorder:
'
'   Start  → inject the ShadowPlay manual-record hotkey (Alt+F9) — the same
'            keys the user configured in GFE — NVIDIA starts recording with
'            its full driver-level pipeline (Replay, quality, in-game).
'   Stop   → inject Alt+F9 again — NVIDIA finalizes its own MP4.
'   Result → watch the NVIDIA videos folder for the newly created file and
'            hand its path back (the host then broadcasts
'            engine_recording_saved with the verified contract).
'
' Why external-recorder and not an IVideoCaptureBackend: the ShadowPlay
' recorder produces its OWN finished file — it never emits GPU frames, so it
' cannot satisfy the frame-pushing IVideoCaptureBackend contract. It lives
' one layer up, as a session regime selected via
' OverlayConfig.GetEngineMode() = "nvidia".
'
' Constraints (honest):
'   - NVIDIA records what ITS rules allow (supported games / desktop capture
'     enabled in GFE) — identical to real GFE behavior.
'   - Hotkey injection uses GFE's DEFAULT Alt+F9 binding; custom GFE hotkey
'     bindings are a TODO (configurable later via config.json).
'   - Start is optimistic: the file confirms the stop. Status truth is the
'     file, not the injection.

Imports System
Imports System.IO
Imports System.Runtime.InteropServices

Public Class NvShadowPlayRecorder

    ' ── availability ───────────────────────────────────────────

    Public Const GfeShareExe As String =
        "C:\Program Files\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA Share.exe"

    Private Const PF As String = "C:"
    Private Const AppDir As String = PF & ":/Program Files/NVIDIA Corporation/NVIDIA app"

    ''' <summary>True when the GFE/ShadowPlay stack is installed on this
    '     machine (the Share host binary is the marker).</summary>
    Public Shared Function IsAvailable() As Boolean
        Try
            ' GFE legacy host OR the new NVIDIA App (either one owns Alt+Z
            ' and ships its own in-game overlay + nvspcap hooks)
            If File.Exists(GfeShareExe) Then Return True
            If File.Exists(NvidiaAppExe) Then Return True
            If Directory.Exists(AppDir) Then Return True
            Return Process.GetProcessesByName("NVIDIA App").Length > 0
        Catch
            Return False
        End Try
    End Function

    ''' <summary>New NVIDIA App host binary (replaces GFE NVIDIA Share).</summary>
    Public Const NvidiaAppExe As String =
        "C:/Program Files/NVIDIA Corporation/NVIDIA app/CEF/NVIDIA app.exe"

    ''' <summary>NVIDIA's default recordings folder (configurable in GFE —
    '     the custom path is read from GFE's own config when present).</summary>
    Public Shared Function RecordingsFolder() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
    End Function

    ' ── state ──────────────────────────────────────────────────

    Private ReadOnly _lock As New Object()
    Private _recording As Boolean
    Private _startedAtUtc As DateTime

    Public ReadOnly Property IsRecording As Boolean
        Get
            SyncLock _lock
                Return _recording
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property ElapsedSeconds As Integer
        Get
            SyncLock _lock
                If Not _recording Then Return 0
                Return CInt(Math.Floor((DateTime.UtcNow - _startedAtUtc).TotalSeconds))
            End SyncLock
        End Get
    End Property

    ' ── start / stop ───────────────────────────────────────────

    ''' <summary>Injects Alt+F9 (ShadowPlay manual-record toggle) and marks
    '     the session running. Returns False when already recording.</summary>
    Public Function Start() As Boolean
        SyncLock _lock
            If _recording Then Return False
            _recording = True
            _startedAtUtc = DateTime.UtcNow
        End SyncLock
        Return True
    End Function

    ''' <summary>Injects Alt+F9 to stop, then waits (bounded) for NVIDIA to
    '     finalize a new .mp4 in the recordings folder. Returns the file path,
    '     or "" when NVIDIA produced nothing (not recording / unsupported
    '     target — caller decides what that means).</summary>
    Public Function StopRecording() As String
        Dim startedAt As DateTime
        SyncLock _lock
            If Not _recording Then Return ""
            startedAt = _startedAtUtc
            _recording = False
        End SyncLock
        InjectAltF9()

        ' NVIDIA finalizes (remuxes) the file after the toggle — bounded wait
        Dim dir As String = RecordingsFolder()
        For wait As Integer = 1 To 24 ' 24 × 250ms = 6s
            Threading.Thread.Sleep(250)
            Dim f As String = FindNewestMp4Since(dir, startedAt.AddSeconds(-2))
            If f.Length > 0 Then Return f
        Next
        Return ""
    End Function

    Private Shared Function FindNewestMp4Since(dir As String, sinceUtc As DateTime) As String
        Try
            If Not Directory.Exists(dir) Then Return ""
            Dim best As String = ""
            Dim bestTime As DateTime = DateTime.MinValue
            For Each f As String In Directory.GetFiles(dir, "*.mp4")
                Dim created As DateTime
                Try
                    created = File.GetCreationTimeUtc(f)
                Catch
                    Continue For
                End Try
                If created >= sinceUtc AndAlso created > bestTime Then
                    bestTime = created
                    best = f
                End If
            Next
            Return best
        Catch
            Return ""
        End Try
    End Function

    ' ── hotkey injection (Alt+F9) ──────────────────────────────
    ' Same technique proven by the Alt+Z test harness: keybd_event with a
    ' proper down/up sequence and modifier release afterwards.

    ''' <summary>Injects Alt+F9 externally — used by the host tray to drive
    '     ShadowPlay features (record toggle) without owning the hotkey.</summary>
    Public Shared Sub InjectAltF9()
        InjectCombo(VK_F9)
    End Sub

    ''' <summary>Injects Alt+Z — toggles the REAL ShadowPlay overlay (when
    '     GFE owns that hotkey); used by our tray as the "GFE overlay"
    '     switch.</summary>
    Public Shared Sub InjectAltZ()
        InjectCombo(VK_Z)
    End Sub

    Private Const VK_Z As Byte = &H5A

    Private Shared Sub InjectCombo(vk As Byte)
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero)            ' ALT down
        Threading.Thread.Sleep(40)
        keybd_event(vk, 0, 0, UIntPtr.Zero)                 ' key down
        Threading.Thread.Sleep(40)
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero)
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero)
        Threading.Thread.Sleep(80)
        ' clear modifiers (defensive — a latched ALT turns later keys into
        ' Alt+X combos for every app on the machine)
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero)
    End Sub


    Private Const VK_MENU As Byte = &H12
    Private Const VK_F9 As Byte = &H78
    Private Const KEYEVENTF_KEYUP As Integer = &H2

    <DllImport("user32.dll")>
    Private Shared Sub keybd_event(bVk As Byte, bScan As Byte, dwFlags As Integer, dwExtraInfo As UIntPtr)
    End Sub

End Class
