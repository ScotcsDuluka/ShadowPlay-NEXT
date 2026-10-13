Imports System.IO
Imports System.Net.Sockets
Imports System.Threading

Public Class TcpClientHelper
    Implements IDisposable

    Private _client As TcpClient
    Private _writer As StreamWriter
    Private _reader As StreamReader
    Private _isConnected As Boolean
    Private _cts As CancellationTokenSource
    Private _writeLock As New Object()

    Private ReadOnly _appName As String
    Private ReadOnly _host As String
    Private ReadOnly _port As Integer
    Private ReadOnly _autoReconnect As Boolean
    Private ReadOnly _reconnectIntervalMs As Integer = 3000
    Private _currentReconnectDelay As Integer = 1000

    Public Event OnMessageReceived(msg As String)
    Public Event OnDisconnected()
    Public Event OnReconnecting()

    ''' <summary>
    ''' ★ v9 evidence fix: raised ONCE after a reconnect succeeds (ReconnectLoop).
    ''' The initial successful Connect() does NOT raise this (handlers wire at
    ''' construction and run their own first-time setup).
    ''' Consumers re-register / re-announce on this event — e.g. the Engine
    ''' re-broadcasts engine_ready, which makes the Overlay re-send
    ''' PREWARM_FFMPEG after a hub restart.
    ''' </summary>
    Public Event OnReconnected()

    Public Sub New(appName As String,
                   Optional host As String = "127.0.0.1",
                   Optional port As Integer = 5001, ' NVIDIA API hub (Duluka.Server owns 5000 — public Funnel binds there)
                   Optional autoReconnect As Boolean = True)

        _appName = appName
        _host = host
        _port = port
        _autoReconnect = autoReconnect

        Connect()
    End Sub

    Public Sub Connect()
        Try
            Disconnect()

            _cts = New CancellationTokenSource()

            _client = New TcpClient()
            _client.Connect(_host, _port)

            Dim stream As NetworkStream = _client.GetStream()
            _writer = New StreamWriter(stream) With {.AutoFlush = True}
            _reader = New StreamReader(stream)

            _isConnected = True

            Task.Run(AddressOf ListenLoop)
            Task.Run(AddressOf PingLoop)

        Catch ex As Exception
            _isConnected = False

            If _autoReconnect Then
                Task.Run(AddressOf ReconnectLoop)
            End If
        End Try
    End Sub

    Public Sub Disconnect()
        _isConnected = False
        If _cts IsNot Nothing Then
            Try : _cts.Cancel() : Catch : End Try
        End If
        If _client IsNot Nothing Then
            Try : _client.Close() : Catch : End Try
        End If
        _client = Nothing
    End Sub

    Public Sub Send(cmd As String, Optional value As String = "")
        If Not IsConnected Then Exit Sub

        Try
            SyncLock _writeLock
                Dim msg As String

                If value = "" Then
                    msg = $"[Send] {_appName}|{cmd}"
                Else
                    msg = $"[Send] {_appName}|{cmd}:{value}"
                End If

                _writer.WriteLine(msg)
            End SyncLock
        Catch ex As Exception
            Debug.WriteLine($"TcpClientHelper.Send Error ({_appName}): {ex.Message}")
            _isConnected = False
        End Try
    End Sub

    Public Sub SendLog(message As String)
        If Not IsConnected Then Exit Sub

        Try
            SyncLock _writeLock
                _writer.WriteLine($"[Receive] {_appName}|{message}")
            End SyncLock
        Catch ex As Exception
            Debug.WriteLine($"TcpClientHelper.SendLog Error ({_appName}): {ex.Message}")
            _isConnected = False
        End Try
    End Sub

    Public ReadOnly Property IsConnected As Boolean
        Get
            Return _isConnected AndAlso _client IsNot Nothing AndAlso _client.Connected
        End Get
    End Property

    Private Sub ListenLoop()
        Try
            While _isConnected AndAlso Not _cts.IsCancellationRequested
                Dim msg = _reader.ReadLine()
                If msg Is Nothing Then Exit While

                If msg = "[System]|pong" Then Continue While

                RaiseEvent OnMessageReceived(msg)
            End While
        Catch ex As Exception
            Debug.WriteLine($"TcpClientHelper.ListenLoop Error ({_appName}): {ex.Message}")
        End Try

        _isConnected = False
        RaiseEvent OnDisconnected()

        If _autoReconnect Then
            Task.Run(AddressOf ReconnectLoop)
        End If
    End Sub

    Private Sub PingLoop()
        Try
            While _isConnected AndAlso Not _cts.IsCancellationRequested
                Thread.Sleep(10000)

                If Not IsConnected Then Exit While

                Try
                    SyncLock _writeLock
                        _writer.WriteLine($"[Send] {_appName}|ping")
                    End SyncLock
                Catch
                    Exit While
                End Try
            End While
        Catch ex As Exception
            Debug.WriteLine($"TcpClientHelper.PingLoop Error ({_appName}): {ex.Message}")
        End Try
    End Sub

    Private Sub ReconnectLoop()
        RaiseEvent OnReconnecting()

        While Not IsConnected AndAlso _autoReconnect
            Try
                ' ✅ M12 FIX: old code doubled _currentReconnectDelay BEFORE
                ' the first sleep, so the first reconnect waited 2s instead
                ' of 1s. Now sleep first, then double for next iteration.
                Thread.Sleep(_currentReconnectDelay)
                _currentReconnectDelay = Math.Min(_currentReconnectDelay * 2, 30000) ' max 30s

                ' ✅ M7 FIX: dispose old _cts / _writer / _reader before
                ' replacing them. Old code just overwrote the references,
                ' leaking CancellationTokenSource objects (hold internal
                ' timers) and StreamReader/StreamWriter (hold NetworkStream).
                If _cts IsNot Nothing Then
                    Try : _cts.Cancel() : Catch : End Try
                    Try : _cts.Dispose() : Catch : End Try
                End If
                If _writer IsNot Nothing Then
                    Try : _writer.Dispose() : Catch : End Try
                End If
                If _reader IsNot Nothing Then
                    Try : _reader.Dispose() : Catch : End Try
                End If
                If _client IsNot Nothing Then
                    Try : _client.Close() : Catch : End Try
                End If

                _cts = New CancellationTokenSource()
                _client = New TcpClient()
                _client.Connect(_host, _port)

                ' ← แก้: ประกาศ type ชัดเจน
                Dim stream As NetworkStream = _client.GetStream()
                _writer = New StreamWriter(stream) With {.AutoFlush = True}
                _reader = New StreamReader(stream)

                _isConnected = True
                _currentReconnectDelay = 1000 ' ★ Reset backoff on successful connection

                Task.Run(AddressOf ListenLoop)
                Task.Run(AddressOf PingLoop)

                ' ★ v9 evidence fix: announce the reconnect so consumers can
                ' re-register / re-announce (Engine → engine_ready, which also
                ' triggers the Overlay's PREWARM_FFMPEG re-send). Without this,
                ' a hub restart left the engine silently connected forever.
                Try
                    RaiseEvent OnReconnected()
                Catch ex As Exception
                    Debug.WriteLine($"TcpClientHelper.OnReconnected handler error ({_appName}): {ex.Message}")
                End Try
                Return

            Catch ex As Exception
                Debug.WriteLine($"TcpClientHelper.ReconnectLoop Error ({_appName}): {ex.Message}")
                _isConnected = False
                Try : _client.Close() : Catch ex2 As Exception : Debug.WriteLine($"TcpClientHelper.ReconnectLoop cleanup Error: {ex2.Message}") : End Try
            End Try
        End While
    End Sub

    ''' <summary>★ FIX: Implements IDisposable for proper cleanup</summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        Disconnect()
    End Sub

End Class