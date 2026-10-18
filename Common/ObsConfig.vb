Imports System.IO
Imports Newtonsoft.Json.Linq

' ObsConfig.vb — SHARED notifier_obs.json schema/access.
'
' Lives in Common\ and is linked into both projects that touch OBS settings:
'
'   NVIDIA Notifier  — Load() + 2s hot-reload watcher (HasFileChanged/Reload);
'                      owns the actual OBS WebSocket bridge (ObsWebSocketClient).
'   NVIDIA Overlay   — Settings → General page edits Enabled/Host/Port/Password
'                      and calls Save(); the Notifier picks the file change up
'                      within ~2 seconds.
'
' Write policy mirrors AppConfigShared: Save() is a read-modify-write against
' the CURRENT file content, so the "forward" section and any unknown keys
' survive untouched.

Public Class ObsConfig

    Public Property Enabled As Boolean = True
    Public Property Host As String = "127.0.0.1"
    Public Property Port As Integer = 4455
    Public Property Password As String = ""

    Public Property ForwardRecordStateChanged As Boolean = True
    Public Property ForwardReplayBufferStateChanged As Boolean = True
    Public Property ForwardReplayBufferSaved As Boolean = True
    Public Property ForwardScreenshotSaved As Boolean = True

    Public Const FileName As String = "notifier_obs.json"

    Private _lastWriteUtc As DateTime = DateTime.MinValue

    Public ReadOnly Property ConfigPath As String
        Get
            Dim activePath As String = AppLayout.P("NVIDIA Config", FileName)
            If File.Exists(activePath) Then Return activePath

            ' Migrate a legacy config forward once; never keep writing to the old slot.
            Dim legacyPath As String = AppLayout.P("NvConfig", FileName)
            If File.Exists(legacyPath) Then
                Try
                    AppLayout.EnsureParentDir(activePath)
                    File.Copy(legacyPath, activePath, False)
                    Return activePath
                Catch
                    ' If another process created the new file first, prefer it.
                    If File.Exists(activePath) Then Return activePath
                    ' Preserve availability when the one-time migration is blocked.
                    Return legacyPath
                End Try
            End If

            Return activePath
        End Get
    End Property

    Public Shared Function Load() As ObsConfig
        Return LoadInternal(Nothing)
    End Function

    Public Function Reload() As Boolean
        Dim fresh As ObsConfig = LoadInternal(Me)
        If fresh Is Nothing Then Return False

        Me.Enabled = fresh.Enabled
        Me.Host = fresh.Host
        Me.Port = fresh.Port
        Me.Password = fresh.Password
        Me.ForwardRecordStateChanged = fresh.ForwardRecordStateChanged
        Me.ForwardReplayBufferStateChanged = fresh.ForwardReplayBufferStateChanged
        Me.ForwardReplayBufferSaved = fresh.ForwardReplayBufferSaved
        Me.ForwardScreenshotSaved = fresh.ForwardScreenshotSaved
        Me._lastWriteUtc = fresh._lastWriteUtc
        Return True
    End Function

    ''' <summary>
    ''' Persists Enabled/Host/Port/Password using read-modify-write on the
    ''' CURRENT file content, so the "forward" section (and unknown keys)
    ''' survive. Never throws; returns False when the write failed.
    ''' </summary>
    Public Function Save() As Boolean
        Try
            Dim root As JObject = Nothing
            If File.Exists(ConfigPath) Then
                Try
                    root = TryCast(JObject.Parse(File.ReadAllText(ConfigPath)), JObject)
                Catch
                    root = Nothing
                End Try
            End If
            If root Is Nothing Then root = New JObject()

            root("enabled") = Enabled
            root("host") = Host
            root("port") = Port
            root("password") = Password

            AppLayout.EnsureParentDir(ConfigPath)
            File.WriteAllText(ConfigPath, root.ToString(Newtonsoft.Json.Formatting.Indented),
                              New System.Text.UTF8Encoding(False))

            Dim info As New FileInfo(ConfigPath)
            _lastWriteUtc = info.LastWriteTimeUtc
            Return True
        Catch
            Return False
        End Try
    End Function

    Public Function HasFileChanged() As Boolean
        Try
            If Not File.Exists(ConfigPath) Then Return False
            Dim info As New FileInfo(ConfigPath)
            Return info.LastWriteTimeUtc <> _lastWriteUtc
        Catch
            Return False
        End Try
    End Function

    Private Shared Function LoadInternal(target As ObsConfig) As ObsConfig
        Dim cfg As ObsConfig
        If target IsNot Nothing Then
            cfg = target
        Else
            cfg = New ObsConfig()
        End If

        Dim configPath As String = cfg.ConfigPath
        If Not File.Exists(configPath) Then Return cfg

        Try
            Dim info As New FileInfo(configPath)
            cfg._lastWriteUtc = info.LastWriteTimeUtc

            Dim json As String
            Using fs As New FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Using sr As New StreamReader(fs)
                    json = sr.ReadToEnd()
                End Using
            End Using

            Dim root As JObject = JObject.Parse(json)

            cfg.Enabled = ReadBool(root, "enabled", True)
            cfg.Host = ReadString(root, "host", "127.0.0.1")
            cfg.Port = ReadInt(root, "port", 4455)
            cfg.Password = ReadString(root, "password", "")

            Dim forward As JObject = TryCast(root("forward"), JObject)
            If forward IsNot Nothing Then
                cfg.ForwardRecordStateChanged = ReadBool(forward, "record_state_changed", True)
                cfg.ForwardReplayBufferStateChanged = ReadBool(forward, "replay_buffer_state_changed", True)
                cfg.ForwardReplayBufferSaved = ReadBool(forward, "replay_buffer_saved", True)
                cfg.ForwardScreenshotSaved = ReadBool(forward, "screenshot_saved", True)
            End If

            Debug.WriteLine("[ObsConfig] Loaded " & configPath &
                            "  enabled=" & cfg.Enabled.ToString() &
                            "  host=" & cfg.Host & ":" & cfg.Port.ToString() &
                            "  ts=" & cfg._lastWriteUtc.ToString("HH:mm:ss"))
        Catch ex As Exception
            Debug.WriteLine("[ObsConfig] Failed to load " & configPath &
                            ": " & ex.Message & "  (keeping previous values)")
            Return Nothing
        End Try

        Return cfg
    End Function

    Private Shared Function ReadBool(obj As JObject, key As String, defaultValue As Boolean) As Boolean
        If obj Is Nothing Then Return defaultValue
        Dim tok As JToken = obj(key)
        If tok Is Nothing Then Return defaultValue
        Try
            Return tok.Value(Of Boolean)()
        Catch
            Return defaultValue
        End Try
    End Function

    Private Shared Function ReadInt(obj As JObject, key As String, defaultValue As Integer) As Integer
        If obj Is Nothing Then Return defaultValue
        Dim tok As JToken = obj(key)
        If tok Is Nothing Then Return defaultValue
        Try
            Return tok.Value(Of Integer)()
        Catch
            Dim s As String = Nothing
            Try
                s = tok.Value(Of String)()
            Catch
            End Try
            Dim n As Integer
            If s IsNot Nothing AndAlso Integer.TryParse(s, n) Then Return n
            Return defaultValue
        End Try
    End Function

    Private Shared Function ReadString(obj As JObject, key As String, defaultValue As String) As String
        If obj Is Nothing Then Return defaultValue
        Dim tok As JToken = obj(key)
        If tok Is Nothing Then Return defaultValue
        Dim s As String = Nothing
        Try
            s = tok.Value(Of String)()
        Catch
        End Try
        If String.IsNullOrEmpty(s) Then Return defaultValue
        Return s
    End Function

    Public Function ShouldForward(eventType As String) As Boolean
        Select Case eventType
            Case "RecordStateChanged"
                Return ForwardRecordStateChanged
            Case "ReplayBufferStateChanged"
                Return ForwardReplayBufferStateChanged
            Case "ReplayBufferSaved"
                Return ForwardReplayBufferSaved
            Case "ScreenshotSaved"
                Return ForwardScreenshotSaved
            Case Else
                Return False
        End Select
    End Function

End Class
