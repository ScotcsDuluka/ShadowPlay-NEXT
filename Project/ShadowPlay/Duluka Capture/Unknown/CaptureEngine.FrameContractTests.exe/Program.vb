Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports CaptureEngine.Video.Frames

Namespace CaptureEngine.FrameContractTests
    Friend Module Program
        Private _passed As Integer = 0
        Private _failed As Integer = 0
        Private ReadOnly _failures As New List(Of String)()

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine.Video Frame Contract Tests (P1-D)")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            RunTest("FRAME: Create frame — all properties accessible", AddressOf Test_CreateFrame)
            RunTest("FRAME: Metadata — timestamps, source, flags", AddressOf Test_FrameMetadata)
            RunTest("FRAME: PixelFormat — all 5 values accessible", AddressOf Test_PixelFormat)
            RunTest("FRAME: Resource cleanup callback invoked on Dispose", AddressOf Test_ResourceCleanupCallback)
            RunTest("FRAME: Dispose twice — idempotent, no crash", AddressOf Test_DisposeTwice)
            RunTest("FRAME: Concurrent Dispose — only one cleanup callback fires", AddressOf Test_ConcurrentDispose)
            RunTest("FRAME: Properties readable after Dispose (no crash)", AddressOf Test_ReadAfterDispose)
            RunTest("FRAME: IsDisposed is False before Dispose, True after", AddressOf Test_IsDisposedProperty)

            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine(" Result: " & _passed & " passed, " & _failed & " failed, " & (_passed + _failed) & " total")
            Console.WriteLine("--------------------------------------------------")
            If _failed > 0 Then
                Console.WriteLine()
                Console.WriteLine("Failures:")
                For Each f As String In _failures
                    Console.WriteLine("  - " & f)
                Next
            End If
            Return If(_failed > 0, 1, 0)
        End Function

        Private Sub RunTest(name As String, test As Action)
            Dim paddedName = name
            If paddedName.Length < 70 Then paddedName = paddedName & New String(" "c, 70 - paddedName.Length)
            Console.Write("[" & paddedName & "] ")
            Try
                test()
                _passed += 1
                Console.WriteLine("PASS")
            Catch ex As Exception
                _failed += 1
                _failures.Add(name & ": " & ex.GetType().Name & ": " & ex.Message)
                Console.WriteLine("FAIL")
                Console.WriteLine("    " & ex.GetType().Name & ": " & ex.Message)
            End Try
        End Sub

        Private Sub Assert(cond As Boolean, msg As String)
            If Not cond Then Throw New InvalidOperationException("ASSERT: " & msg)
        End Sub

        ' ===== Tests =====

        Private Sub Test_CreateFrame()
            Dim meta As New FrameMetadata(1000000, 1000000, "test", 0)
            Dim frame As New VideoFrame(
                frameId:=42,
                timestamp:=1000000,
                width:=1920,
                height:=1080,
                pixelFormat:=PixelFormat.BGRA8,
                metadata:=meta,
                resourceHandle:=IntPtr.Zero,
                cleanupCallback:=Nothing)

            Assert(frame.FrameId = 42, "FrameId should be 42")
            Assert(frame.Timestamp = 1000000, "Timestamp should be 1000000")
            Assert(frame.Width = 1920, "Width should be 1920")
            Assert(frame.Height = 1080, "Height should be 1080")
            Assert(frame.PixelFormat = PixelFormat.BGRA8, "PixelFormat should be BGRA8")
            Assert(frame.ResourceHandle = IntPtr.Zero, "ResourceHandle should be Zero")
            Assert(Not frame.IsDisposed, "Should not be disposed yet")
            frame.Dispose()
        End Sub

        Private Sub Test_FrameMetadata()
            Dim meta As New FrameMetadata(
                captureTimestamp:=5000000,
                presentationTimestamp:=5100000,
                source:="ddagrab",
                flags:=42)

            Assert(meta.CaptureTimestamp = 5000000, "CaptureTimestamp should be 5000000")
            Assert(meta.PresentationTimestamp = 5100000, "PTS should be 5100000")
            Assert(meta.Source = "ddagrab", "Source should be 'ddagrab'")
            Assert(meta.Flags = 42, "Flags should be 42")

            ' Test convenience factory
            Dim meta2 = FrameMetadata.Create(999, "test")
            Assert(meta2.CaptureTimestamp = 999, "Create should set CaptureTimestamp")
            Assert(meta2.PresentationTimestamp = 999, "Create should set PTS = CaptureTimestamp")
            Assert(meta2.Source = "test", "Create should set Source")
        End Sub

        Private Sub Test_PixelFormat()
            ' Verify all 5 enum values exist and are accessible
            Dim formats As PixelFormat() = {
                PixelFormat.Unknown,
                PixelFormat.BGRA8,
                PixelFormat.RGBA8,
                PixelFormat.NV12,
                PixelFormat.P010
            }
            Assert(formats.Length = 5, "Should have 5 pixel formats")

            ' Create frames with different formats
            For Each fmt In formats
                Dim frame As New VideoFrame(0, 0, 1, 1, fmt, FrameMetadata.Create(0, "test"))
                Assert(frame.PixelFormat = fmt, "PixelFormat should match: " & fmt.ToString())
                frame.Dispose()
            Next
        End Sub

        Private Sub Test_ResourceCleanupCallback()
            Dim callbackCount As Integer = 0
            Dim capturedFrame As VideoFrame = Nothing

            Dim frame As New VideoFrame(
                frameId:=1,
                timestamp:=0,
                width:=640,
                height:=480,
                pixelFormat:=PixelFormat.BGRA8,
                metadata:=FrameMetadata.Create(0, "test"),
                resourceHandle:=New IntPtr(12345),
                cleanupCallback:=Sub(f As VideoFrame)
                                      Interlocked.Increment(callbackCount)
                                      capturedFrame = f
                                  End Sub)

            ' Before Dispose: callback not called
            Assert(callbackCount = 0, "Callback should not fire before Dispose")
            Assert(Not frame.IsDisposed, "Should not be disposed")

            frame.Dispose()

            ' After Dispose: callback called exactly once
            Assert(callbackCount = 1, "Callback should fire exactly once (got " & callbackCount & ")")
            Assert(frame.IsDisposed, "Should be disposed")
            Assert(capturedFrame IsNot Nothing, "Callback should receive the frame")
            Assert(capturedFrame Is frame, "Callback should receive the same frame instance")
        End Sub

        Private Sub Test_DisposeTwice()
            Dim callbackCount As Integer = 0

            Dim frame As New VideoFrame(
                frameId:=1,
                timestamp:=0,
                width:=320,
                height:=240,
                pixelFormat:=PixelFormat.NV12,
                metadata:=FrameMetadata.Create(0, "test"),
                cleanupCallback:=Sub(f) Interlocked.Increment(callbackCount))

            frame.Dispose()
            Assert(callbackCount = 1, "First Dispose should invoke callback")

            frame.Dispose()  ' Should be a no-op
            frame.Dispose()  ' Should be a no-op
            frame.Dispose()  ' Should be a no-op

            Assert(callbackCount = 1, "Subsequent Dispose should NOT invoke callback again")
            Assert(frame.IsDisposed, "Should still be disposed")
        End Sub

        Private Sub Test_ConcurrentDispose()
            Dim callbackCount As Integer = 0

            Dim frame As New VideoFrame(
                frameId:=1,
                timestamp:=0,
                width:=1280,
                height:=720,
                pixelFormat:=PixelFormat.BGRA8,
                metadata:=FrameMetadata.Create(0, "test"),
                cleanupCallback:=Sub(f)
                                      Interlocked.Increment(callbackCount)
                                      Thread.Sleep(10)  ' Simulate slow cleanup
                                  End Sub)

            ' Dispose from multiple threads concurrently
            Dim tasks As New List(Of Task)()
            For i As Integer = 0 To 9
                tasks.Add(Task.Run(Sub() frame.Dispose()))
            Next
            Task.WaitAll(tasks.ToArray())

            Assert(callbackCount = 1,
                   "Concurrent Dispose should invoke callback exactly once (got " & callbackCount & ")")
            Assert(frame.IsDisposed, "Should be disposed after concurrent calls")
        End Sub

        Private Sub Test_ReadAfterDispose()
            Dim meta As New FrameMetadata(100, 200, "ddagrab", 7)
            Dim handle As New IntPtr(99999)

            Dim frame As New VideoFrame(
                frameId:=77,
                timestamp:=100,
                width:=1920,
                height:=1080,
                pixelFormat:=PixelFormat.P010,
                metadata:=meta,
                resourceHandle:=handle)

            frame.Dispose()
            Assert(frame.IsDisposed, "Should be disposed")

            ' All properties should still be readable after Dispose
            Assert(frame.FrameId = 77, "FrameId should still be 77 after Dispose")
            Assert(frame.Timestamp = 100, "Timestamp should still be 100 after Dispose")
            Assert(frame.Width = 1920, "Width should still be 1920 after Dispose")
            Assert(frame.Height = 1080, "Height should still be 1080 after Dispose")
            Assert(frame.PixelFormat = PixelFormat.P010, "PixelFormat should still be P010 after Dispose")
            Assert(frame.Metadata.CaptureTimestamp = 100, "Metadata.CaptureTimestamp should still be 100")
            Assert(frame.Metadata.PresentationTimestamp = 200, "Metadata.PTS should still be 200")
            Assert(frame.Metadata.Source = "ddagrab", "Metadata.Source should still be 'ddagrab'")
            Assert(frame.Metadata.Flags = 7, "Metadata.Flags should still be 7")
            Assert(frame.ResourceHandle = handle, "ResourceHandle should still be 99999")
        End Sub

        Private Sub Test_IsDisposedProperty()
            Dim frame As New VideoFrame(0, 0, 1, 1, PixelFormat.Unknown, FrameMetadata.Create(0, "test"))

            Assert(Not frame.IsDisposed, "IsDisposed should be False before Dispose")
            frame.Dispose()
            Assert(frame.IsDisposed, "IsDisposed should be True after Dispose")
        End Sub
    End Module
End Namespace
