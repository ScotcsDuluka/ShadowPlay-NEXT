Option Strict On
Option Explicit On
Option Infer On

' DisposerTests.vb — regression coverage for DeferredVideoFrameDisposer.
'
' Pins three real defects found in the 2026-09-05 forensic audit:
'   1. Enqueue racing CompleteAndWait could drop a frame into a queue nobody
'      drains (pinned D3D11 texture leak) and then ObjectDisposedException on
'      _wake.Set() — thrown onto a producer thread.
'   2. CompleteAndWait threw TimeoutException on a slow (not hung) drain,
'      which aborted CaptureSession finalization (skipped encoder stop, audio
'      finalize, mux finalize) and reported a healthy recording failed.
'   3. Dispose after a timed-out CompleteAndWait disposed _wake that producers
'      were still using.
'
' The disposer source is LINKED (not copied) into this assembly — same
' production file the Engine compiles — so the tests exercise the real logic.

Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports CaptureEngine.Video

Namespace CaptureEngine.Recording.Tests

    Friend Module DisposerTests

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── DeferredVideoFrameDisposer (GPU frame retirement) ──")
            TestRunner.RunTest("DISPOSER: drains every frame exactly once", AddressOf Test_DrainsAllExactlyOnce)
            TestRunner.RunTest("DISPOSER: enqueue racing CompleteAndWait loses no frame", AddressOf Test_EnqueueRace)
            TestRunner.RunTest("DISPOSER: enqueue after stop disposes directly (no throw)", AddressOf Test_EnqueueAfterStop)
            TestRunner.RunTest("DISPOSER: CompleteAndWait never throws on repeated calls", AddressOf Test_CompleteAndWaitIdempotent)
        End Sub

        ''' <summary>Minimal IVideoFrame stand-in with one-shot Dispose
        ''' semantics mirroring D3D11VideoFrame (Interlocked guard).</summary>
        Private NotInheritable Class FakeFrame
            Implements IVideoFrame

            Private _disposedState As Integer = 0
            Public ReadOnly Property DisposeCalls As Integer
                Get
                    Return Volatile.Read(_disposedState)
                End Get
            End Property

            Public Sub Dispose() Implements IDisposable.Dispose
                Interlocked.Increment(_disposedState)
            End Sub

            Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
                Get
                    Return VideoFrameOrigin.GpuD3D11Texture
                End Get
            End Property

            Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
                Get
                    Return VideoPixelFormat.Bgra8
                End Get
            End Property

            Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
                Get
                    Return New VideoFrameDimensions(16, 16)
                End Get
            End Property

            Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
                Get
                    Return New FrameDiagnostics(0, 0, 0)
                End Get
            End Property
        End Class

        Private Sub Test_DrainsAllExactlyOnce()
            Dim disposer As New CaptureEngine.Recording.DeferredVideoFrameDisposer()
            Dim frames As New List(Of FakeFrame)()
            For i As Integer = 1 To 200
                Dim f As New FakeFrame()
                frames.Add(f)
                disposer.Enqueue(f)
            Next
            disposer.CompleteAndWait()
            For Each f As FakeFrame In frames
                TestRunner.Assert(f.DisposeCalls = 1, $"frame disposed exactly once (got {f.DisposeCalls})")
            Next
            disposer.Dispose()
        End Sub

        ''' <summary>The core regression: producers enqueue while another thread
        ''' transitions the disposer to stopping. EVERY frame must still be
        ''' disposed exactly once and no producer may observe an exception
        ''' (the old lock-free check-then-act failed both).</summary>
        Private Sub Test_EnqueueRace()
            Dim disposer As New CaptureEngine.Recording.DeferredVideoFrameDisposer()
            Dim allFrames As New Concurrent.ConcurrentQueue(Of FakeFrame)()
            Dim producerErrors As New Concurrent.ConcurrentQueue(Of String)()

            Dim stopTask As Task = Task.Run(Sub()
                                                Thread.Sleep(15)
                                                disposer.CompleteAndWait()
                                            End Sub)

            Dim producers(7) As Task
            For p As Integer = 0 To producers.Length - 1
                producers(p) = Task.Run(Sub()
                                            Try
                                                For i As Integer = 1 To 250
                                                    Dim f As New FakeFrame()
                                                    allFrames.Enqueue(f)
                                                    disposer.Enqueue(f)
                                                Next
                                            Catch ex As Exception
                                                producerErrors.Enqueue(ex.ToString())
                                            End Try
                                        End Sub)
            Next

            Task.WaitAll(producers)
            stopTask.Wait(20000)
            disposer.Dispose()

            TestRunner.Assert(producerErrors.IsEmpty,
                              $"no producer exceptions (got {producerErrors.Count}: {If(producerErrors.IsEmpty, "", producerErrors.ToArray()(0))})")

            Dim notDisposed As Long = 0
            Dim multiDisposed As Long = 0
            For Each f As FakeFrame In allFrames
                If f.DisposeCalls = 0 Then notDisposed += 1
                If f.DisposeCalls > 1 Then multiDisposed += 1
            Next
            TestRunner.Assert(notDisposed = 0, $"every frame disposed (missed {notDisposed} of {allFrames.Count})")
            TestRunner.Assert(multiDisposed = 0, $"no double-dispose (extra {multiDisposed} of {allFrames.Count})")
        End Sub

        Private Sub Test_EnqueueAfterStop()
            Dim disposer As New CaptureEngine.Recording.DeferredVideoFrameDisposer()
            disposer.CompleteAndWait()
            Dim f As New FakeFrame()
            ' Must not throw (old code: ObjectDisposedException on _wake.Set()
            ' for the stopping-but-not-yet-disposed window) and must dispose
            ' the frame directly since no worker drains anymore.
            disposer.Enqueue(f)
            TestRunner.Assert(f.DisposeCalls = 1, "late frame disposed directly")
            disposer.Dispose()
            Dim g As New FakeFrame()
            disposer.Enqueue(g)
            TestRunner.Assert(g.DisposeCalls = 1, "post-dispose frame disposed directly")
        End Sub

        Private Sub Test_CompleteAndWaitIdempotent()
            Dim disposer As New CaptureEngine.Recording.DeferredVideoFrameDisposer()
            Dim f As New FakeFrame()
            disposer.Enqueue(f)
            disposer.CompleteAndWait()
            ' The old code threw TimeoutException from the SECOND call path
            ' (Dispose → CompleteAndWait) once the worker had already exited —
            ' verify both call orders complete without throwing.
            disposer.CompleteAndWait()
            disposer.Dispose()
            TestRunner.Assert(f.DisposeCalls = 1, "frame disposed exactly once across repeated CompleteAndWait")
        End Sub

    End Module

End Namespace
