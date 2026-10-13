Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Video

Namespace CaptureEngine.Encoder.Tests
    ''' <summary>
    ''' Shared test helpers: Assert, FakeVideoFrame, TestConfig factory.
    ''' </summary>
    Friend Module TestHelpers
        ''' <summary>Throw InvalidOperationException if condition is false.</summary>
        Public Sub Assert(condition As Boolean, message As String)
            If Not condition Then
                Throw New InvalidOperationException("Assertion failed: " & message)
            End If
        End Sub

        ''' <summary>Assert two values are equal (Object.Equals).</summary>
        Public Sub AssertEqual(Of T)(expected As T, actual As T, message As String)
            If Not Object.Equals(expected, actual) Then
                Throw New InvalidOperationException(
                    "Assertion failed: " & message &
                    " (expected=" & If(expected Is Nothing, "null", expected.ToString()) &
                    ", actual=" & If(actual Is Nothing, "null", actual.ToString()) & ")")
            End If
        End Sub

        ''' <summary>Assert that action throws exception of type TEx.</summary>
        Public Sub AssertThrows(Of TEx As Exception)(action As Action, message As String)
            Try
                action()
                Throw New InvalidOperationException(
                    "Expected " & GetType(TEx).Name & " but no exception was thrown: " & message)
            Catch ex As TEx
                ' Expected
            Catch ex As Exception
                Throw New InvalidOperationException(
                    "Wrong exception type for: " & message &
                    " — expected " & GetType(TEx).Name &
                    ", got " & ex.GetType().Name, ex)
            End Try
        End Sub

        ''' <summary>Spin-wait up to timeoutMs for predicate to return true.</summary>
        Public Function SpinWaitFor(predicate As Func(Of Boolean), timeoutMs As Integer, Optional pollMs As Integer = 10) As Boolean
            Dim sw As New Stopwatch()
            sw.Start()
            Do While sw.ElapsedMilliseconds < timeoutMs
                If predicate() Then Return True
                Thread.Sleep(pollMs)
            Loop
            Return predicate()
        End Function

        ''' <summary>Default test config (NVENC_H264, valid parameters).</summary>
        Public Function CreateDefaultConfig() As EncoderConfig
            Return New EncoderConfig()
        End Function

        ''' <summary>Create a FakeVideoFrame with given sequence + PTS.</summary>
        Public Function CreateFrame(seq As Long, pts As Long,
                                    Optional width As Integer = 1920,
                                    Optional height As Integer = 1080,
                                    Optional format As VideoPixelFormat = VideoPixelFormat.Bgra8) As FakeVideoFrame
            Dim dims As New VideoFrameDimensions(width, height)
            Dim diag As New FrameDiagnostics(seq, pts, pts)
            Return New FakeVideoFrame(VideoFrameOrigin.CpuMemory, format, dims, diag)
        End Function
    End Module

    ''' <summary>
    ''' Minimal IVideoFrame implementation for encoder tests. Tracks Dispose
    ''' calls so tests can assert that encoder did NOT dispose the input.
    ''' </summary>
    Public NotInheritable Class FakeVideoFrame
        Implements IVideoFrame

        Private ReadOnly _origin As VideoFrameOrigin
        Private ReadOnly _pixelFormat As VideoPixelFormat
        Private ReadOnly _dimensions As VideoFrameDimensions
        Private ReadOnly _diagnostics As FrameDiagnostics
        Private _disposeCount As Integer = 0

        Public Sub New(origin As VideoFrameOrigin,
                       pixelFormat As VideoPixelFormat,
                       dimensions As VideoFrameDimensions,
                       diagnostics As FrameDiagnostics)
            _origin = origin
            _pixelFormat = pixelFormat
            _dimensions = dimensions
            _diagnostics = diagnostics
        End Sub

        Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
            Get
                Return _origin
            End Get
        End Property

        Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
            Get
                Return _pixelFormat
            End Get
        End Property

        Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
            Get
                Return _dimensions
            End Get
        End Property

        Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
            Get
                Return _diagnostics
            End Get
        End Property

        Public ReadOnly Property DisposeCount As Integer
            Get
                Return Volatile.Read(_disposeCount)
            End Get
        End Property

        Public ReadOnly Property IsDisposed As Boolean
            Get
                Return Volatile.Read(_disposeCount) > 0
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            Volatile.Write(_disposeCount, Volatile.Read(_disposeCount) + 1)
        End Sub
    End Class
End Namespace
