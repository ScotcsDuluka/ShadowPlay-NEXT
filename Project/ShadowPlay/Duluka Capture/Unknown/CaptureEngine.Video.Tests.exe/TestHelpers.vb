Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video.Backends.Fake

Namespace CaptureEngine.Video.Tests
    ''' <summary>
    ''' Test helpers shared across all test categories.
    ''' </summary>
    Friend Module TestHelpers
        ''' <summary>Build a default fake backend that emits FrameAvailable results forever.</summary>
        Public Function CreateDefaultFake() As FakeVideoCaptureBackend
            Return New FakeVideoCaptureBackend(
                New EngineLogger("FakeVideoCaptureBackend", EngineLogger.LogLevel.Warning))
        End Function

        Public Function CreateContext(Optional kind As VideoBackendKind = VideoBackendKind.Ddagrab) As FakeVideoBackendContext
            Return New FakeVideoBackendContext(
                kind,
                New EngineLogger("FakeBackendCtx", EngineLogger.LogLevel.Warning))
        End Function

        Public Sub Assert(condition As Boolean, message As String)
            If Not condition Then
                Throw New InvalidOperationException("ASSERT FAILED: " & message)
            End If
        End Sub

        Public Sub AssertEqual(Of T)(expected As T, actual As T, message As String)
            If Not EqualityComparer(Of T).Default.Equals(expected, actual) Then
                Throw New InvalidOperationException(
                    "ASSERT FAILED: " & message &
                    " (expected=" & If(expected Is Nothing, "null", expected.ToString()) &
                    ", actual=" & If(actual Is Nothing, "null", actual.ToString()) & ")")
            End If
        End Sub

        Public Sub AssertThrows(Of TEx As Exception)(action As Action, message As String)
            Try
                action()
            Catch ex As Exception
                If GetType(TEx).IsAssignableFrom(ex.GetType()) Then
                    Return
                End If
                Throw New InvalidOperationException(
                    "ASSERT FAILED: " & message &
                    " — wrong exception type. Expected " & GetType(TEx).Name &
                    ", got " & ex.GetType().Name & ": " & ex.Message)
            End Try
            Throw New InvalidOperationException(
                "ASSERT FAILED: " & message & " — no exception was thrown. Expected " & GetType(TEx).Name)
        End Sub

        ''' <summary>Spin-wait until predicate is true, with timeout.</summary>
        Public Function SpinWaitFor(predicate As Func(Of Boolean), timeoutMs As Integer, Optional pollMs As Integer = 5) As Boolean
            Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)
            Do While DateTime.UtcNow < deadline
                If predicate() Then Return True
                Thread.Sleep(pollMs)
            Loop
            Return predicate()
        End Function
    End Module
End Namespace
