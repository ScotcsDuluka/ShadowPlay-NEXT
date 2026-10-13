Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Diagnostics
    ''' <summary>
    ''' Minimal synchronous logger for the CaptureEngine Foundation.
    '''
    ''' Output format:
    '''   [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] source message
    '''
    ''' Example:
    '''   [2026-08-17 01:23:45.123] [INFO] CaptureEngine started
    '''
    ''' Design notes (Phase 0 / Foundation):
    '''   - Synchronous on purpose — predictability over throughput.
    '''   - No file rotation, no async queues, no DI plumbing.
    '''   - Thread-safe via a single global lock so concurrent log calls
    '''     never interleave inside a single line.
    ''' </summary>
    Public NotInheritable Class EngineLogger
        Private Shared ReadOnly _lineSync As New Object()

        ''' <summary>Log severity levels, ordered from most verbose to most severe.</summary>
        Public Enum LogLevel
            Debug = 0
            Info = 1
            Warning = 2
            [Error] = 3
        End Enum

        Private ReadOnly _source As String
        Private ReadOnly _minLevel As LogLevel
        Private ReadOnly _sink As Action(Of String)

        ''' <summary>
        ''' Construct a logger.
        ''' </summary>
        ''' <param name="source">Short tag prepended to every line (e.g. "CaptureEngine").</param>
        ''' <param name="minLevel">Minimum severity to emit.</param>
        ''' <param name="sink">Output sink. Defaults to <see cref="Console.WriteLine"/> when null.</param>
        Public Sub New(source As String,
                       Optional minLevel As LogLevel = LogLevel.Info,
                       Optional sink As Action(Of String) = Nothing)
            If String.IsNullOrEmpty(source) Then
                Throw New ArgumentException("Logger source must not be null or empty.", NameOf(source))
            End If
            _source = source
            _minLevel = minLevel
            _sink = If(sink, AddressOf Console.WriteLine)
        End Sub

        Public ReadOnly Property Source As String
            Get
                Return _source
            End Get
        End Property

        Public ReadOnly Property MinimumLevel As LogLevel
            Get
                Return _minLevel
            End Get
        End Property

        Public Sub Debug(message As String)
            Write(LogLevel.Debug, message, Nothing)
        End Sub

        Public Sub Info(message As String)
            Write(LogLevel.Info, message, Nothing)
        End Sub

        Public Sub Warning(message As String)
            Write(LogLevel.Warning, message, Nothing)
        End Sub

        Public Sub [Error](message As String)
            Write(LogLevel.Error, message, Nothing)
        End Sub

        Public Sub [Error](message As String, ex As Exception)
            If ex Is Nothing Then
                Write(LogLevel.Error, message, Nothing)
            Else
                Dim suffix As String = " :: " & ex.GetType().FullName & ": " & ex.Message
                Write(LogLevel.Error, message & suffix, ex)
            End If
        End Sub

        Private Sub Write(level As LogLevel, message As String, ex As Exception)
            If level < _minLevel Then Return
            If message Is Nothing Then message = String.Empty

            Dim timestamp As String = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            Dim levelTag As String = FormatLevelTag(level)
            Dim line As String = "[" & timestamp & "] [" & levelTag & "] " & _source & " " & message

            SyncLock _lineSync
                _sink(line)
            End SyncLock
        End Sub

        Private Shared Function FormatLevelTag(level As LogLevel) As String
            Select Case level
                Case LogLevel.Debug : Return "DEBUG"
                Case LogLevel.Info : Return "INFO"
                Case LogLevel.Warning : Return "WARNING"
                Case LogLevel.Error : Return "ERROR"
                Case Else : Return level.ToString().ToUpperInvariant()
            End Select
        End Function
    End Class
End Namespace
