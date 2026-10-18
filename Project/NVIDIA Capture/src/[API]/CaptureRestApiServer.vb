' CaptureRestApiServer.vb — Phase 3 (§21.2): capture boundary REST ของ NvCapture.exe
'
' NvCapture.exe เป็น "เจ้าของการอัด" ตามสถาปัตยกรรม §20 — node JS boundary
' (NvShadowPlayAPI.js middleware, [NvCapture-boundary §21.2]) ยิงมาที่นี่:
'
'   GET  /health         → {"ok":true,"ready":...,"recording":...}
'   GET  /state          → {"ready","recording","enabled","elapsedSec","outputPath","lastFile","failReason"}
'   POST /record/start   → StartRecording ผ่าน HandleRecordingStart (engine path เดียวกับ TCP)
'   POST /record/stop    → HandleRecordingStop + เก็บ result.OutputPath
'   GET  /settings       → {"quality","resolution","framerate","bitrateBps"} จาก engine config จริง
'   POST /settings       → เขียน CaptureSettings + Save(config) (record ถัดไปใช้ค่าใหม่ — fresh reload)
'
' หลักการ: ไม่เปิดพอร์ตสาธารณะ (127.0.0.1 เท่านั้น) · fail-soft ทุก catch ·
' ไม่เดาสถานะ — รายงานจาก _recordingEngine/_recordingTask จริงเท่านั้น
Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

Partial Public Class UI_Engine

    Private Const RestApiPort As Integer = 59077
    Private _restApiListener As TcpListener
    Private _restApiThread As Thread
    Private _restApiRunning As Boolean
    Private _restLastFile As String = ""
    Private ReadOnly _restLock As New Object()

    Public Sub StartCaptureRestApi()
        If _restApiRunning Then Return
        _restApiRunning = True
        _restApiListener = New TcpListener(IPAddress.Loopback, RestApiPort)
        Try
            _restApiListener.Start()
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] listener start failed: {ex.Message}")
            _restApiRunning = False
            Return
        End Try
        _restApiThread = New Thread(AddressOf RestApiLoop) With {.IsBackground = True}
        _restApiThread.Start()
        DebugLog($"[CaptureRestApi] listening on 127.0.0.1:{RestApiPort}")
    End Sub

    Private Sub RestApiLoop()
        While _restApiRunning
            Try
                Dim client = _restApiListener.AcceptTcpClient()
                Task.Run(Sub() ServeRestClient(client))
            Catch ex As Exception
                If _restApiRunning Then DebugLog($"[CaptureRestApi] accept: {ex.Message}")
            End Try
        End While
    End Sub

    Private Sub ServeRestClient(client As TcpClient)
        Try
            client.ReceiveTimeout = 3000 : client.SendTimeout = 3000
            Using c = client
                Dim stream = c.GetStream()
                Dim req = ReadHttpRequest(stream)
                If req Is Nothing Then Return
                Dim method = req("method").ToString().ToUpperInvariant()
                Dim path = req("path").ToString()
                Dim body = req("body").ToString()
                Dim respText As String = HandleRestRequest(method, path, body)
                Dim payload As Byte() = System.Text.Encoding.UTF8.GetBytes(respText)
                Dim head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK{vbCr}{vbLf}Content-Type: application/json{vbCr}{vbLf}Content-Length: {payload.Length}{vbCr}{vbLf}Connection: close{vbCr}{vbLf}{vbCr}{vbLf}")
                stream.Write(head, 0, head.Length)
                stream.Write(payload, 0, payload.Length)
                stream.Flush()
            End Using
        Catch
        End Try
    End Sub

    ' อ่าน HTTP request จาก stream — return {method,path,body} หรือ Nothing
    Private Function ReadHttpRequest(stream As NetworkStream) As Dictionary(Of String, String)
        Try
            Dim buf(8191) As Byte
            Dim sb As New StringBuilder()
            Dim headerEnd As Integer = -1
            Dim total As Integer = 0
            ' อ่านจนจบ header
            While headerEnd < 0 AndAlso total < buf.Length
                Dim n = stream.Read(buf, 0, buf.Length)
                If n <= 0 Then Exit While
                sb.Append(Encoding.ASCII.GetString(buf, total, n))
                total += n
                headerEnd = sb.ToString().IndexOf(vbCr & vbLf & vbCr & vbLf, StringComparison.Ordinal)
            End While
            If headerEnd < 0 Then Return Nothing
            Dim head = sb.ToString().Substring(0, headerEnd)
            Dim lines = head.Split({vbCr & vbLf}, StringSplitOptions.None)
            If lines.Length = 0 Then Return Nothing
            Dim reqLine = lines(0).Split(" "c)
            If reqLine.Length < 2 Then Return Nothing
            Dim bodyLen = 0
            For Each ln In lines
                If ln.ToLowerInvariant().StartsWith("content-length:") Then
                    Integer.TryParse(ln.Substring(15).Trim(), bodyLen)
                End If
            Next
            Dim bodyStart = headerEnd + 4
            Dim bodyBytes(bodyLen - 1) As Byte
            Dim have = total - bodyStart
            If have > 0 Then Array.Copy(buf, bodyStart, bodyBytes, 0, Math.Min(have, bodyLen))
            ' อ่าน body ที่เหลือ
            While have < bodyLen
                Dim n = stream.Read(bodyBytes, have, bodyLen - have)
                If n <= 0 Then Exit While
                have += n
            End While
            Return New Dictionary(Of String, String) From {
                {"method", reqLine(0)},
                {"path", reqLine(1)},
                {"body", Encoding.UTF8.GetString(bodyBytes)}
            }
        Catch
            Return Nothing
        End Try
    End Function

    Private Function HandleRestRequest(method As String, path As String, body As String) As String
        Try
            If path = "/health" Then
                Return $"{{""ok"":true,""engine"":""NvCapture"",""ready"":{_engineReady.ToString().ToLowerInvariant()},""recording"":{RestRecording().ToString().ToLowerInvariant()}}}"
            End If

            If path = "/state" Then
                SyncLock _restLock
                End SyncLock
                Dim elapsed = 0
                If RestRecording() AndAlso _newEngineSessionClock.IsRunning Then elapsed = CInt(_newEngineSessionClock.Elapsed.TotalSeconds)
                Dim outPath = If(_newEngineSessionOutputPath, "")
                Return $"{{""ready"":{_engineReady.ToString().ToLowerInvariant()},""failReason"":{JStr(_engineInitFailReason)},""recording"":{RestRecording().ToString().ToLowerInvariant()},""enabled"":{RestRecording().ToString().ToLowerInvariant()},""elapsedSec"":{elapsed},""outputPath"":{JStr(outPath)},""lastFile"":{JStr(_restLastFile)}}}"
            End If

            If path = "/record/start" AndAlso method = "POST" Then
                If Not _engineReady OrElse _recordingEngine Is Nothing Then
                    Return "{""started"":false,""error"":""engine_not_ready"",""failReason"":" & JStr(_engineInitFailReason) & "}"
                End If
                If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
                    Return "{""started"":false,""error"":""already_recording""}"
                End If
                Dim outPath = ResolveRecordOutputPath()
                HandleRecordingStart(outPath, "restapi", LoadOscSnapshot())   ' snapshot = settings จาก OSC plane (override config)
                ' รอสั้น ๆ ให้ handler ตอบ (start เร็ว — ffmpeg spawn)
                For i = 1 To 20
                    Thread.Sleep(250)
                    If _recordingTask IsNot Nothing Then Exit For
                Next
                Thread.Sleep(700)
                Dim active = _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted
                Return $"{{""started"":{active.ToString().ToLowerInvariant()},""outputPath"":{JStr(_newEngineSessionOutputPath)}}}"
            End If

            If path = "/record/stop" AndAlso method = "POST" Then
                If _recordingTask Is Nothing OrElse _recordingTask.IsCompleted Then
                    Return "{""stopped"":false,""error"":""not_recording""}"
                End If
                HandleRecordingStop("restapi")
                ' รอ finalize (mux) — ไม่เกิน 20 วิ
                For i = 1 To 80
                    Thread.Sleep(250)
                    If _recordingTask IsNot Nothing AndAlso _recordingTask.IsCompleted Then Exit For
                Next
                Dim file = ""
                Try
                    If _recordingTask IsNot Nothing AndAlso _recordingTask.IsCompleted AndAlso _recordingTask.Result IsNot Nothing Then
                        file = _recordingTask.Result.OutputPath
                    End If
                Catch ex As Exception
                    DebugLog($"[CaptureRestApi] stop result: {ex.Message}")
                End Try
                If Not String.IsNullOrEmpty(file) Then _restLastFile = file
                Return $"{{""stopped"":true,""file"":{JStr(file)}}}"
            End If

            If path = "/settings" AndAlso method = "GET" Then
                Return OscSettingsJson()
            End If

            If path = "/settings" AndAlso method = "POST" Then
                ApplyOscSettings(body)
                DebugLog("[CaptureRestApi] OSC settings stored (applied to next session via snapshot)")
                Return OscSettingsJson()
            End If

            Return "{""error"":""not_found""}"
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] {method} {path}: {ex.Message}")
            Return "{""error"":""internal""}"
        End Try
    End Function

    ' ── truth: session active = host clock รัน + task ยังไม่จบ ──
    Private Function RestRecording() As Boolean
        Return _engineReady AndAlso _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted
    End Function

    ' ── output path: videos จาก genuine /RecordPaths → fallback OutputDirectory/MyVideos ──
    Private Function ResolveRecordOutputPath() As String
        Dim dir As String = ""
        Try
            Dim req = DirectCast(Net.WebRequest.Create("http://127.0.0.1:59001/ShadowPlay/v.1.0/RecordPaths"), Net.HttpWebRequest)
            req.Method = "GET" : req.Timeout = 2500 : req.Proxy = Nothing
            Using rsp = DirectCast(req.GetResponse(), Net.HttpWebResponse)
                Dim body As String = ""
                Using sr As New System.IO.StreamReader(CType(rsp.GetResponseStream(), System.IO.Stream))
                    body = sr.ReadToEnd()
                End Using
                Using doc = System.Text.Json.JsonDocument.Parse(body)
                    Dim p As System.Text.Json.JsonElement = Nothing
                    If doc.RootElement.TryGetProperty("videos", p) Then dir = p.GetString()
                End Using
            End Using
        Catch
        End Try
        If String.IsNullOrWhiteSpace(dir) OrElse Not Directory.Exists(dir) Then
            dir = _settings.OutputDirectory
            If String.IsNullOrWhiteSpace(dir) OrElse Not Directory.Exists(dir) Then dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        End If
        Try : Directory.CreateDirectory(dir) : Catch : End Try
        Return Path.Combine(dir, "NvCapture_" & DateTime.Now.ToString("yyyy-MM-dd_HHmmss") & ".mp4")
    End Function

    ' ── OSC settings store (Config\capture-osc.json) — defaults = genuine harvest (VeryGood=50Mbps, 8 res, fps 60/30) ──
    Private Shared ReadOnly OscStoreLock As New Object()

    Private Function OscStorePath() As String
        Return AppLayout.P("Config", "capture-osc.json")
    End Function

    Private Function LoadOscSettings() As Dictionary(Of String, String)
        SyncLock OscStoreLock
            Dim d As New Dictionary(Of String, String) From {
                {"quality", "VeryGood"}, {"resolution", "In-game"}, {"framerate", "60"}, {"bitrateBps", "50000000"}
            }
            Try
                Using doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(OscStorePath()))
                    Dim r = doc.RootElement
                    For Each k In New String() {"quality", "resolution", "framerate", "bitrateBps"}
                        If r.ValueKind = System.Text.Json.JsonValueKind.Object AndAlso r.TryGetProperty(k, pr) Then
                            d(k) = If(pr.ValueKind = System.Text.Json.JsonValueKind.Number, pr.GetRawText(), pr.GetString())
                        End If
                    Next
                End Using
            Catch
            End Try
            Return d
        End SyncLock
    End Function

    Private Sub SaveOscSettings(d As Dictionary(Of String, String))
        SyncLock OscStoreLock
            Dim j = "{""quality"":" & JStr(d("quality")) & ",""resolution"":" & JStr(d("resolution")) &
                    ",""framerate"":" & d("framerate") & ",""bitrateBps"":" & d("bitrateBps") & "}"
            File.WriteAllText(OscStorePath(), j)
        End SyncLock
    End Sub

    Private Function OscSettingsJson() As String
        Dim d = LoadOscSettings()
        Return "{""quality"":" & JStr(d("quality")) & ",""resolution"":" & JStr(d("resolution")) &
               ",""framerate"":" & d("framerate") & ",""bitrateBps"":" & d("bitrateBps") & "}"
    End Function

    Private Sub ApplyOscSettings(body As String)
        Try
            Dim d = LoadOscSettings()
            Using doc = System.Text.Json.JsonDocument.Parse(If(body, "{}"))
                Dim r = doc.RootElement
                If r.ValueKind = System.Text.Json.JsonValueKind.Object Then
                    Dim pr As System.Text.Json.JsonElement = Nothing
                    If r.TryGetProperty("quality", pr) Then d("quality") = pr.GetString()
                    If r.TryGetProperty("resolution", pr) Then d("resolution") = pr.GetString()
                    If r.TryGetProperty("framerate", pr) Then d("framerate") = pr.GetInt32().ToString()
                    If r.TryGetProperty("bitrateBps", pr) Then d("bitrateBps") = pr.GetInt64().ToString()
                End If
            End Using
            SaveOscSettings(d)
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] osc settings parse: {ex.Message}")
        End Try
    End Sub

    ' Snapshot ที่ override session config จริง (NextRecordingConfig seam) — "engine config จริง" ของ OSC plane
    Private Function LoadOscSnapshot() As RecordSettingsSnapshot
        Dim d = LoadOscSettings()
        Dim snap As New RecordSettingsSnapshot With {
            .Quality = d("quality"), .Resolution = d("resolution"),
            .Framerate = CInt(Val(d("framerate"))), .BitrateBps = CInt(Val(d("bitrateBps")))
        }
        If d("resolution") = "In-game" Then
            snap.NativeResolution = True
        Else
            snap.NativeResolution = False
            Select Case d("resolution")
                Case "2160p 4K" : snap.Width = 3840 : snap.Height = 2160
                Case "1440p HD" : snap.Width = 2560 : snap.Height = 1440
                Case "1080p HD" : snap.Width = 1920 : snap.Height = 1080
                Case "720p HD" : snap.Width = 1280 : snap.Height = 720
                Case "480p" : snap.Width = 854 : snap.Height = 480
                Case "360p" : snap.Width = 640 : snap.Height = 360
                Case "240p" : snap.Width = 426 : snap.Height = 240
                Case Else : snap.NativeResolution = True
            End Select
        End If
        DebugLog($"[CaptureRestApi] snapshot: q={snap.Quality} res={snap.Resolution} fps={snap.Framerate} bps={snap.BitrateBps}")
        Return snap
    End Function

    Private Function JStr(v As String) As String
        If v Is Nothing Then Return """"""
        Return """" & v.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, "").Replace(vbLf, "") & """"
    End Function
End Class
