Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Encoder.Backends.Fake
Imports CaptureEngine.Video

Namespace CaptureEngine.Encoder.Tests.Encode
    ''' <summary>
    ''' Encode tests: happy path, input validation, output metadata,
    ''' determinism, flush, failure.
    ''' </summary>
    Friend NotInheritable Class EncodeTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("ENCODE: happy path — 1 frame → 1 packet", AddressOf Test_EncodeHappyPath)
            runner("ENCODE: Nothing frame → ArgumentNullException", AddressOf Test_EncodeNothingFrame)
            runner("ENCODE: wrong dimensions → EncoderRuntimeException", AddressOf Test_EncodeWrongDimensions)
            runner("ENCODE: wrong pixel format → EncoderRuntimeException", AddressOf Test_EncodeWrongPixelFormat)
            runner("ENCODE: PTS propagation (frame PTS → packet PTS)", AddressOf Test_PtsPropagation)
            runner("ENCODE: sequence propagation (0,1,2,...)", AddressOf Test_SequencePropagation)
            runner("ENCODE: first packet is keyframe", AddressOf Test_FirstPacketKeyframe)
            runner("ENCODE: keyframe cadence (every GopSize)", AddressOf Test_KeyframeCadence)
            runner("ENCODE: deterministic payload (same input → same output)", AddressOf Test_DeterministicPayload)
            runner("ENCODE: encoder does NOT dispose input frame", AddressOf Test_EncoderDoesNotDisposeFrame)
            runner("ENCODE: multiple Encode calls", AddressOf Test_MultipleEncode)
            runner("ENCODE: Flush empty queue → 0 packets", AddressOf Test_FlushEmptyQueue)
            runner("ENCODE: Flush with Nothing sink → ArgumentNullException", AddressOf Test_FlushNothingSink)
            runner("ENCODE: Diagnostics counters correct", AddressOf Test_DiagnosticsCounters)
            runner("ENCODE: invalid config → EncoderConfigurationException", AddressOf Test_InvalidConfig)
            runner("ENCODE: encoder failure → Faulted state", AddressOf Test_EncoderFailureFaulted)
            runner("ENCODE: Encode after Faulted → InvalidOperationException", AddressOf Test_EncodeAfterFaulted)
        End Sub

        Private Shared Function CreateRunningEncoder() As FakeEncoderBackend
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            enc.Start()
            Return enc
        End Function

        Private Shared Sub Test_EncodeHappyPath()
            Dim enc = CreateRunningEncoder()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            Dim result As Boolean = enc.Encode(frame, packet)
            TestHelpers.Assert(result, "Encode should return True for valid frame")
            TestHelpers.Assert(packet IsNot Nothing, "packet should not be Nothing when result=True")
            TestHelpers.Assert(packet.PayloadLength > 0, "payload length should be > 0")
            frame.Dispose()
            packet.Dispose()
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncodeNothingFrame()
            Dim enc = CreateRunningEncoder()
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of ArgumentNullException)(
                Sub() enc.Encode(Nothing, packet),
                "Encode with Nothing frame must throw ArgumentNullException")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncodeWrongDimensions()
            Dim cfg As New EncoderConfig() With {.ExpectedWidth = 1920, .ExpectedHeight = 1080}
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(cfg)
            enc.Start()
            Dim frame = TestHelpers.CreateFrame(0, 1000, width:=1280, height:=720)
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of EncoderRuntimeException)(
                Sub() enc.Encode(frame, packet),
                "Encode with wrong dimensions must throw EncoderRuntimeException")
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.InternalState, "state after encode failure")
            frame.Dispose()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncodeWrongPixelFormat()
            Dim cfg As New EncoderConfig() With {.ExpectedInputFormat = VideoPixelFormat.Bgra8}
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(cfg)
            enc.Start()
            Dim frame = TestHelpers.CreateFrame(0, 1000, format:=VideoPixelFormat.Nv12)
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of EncoderRuntimeException)(
                Sub() enc.Encode(frame, packet),
                "Encode with wrong pixel format must throw EncoderRuntimeException")
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.InternalState, "state after pixel format failure")
            frame.Dispose()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_PtsPropagation()
            Dim enc = CreateRunningEncoder()
            Dim frame = TestHelpers.CreateFrame(42, 12345)
            Dim packet As EncodedPacket = Nothing
            enc.Encode(frame, packet)
            TestHelpers.AssertEqual(12345L, packet.Metadata.PresentationTimestampTicks,
                "packet PTS must match frame PTS")
            frame.Dispose()
            packet.Dispose()
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_SequencePropagation()
            Dim enc = CreateRunningEncoder()
            For i As Integer = 0 To 4
                Dim frame = TestHelpers.CreateFrame(i, i * 1000L)
                Dim packet As EncodedPacket = Nothing
                enc.Encode(frame, packet)
                TestHelpers.AssertEqual(CLng(i), packet.Metadata.Sequence,
                    "packet Sequence must be " & i)
                frame.Dispose()
                packet.Dispose()
            Next
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_FirstPacketKeyframe()
            Dim enc = CreateRunningEncoder()
            Dim frame = TestHelpers.CreateFrame(0, 0)
            Dim packet As EncodedPacket = Nothing
            enc.Encode(frame, packet)
            TestHelpers.Assert(packet.Metadata.IsKeyFrame, "first packet must be keyframe")
            frame.Dispose()
            packet.Dispose()
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_KeyframeCadence()
            Dim cfg As New EncoderConfig() With {.GopSize = 5}
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(cfg)
            enc.Start()
            For i As Integer = 0 To 14
                Dim frame = TestHelpers.CreateFrame(i, i * 1000L)
                Dim packet As EncodedPacket = Nothing
                enc.Encode(frame, packet)
                Dim expectedKeyframe As Boolean = (i = 0) OrElse ((i Mod 5) = 0)
                TestHelpers.AssertEqual(expectedKeyframe, packet.Metadata.IsKeyFrame,
                    "packet " & i & " keyframe flag must be " & expectedKeyframe.ToString())
                frame.Dispose()
                packet.Dispose()
            Next
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_DeterministicPayload()
            Dim enc1 = CreateRunningEncoder()
            Dim enc2 = CreateRunningEncoder()
            Dim frame1 = TestHelpers.CreateFrame(7, 4242)
            Dim frame2 = TestHelpers.CreateFrame(7, 4242)
            Dim p1 As EncodedPacket = Nothing
            Dim p2 As EncodedPacket = Nothing
            enc1.Encode(frame1, p1)
            enc2.Encode(frame2, p2)
            TestHelpers.AssertEqual(p1.PayloadLength, p2.PayloadLength, "payload lengths must match")
            For i As Integer = 0 To p1.PayloadLength - 1
                TestHelpers.AssertEqual(p1.Payload(i), p2.Payload(i),
                    "payload byte " & i & " must match (deterministic)")
            Next
            frame1.Dispose()
            frame2.Dispose()
            p1.Dispose()
            p2.Dispose()
            enc1.Stop() : enc1.Dispose()
            enc2.Stop() : enc2.Dispose()
        End Sub

        Private Shared Sub Test_EncoderDoesNotDisposeFrame()
            Dim enc = CreateRunningEncoder()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            enc.Encode(frame, packet)
            TestHelpers.AssertEqual(0, frame.DisposeCount,
                "encoder MUST NOT dispose input frame (BORROW contract)")
            frame.Dispose()
            TestHelpers.AssertEqual(1, frame.DisposeCount, "caller Dispose should set count to 1")
            packet.Dispose()
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_MultipleEncode()
            Dim enc = CreateRunningEncoder()
            For i As Integer = 0 To 9
                Dim frame = TestHelpers.CreateFrame(i, i * 1000L)
                Dim packet As EncodedPacket = Nothing
                Dim result As Boolean = enc.Encode(frame, packet)
                TestHelpers.Assert(result, "Encode " & i & " should return True")
                TestHelpers.Assert(packet IsNot Nothing, "packet " & i & " should not be Nothing")
                frame.Dispose()
                packet.Dispose()
            Next
            TestHelpers.AssertEqual(10L, enc.Diagnostics.SubmittedFrames, "SubmittedFrames should be 10")
            TestHelpers.AssertEqual(10L, enc.Diagnostics.EncodedPackets, "EncodedPackets should be 10")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_FlushEmptyQueue()
            Dim enc = CreateRunningEncoder()
            Dim count As Integer = enc.Flush(Sub(p) p.Dispose())
            TestHelpers.AssertEqual(0, count, "Flush on empty queue should return 0")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_FlushNothingSink()
            Dim enc = CreateRunningEncoder()
            TestHelpers.AssertThrows(Of ArgumentNullException)(
                Sub() enc.Flush(Nothing),
                "Flush with Nothing sink must throw ArgumentNullException")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_DiagnosticsCounters()
            Dim enc = CreateRunningEncoder()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            enc.Encode(frame, packet)
            TestHelpers.AssertEqual(1L, enc.Diagnostics.SubmittedFrames, "SubmittedFrames == 1")
            TestHelpers.AssertEqual(1L, enc.Diagnostics.EncodedPackets, "EncodedPackets == 1")
            TestHelpers.AssertEqual(0L, enc.Diagnostics.DroppedFrames, "DroppedFrames == 0")
            TestHelpers.AssertEqual(0L, enc.Diagnostics.ErrorCount, "ErrorCount == 0")
            frame.Dispose()
            packet.Dispose()
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_InvalidConfig()
            Dim cfg As New EncoderConfig() With {.BitrateBps = 0}
            Dim enc As New FakeEncoderBackend()
            TestHelpers.AssertThrows(Of EncoderConfigurationException)(
                Sub() enc.Initialize(cfg),
                "Initialize with BitrateBps=0 must throw EncoderConfigurationException")
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.InternalState, "state after invalid config")
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncoderFailureFaulted()
            Dim cfg As New EncoderConfig() With {.ExpectedWidth = 1920, .ExpectedHeight = 1080}
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(cfg)
            enc.Start()
            Dim frame = TestHelpers.CreateFrame(0, 0, width:=640, height:=480)
            Dim packet As EncodedPacket = Nothing
            Try
                enc.Encode(frame, packet)
                Throw New InvalidOperationException("Expected EncoderRuntimeException was not thrown")
            Catch ex As EncoderRuntimeException
                ' Expected
            End Try
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.InternalState, "state after failure")
            TestHelpers.Assert(enc.Diagnostics.ErrorCount >= 1, "ErrorCount must be >= 1")
            TestHelpers.AssertEqual("runtime", enc.Diagnostics.LastErrorType, "LastErrorType must be 'runtime'")
            frame.Dispose()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncodeAfterFaulted()
            Dim cfg As New EncoderConfig() With {.ExpectedWidth = 1920}
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(cfg)
            enc.Start()
            Dim badFrame = TestHelpers.CreateFrame(0, 0, width:=640, height:=480)
            Dim packet1 As EncodedPacket = Nothing
            Try
                enc.Encode(badFrame, packet1)
            Catch ex As EncoderRuntimeException
                ' Expected
            End Try
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.InternalState, "must be Faulted")
            Dim goodFrame = TestHelpers.CreateFrame(0, 0)
            Dim packet2 As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() enc.Encode(goodFrame, packet2),
                "Encode after Faulted must throw InvalidOperationException")
            badFrame.Dispose()
            goodFrame.Dispose()
            enc.Dispose()
        End Sub
    End Class
End Namespace
