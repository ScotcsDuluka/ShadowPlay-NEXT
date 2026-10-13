' ShadowPlayRest.vb — CAPTURE-REST-PLAN: REST command channel client + poller
' (the two classes UI_Engine.vb expects — contract per CAPTURE-REST-PLAN.md)
'
' Backend plane (our NvNode shim :59001):
'   GET  /Backend/v.1.0/health      → 200 = backend up (wait loop)
'   GET  /Duluka/v.1.0/State        → {recordState, irState, savePath}  (edge source)
'   POST /Duluka/v.1.0/Actual       → {running:bool}   (engine confirms actuals)
'   GET  /ShadowPlay/v.1.0/Record/Settings → quality/resolution/fps/bitrate (osc page authority)
'
' Cookie: X_LOCAL_SECURITY_COOKIE from %LOCALAPPDATA%\NVIDIA Corporation\NvNode\nvnode-init.json
' (the same pairing file every genuine component reads).
Imports System.IO
Imports System.Net
Imports System.Text

''' <summary>Snapshot of the osc page's record settings (quality authority).</summary>
Public Class RecordSettingsSnapshot
    Public Property Quality As String
    Public Property Resolution As String
    Public Property NativeResolution As Boolean
    Public Property Width As Integer
    Public Property Height As Integer
    Public Property Framerate As Integer
    Public Property BitrateBps As Integer
End Class

''' <summary>One poll of /Duluka/v.1.0/State.</summary>
Public Class DulukaState
    Public Property RecordState As String = "Idle"
    Public Property IrState As String = "Idle"
    Public Property SavePath As String = ""
End Class

''' <summary>Minimal REST client for the :59001 backend (cookie-authenticated).</summary>
Public Class ShadowPlayRestClient
    Private ReadOnly _base As String
    Private ReadOnly _cookie As String

    Public Sub New()
        Dim port = 59001
        Dim secret = "0ca906d2784f0d14e399874f5c5ed4a1"
        Try
            ' ★ coexistence: ไฟล์ของเรา (-duluka) — ห้ามอ่านของแท้ (cookie มันต่าง)
            Dim ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "NVIDIA Corporation", "NvNode", "nvnode-init-duluka.json")
            Dim json = File.ReadAllText(ini)
            Dim pm = System.Text.RegularExpressions.Regex.Match(json, """port""\s*:\s*(\d+)")
            Dim sm = System.Text.RegularExpressions.Regex.Match(json, """secret""\s*:\s*""([0-9a-fA-F]+)""")
            If pm.Success Then port = Integer.Parse(pm.Groups(1).Value)
            If sm.Success Then secret = sm.Groups(1).Value
        Catch
            ' fallback: fixed pairing (our shim node)
        End Try
        _base = "http://127.0.0.1:" & port.ToString()
        _cookie = secret
    End Sub

    Private Function Request(method As String, path As String, body As String) As String
        Try
            Dim req = CType(WebRequest.Create(_base & path), HttpWebRequest)
            req.Method = method
            req.ContentType = "application/json"
            req.Proxy = Nothing
            req.Timeout = 3000
            req.Headers.Add("X_LOCAL_SECURITY_COOKIE", _cookie)
            If body IsNot Nothing Then
                Dim bs = Encoding.UTF8.GetBytes(body)
                req.ContentLength = bs.Length
                Using s = req.GetRequestStream()
                    s.Write(bs, 0, bs.Length)
                End Using
            End If
            Using resp = CType(req.GetResponse(), HttpWebResponse)
                Using sr As New System.IO.StreamReader(CType(resp.GetResponseStream(), System.IO.Stream))
                    Return sr.ReadToEnd()
                End Using
            End Using
        Catch wex As WebException
            If wex.Response IsNot Nothing Then
                Try
                    Using sr = New System.IO.StreamReader(wex.Response.GetResponseStream())
                        Return sr.ReadToEnd()   ' 4xx body — caller decides
                    End Using
                Catch
                    Return Nothing
                End Try
            End If
            Return Nothing
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>200 from /health = backend up.</summary>
    Public Function WaitForBackend(timeout As TimeSpan) As Boolean
        Dim deadline = DateTime.UtcNow + timeout
        While DateTime.UtcNow < deadline
            Dim body = Request("GET", "/Backend/v.1.0/health", Nothing)
            If body IsNot Nothing AndAlso body.Contains("ok") Then Return True
            Threading.Thread.Sleep(500)
        End While
        Return False
    End Function

    Public Function GetDulukaState() As DulukaState
        Dim raw = Request("GET", "/Duluka/v.1.0/State", Nothing)
        If String.IsNullOrEmpty(raw) OrElse raw.Contains("Cannot GET") OrElse raw.Contains("invalid") Then Return Nothing
        Dim st As New DulukaState()
        Dim m = System.Text.RegularExpressions.Regex.Match(raw, """recordState""\s*:\s*""([^""]+)""")
        If m.Success Then st.RecordState = m.Groups(1).Value
        m = System.Text.RegularExpressions.Regex.Match(raw, """irState""\s*:\s*""([^""]+)""")
        If m.Success Then st.IrState = m.Groups(1).Value
        m = System.Text.RegularExpressions.Regex.Match(raw, """savePath""\s*:\s*""([^""]*)""")
        If m.Success Then st.SavePath = m.Groups(1).Value.Replace("\\", "\")
        Return st
    End Function

    ''' <summary>Engine confirms the ACTUAL running state — backend publishes /Record/Running from it.</summary>
    Public Sub ConfirmActual(running As Boolean)
        Request("POST", "/Duluka/v.1.0/Actual", If(running, "{""running"":true}", "{""running"":false}"))
    End Sub

    Public Sub ConfirmRecordStarted()
        ConfirmActual(True)
    End Sub

    Public Sub ConfirmRecordStopped()
        ConfirmActual(False)
    End Sub

    ''' <summary>GET /Record/Settings — the osc page's quality authority → snapshot.</summary>
    Public Function TryGetRecordSettings() As RecordSettingsSnapshot
        Try
            Dim raw = Request("GET", "/ShadowPlay/v.1.0/Record/Settings", Nothing)
            If String.IsNullOrEmpty(raw) OrElse Not raw.StartsWith("{") Then Return Nothing
            Dim snap As New RecordSettingsSnapshot()
            Dim m = System.Text.RegularExpressions.Regex.Match(raw, """quality""\s*:\s*""([^""]+)""")
            If m.Success Then snap.Quality = m.Groups(1).Value
            m = System.Text.RegularExpressions.Regex.Match(raw, """resolution""\s*:\s*""([^""]+)""")
            If m.Success Then snap.Resolution = m.Groups(1).Value
            m = System.Text.RegularExpressions.Regex.Match(raw, """framerate""\s*:\s*(\d+)")
            If m.Success Then snap.Framerate = Integer.Parse(m.Groups(1).Value)
            m = System.Text.RegularExpressions.Regex.Match(raw, """bitrateBps""\s*:\s*(\d+)")
            If m.Success Then snap.BitrateBps = Integer.Parse(m.Groups(1).Value)
            Select Case snap.Resolution
                Case "In-game" : snap.NativeResolution = True
                Case "2160p 4K" : snap.Width = 3840 : snap.Height = 2160
                Case "1440p HD" : snap.Width = 2560 : snap.Height = 1440
                Case "1080p HD" : snap.Width = 1920 : snap.Height = 1080
                Case "720p HD" : snap.Width = 1280 : snap.Height = 720
                Case "480p" : snap.Width = 854 : snap.Height = 480
                Case "360p" : snap.Width = 640 : snap.Height = 360
                Case "240p" : snap.Width = 426 : snap.Height = 240
                Case Else : snap.NativeResolution = True
            End Select
            Return snap
        Catch
            Return Nothing
        End Try
    End Function
End Class

''' <summary>
''' Polls /Duluka/v.1.0/State every 500 ms and raises typed edges
''' (Idle→Starting = record start, Recording→Stopping = stop; same for IR).
''' </summary>
Public Class RestCommandPoller
    Public Event RecordStartRequested(savePath As String)
    Public Event RecordStopRequested()
    Public Event InstantReplayStartRequested(savePath As String)
    Public Event InstantReplayStopRequested()

    Private ReadOnly _rest As ShadowPlayRestClient
    Private _timer As Threading.Timer
    Private _prevRecord As String = "Idle"
    Private _prevIr As String = "Idle"
    Private _busy As Boolean
    Private _pollCount As Integer

    Public Sub New(rest As ShadowPlayRestClient)
        _rest = rest
    End Sub

    Public Sub Start()
        If _timer IsNot Nothing Then Return
        _timer = New Threading.Timer(AddressOf Tick, Nothing, 500, 500)
    End Sub

    Public Sub [Stop]()
        If _timer IsNot Nothing Then
            _timer.Dispose()
            _timer = Nothing
        End If
    End Sub

    Private Sub Tick(state As Object)
        If _busy Then Return
        _busy = True
        Try
            Dim st = _rest.GetDulukaState()
            _pollCount += 1
            If _pollCount Mod 20 = 1 Then
                Try : System.IO.File.AppendAllText("C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\NvContainer\CaptureEngine\Logs\rest-poll.log",
                    DateTime.Now.ToString("HH:mm:ss") & " poll#" & _pollCount & " state=" & If(st, Nothing, If(st.RecordState, "NULL")) & vbCrLf, Encoding.UTF8)
                Catch : End Try
            End If
            If st Is Nothing Then Return
            Dim savePath = st.SavePath

            If _prevRecord <> "Starting" AndAlso st.RecordState = "Starting" Then
                RaiseEvent RecordStartRequested(savePath)
            ElseIf _prevRecord <> "Stopping" AndAlso st.RecordState = "Stopping" Then
                RaiseEvent RecordStopRequested()
            End If
            _prevRecord = st.RecordState

            If _prevIr <> "Saving" AndAlso st.IrState = "Saving" Then
                RaiseEvent InstantReplayStartRequested(savePath)
            ElseIf _prevIr = "Saving" AndAlso st.IrState <> "Saving" Then
                RaiseEvent InstantReplayStopRequested()
            End If
            _prevIr = st.IrState
        Catch
            ' never die from a poll
        Finally
            _busy = False
        End Try
    End Sub
End Class
