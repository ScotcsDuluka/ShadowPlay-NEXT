' [Overlay] Client.vb
' ✅ P2.3: Engine-side TCP client — uses the same shared TcpClientHelper
' as Overlay / Notifier / App Experience. No more bespoke EngineHubClient.
'
' This is a Partial Class of UI_Engine. It owns:
'   - Public Shared tcp As TcpClientHelper
'   - OnMessage parser (replaces EngineHubClient.ProcessMessage)
'   - SendResponse helper (replaces EngineHubClient.SendResponse)
'
' Message format on the wire:
'   [Send] <AppName>|<cmd>[:<value>]
'   [Receive] <AppName>|<msg>
'   [System]|pong
'
' Engine listens for:
'   - PREWARM_FFMPEG:<ffmpegPath>[|<encoderName>]
'   - engine_config_changed[:video|config]
'   - RECORD_START:<outputPath>        (legacy alias → engine_record_start)
'   - RECORD_STOP                      (legacy alias → engine_record_stop)
'   - REPLAY_START:<seconds>           (legacy alias → engine_replay_start)
'   - REPLAY_STOP                      (legacy alias → engine_replay_stop)
'   - REPLAY_SAVE:<path>;<duration>    (legacy alias → engine_replay_save)
'   - engine_get_status
'   - engine_load_config
'   - engine_set_encoder:<value>
'
' Engine sends:
'   - register:NVIDIA Engine       (on connect)
'   - engine_ready                 (broadcast after connect)
'   - ping                         (every 10s)
'   - engine_response:<cmd>,<status>[,<data>][,req=<reqId>]

Imports System.Diagnostics

Partial Public Class UI_Engine

    ''' <summary>Shared TCP helper — accessible from all UI_Engine partials.</summary>
    Public Shared tcp As TcpClientHelper

    ' ── Connection lifecycle ───────────────────────────────────

    Private Sub StartTcpClient()
        Try
            ' Host/port/autoReconnect come from the TcpClientHelper defaults —
            ' the single client-side authority for the NVIDIA API hub port.
            tcp = New TcpClientHelper("NVIDIA Engine")
            AddHandler tcp.OnMessageReceived, AddressOf OnTcpMessage
            AddHandler tcp.OnDisconnected, AddressOf OnTcpDisconnected
            AddHandler tcp.OnReconnecting, AddressOf OnTcpReconnecting
            ' ★ v9 evidence fix: after a reconnect (e.g. hub restart), re-register
            ' and re-broadcast engine_ready. Previously register/engine_ready ran
            ' only once at app start — an engine that started BEFORE the hub (or
            ' survived a hub restart) stayed silently connected forever: the hub
            ' client list showed Unknown, and the Overlay never re-sent
            ' PREWARM_FFMPEG (it keys off engine_ready). Found by test-autospawn
            ' v9 Phase P (engine_ready never arrived for an engine spawned while
            ' the hub was down).
            AddHandler tcp.OnReconnected, AddressOf OnTcpReconnected
            DebugLog("TCP client starting...")
        Catch ex As Exception
            DebugLog("TCP client error: " & ex.Message)
        End Try
    End Sub

    Private Sub OnTcpDisconnected()
        ' ✅ P2.6: use BeginInvoke (fire-and-forget) instead of Invoke.
        ' OnTcpDisconnected runs on the listener thread; using Me.Invoke would
        ' block the listener until the UI thread processes it, which could
        ' delay incoming messages. Same fix as OnTcpMessage.
        BeginUiInvoke(Sub()
                      End Sub)
    End Sub

    Private Sub OnTcpReconnecting()
        ' Could log; for now keep silent to avoid log spam every retry.
    End Sub

    ''' <summary>
    ''' ★ v9 evidence fix: TCP just reconnected (hub came back / restarted).
    ''' Re-register our name and re-broadcast engine_ready so the hub client
    ''' list is correct and the Overlay re-sends PREWARM_FFMPEG. Marshal to the
    ''' UI thread via BeginUiInvoke (fire-and-forget) — ReconnectLoop thread
    ''' must not block on UI.
    ''' </summary>
    Private Sub OnTcpReconnected()
        BeginUiInvoke(Sub()
                          Try
                              BroadcastEngineReady()
                              DebugLog("[Engine] reconnected → re-broadcast engine_ready")
                          Catch ex As Exception
                              DebugLog("[Engine] reconnect broadcast failed: " & ex.Message)
                          End Try
                      End Sub)
    End Sub

    ''' <summary>
    ''' Called after a successful connect (detected via first register ack or
    ''' any time we want to broadcast engine_ready). Public so UI_Engine_Load
    '  can call it after StartTcpClient.
    ''' </summary>
    Private Sub BroadcastEngineReady()
        Try
            If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                ' Send register first so Hub knows who we are.
                tcp.Send("register", "NVIDIA Engine")
                tcp.Send("engine_ready")
                DebugLog("[Engine] broadcast engine_ready")
            End If
        Catch ex As Exception
            DebugLog("[Engine] engine_ready broadcast failed: " & ex.Message)
        End Try
    End Sub

    ' ── SendResponse helper ────────────────────────────────────

    ''' <summary>
    ''' Send a response back to whoever sent the command (via Hub broadcast).
    ''' Format: engine_response:&lt;command&gt;,&lt;status&gt;[,&lt;data&gt;][,req=&lt;reqId&gt;]
    ''' </summary>
    Public Sub SendResponse(command As String, status As String, Optional data As String = "", Optional requestId As String = Nothing)
        If tcp Is Nothing Then Return
        Dim value As String = command & "," & status
        If Not String.IsNullOrEmpty(data) Then
            value &= "," & data
        End If
        If Not String.IsNullOrEmpty(requestId) Then
            value &= ",req=" & requestId
        End If
        tcp.Send("engine_response", value)
    End Sub

    ' ── Message parser ─────────────────────────────────────────

    Public Sub OnTcpMessage(msg As String)
        ' ✅ P2.4: Do NOT use Me.Invoke here — it blocks the listener thread
        ' until the UI thread processes the message. If the UI thread is busy
        ' (e.g. RefreshOverlayConfigUI triggered by file-poll, or HandleEnginePrewarmFFmpeg
        ' which itself uses Me.Invoke), every subsequent TCP message gets
        ' queued behind it. RECORD_START would be delayed or lost.
        ' Instead, parse on the listener thread (no UI access) and only
        ' dispatch to UI thread for the actual handler execution via BeginInvoke
        ' (fire-and-forget).

        Try
            If String.IsNullOrEmpty(msg) OrElse Not msg.Contains("|") Then Return

            Dim parts As String() = msg.Split("|"c)
            If parts.Length < 2 Then Return

            ' ✅ M9 FIX: self-filter — old code used .Contains("NVIDIA Engine")
            ' which would also match "NVIDIA Engine Helper" or a path containing
            ' the string. Now strip the "[Send] "/"[Receive] " prefix and compare
            ' the cleaned app name exactly.
            Dim senderSegment As String = parts(0).Trim()
            senderSegment = senderSegment.Replace("[Send] ", "").Replace("[Receive] ", "").Trim()
            If String.Equals(senderSegment, "NVIDIA Engine", StringComparison.Ordinal) Then Return

            Dim data As String = parts(1)

            Dim colonIndex As Integer = data.IndexOf(":"c)
            Dim cmd, value As String
            If colonIndex >= 0 Then
                cmd = data.Substring(0, colonIndex)
                value = data.Substring(colonIndex + 1)
            Else
                cmd = data
                value = ""
            End If

            ' ── Pre-filter commands (handled BEFORE engine_* normalization) ──

            ' PREWARM_FFMPEG:<path>[|<encoderName>]
            If cmd = "PREWARM_FFMPEG" AndAlso value.Length > 0 Then
                Dim pipeIdx As Integer = value.IndexOf("|"c)
                Dim ffmpegPath As String = If(pipeIdx > 0, value.Substring(0, pipeIdx), value)
                ' Marshal to UI thread but fire-and-forget (don't block listener).
                BeginUiInvoke(Sub() HandleEnginePrewarmFFmpeg(ffmpegPath))
                Return
            End If

            ' engine_config_changed[:video|config]
            If cmd = "engine_config_changed" Then
                BeginUiInvoke(Sub() HandleEngineConfigChanged(value))
                Return
            End If

            ' ── Legacy alias mapping (RECORD_* → engine_*) ──
            Dim canonicalCmd As String = cmd
            Select Case cmd
                Case "RECORD_START" : canonicalCmd = "engine_record_start"
                Case "RECORD_STOP" : canonicalCmd = "engine_record_stop"
                Case "REPLAY_START" : canonicalCmd = "engine_replay_start"
                Case "REPLAY_STOP" : canonicalCmd = "engine_replay_stop"
                Case "REPLAY_SAVE" : canonicalCmd = "engine_replay_save"
            End Select

            ' Only engine_* commands are routed to the handler.
            If Not canonicalCmd.StartsWith("engine_") Then Return
            cmd = canonicalCmd

            ' ── Optional request ID ──
            ' Format: req=<token>|<payload>
            Dim reqId As String = Nothing
            If value.StartsWith("req=", StringComparison.Ordinal) Then
                Dim sepIdx As Integer = value.IndexOf("|"c)
                If sepIdx > 4 Then
                    reqId = value.Substring(4, sepIdx - 4)
                    value = value.Substring(sepIdx + 1)
                End If
            End If

            DebugLog($"[Engine] Received: {cmd}={value}" & If(String.IsNullOrEmpty(reqId), "", $" (req={reqId})"))

            ' Dispatch on UI thread (fire-and-forget) so listener stays free.
            Dim cmdCopy As String = cmd
            Dim valueCopy As String = value
            Dim reqIdCopy As String = reqId
            BeginUiInvoke(Sub() DispatchEngineCommand(cmdCopy, valueCopy, reqIdCopy))

        Catch ex As Exception
            Debug.WriteLine($"UI_Engine.OnTcpMessage error: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Fire-and-forget UI dispatch. Uses BeginInvoke so the caller (listener
    ''' thread) doesn't block waiting for the UI thread. Safe to call even
    ''' if the form handle hasn't been created yet.
    ''' </summary>
    Private Sub BeginUiInvoke(action As Action)
        Try
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            Me.BeginInvoke(action)
        Catch ex As InvalidOperationException
            ' Form handle not created yet — drop the message.
        Catch ex As Exception
            Debug.WriteLine($"BeginUiInvoke error: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Route engine_* commands to their handlers. Extracted from OnTcpMessage
    ''' so it's easy to read and add new commands.
    ''' </summary>
    Private Async Sub DispatchEngineCommand(cmd As String, value As String, reqId As String)
        Try
            Select Case cmd
                Case "engine_record_start"
                    If _useNewEngine AndAlso _recordingEngine IsNot Nothing Then
                        Await HandleRecordingStart(value, reqId)
                    Else
                        ' ★ P13-AUDIO-TIMELINE: loud, greppable pipeline marker.
                        ' Every legacy-pipeline recording MUST announce itself —
                        ' the [speech][speech][apad-silence] owner evidence was
                        ' produced by THIS branch while the live-mux fixes were
                        ' invisible (fallback happened silently at startup).
                        Dim reasonTxt As String = If(String.IsNullOrEmpty(_engineInitFailReason),
                                                     "",
                                                     " — init failed: " & _engineInitFailReason)
                        DebugLog($"★★ PIPELINE = LEGACY CaptureEngine (live-mux NOT active{reasonTxt}) — audio = WAV + apad mux ★★")
                        Await HandleEngineRecordStart(value, reqId)
                    End If
                Case "engine_record_stop"
                    If _useNewEngine AndAlso _recordingEngine IsNot Nothing Then
                        Await HandleRecordingStop(reqId)
                    Else
                        Await HandleEngineRecordStop(reqId)
                    End If
                Case "engine_replay_start"
                    HandleEngineReplayStart(value, reqId)
                Case "engine_replay_stop"
                    HandleEngineReplayStop(reqId)
                Case "engine_replay_save"
                    HandleEngineReplaySave(value, reqId)
                Case "engine_get_status"
                    If _useNewEngine AndAlso _recordingEngine IsNot Nothing Then
                        HandleRecordingGetStatus(reqId)
                    Else
                        HandleEngineGetStatus(reqId)
                    End If
                Case "engine_load_config"
                    HandleEngineLoadConfig(reqId)
                Case "engine_set_encoder"
                    HandleEngineSetEncoder(value, reqId)
                Case Else
                    SendResponse(cmd, "error", "unknown_command", reqId)
            End Select
        Catch ex As Exception
            DebugLog($"DispatchEngineCommand unhandled exception: {ex.Message}")
            Try
                SendResponse(cmd, "error", ex.Message, reqId)
            Catch
            End Try
        End Try
    End Sub

End Class
