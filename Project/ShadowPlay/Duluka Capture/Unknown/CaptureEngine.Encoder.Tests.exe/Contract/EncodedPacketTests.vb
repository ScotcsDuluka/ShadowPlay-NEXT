Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Encoder.Tests.Contract
    ''' <summary>
    ''' EncodedPacket tests: construction, Dispose, ownership, immutability.
    ''' </summary>
    Friend NotInheritable Class EncodedPacketTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("PACKET: construct valid packet", AddressOf Test_ConstructValid)
            runner("PACKET: Nothing payload → ArgumentNullException", AddressOf Test_NothingPayload)
            runner("PACKET: negative payloadLength → ArgumentOutOfRangeException", AddressOf Test_NegativeLength)
            runner("PACKET: payloadLength > payload.Length → ArgumentOutOfRangeException", AddressOf Test_LengthTooBig)
            runner("PACKET: Dispose → IsDisposed=True", AddressOf Test_DisposeSetsFlag)
            runner("PACKET: double-Dispose → DisposeCount=2, no exception", AddressOf Test_DoubleDispose)
            runner("PACKET: concurrent Dispose → no crash", AddressOf Test_ConcurrentDispose)
            runner("PACKET: metadata immutable", AddressOf Test_MetadataImmutable)
            runner("PACKET: PacketMetadata null codecKey → empty string", AddressOf Test_NullCodecKey)
        End Sub

        Private Shared Sub Test_ConstructValid()
            Dim metadata As New PacketMetadata(0, 0, 0, 100, True, True, "NVENC_H264", 0)
            Dim payload As Byte() = New Byte(63) {}
            Dim pkt As New EncodedPacket(metadata, payload, 64)
            TestHelpers.AssertEqual(0L, pkt.Metadata.Sequence, "Sequence")
            TestHelpers.AssertEqual(64, pkt.PayloadLength, "PayloadLength")
            TestHelpers.AssertEqual(False, pkt.IsDisposed, "IsDisposed")
        End Sub

        Private Shared Sub Test_NothingPayload()
            Dim metadata As New PacketMetadata(0, 0, 0, 0, True, True, "NVENC_H264", 0)
            TestHelpers.AssertThrows(Of ArgumentNullException)(
                Sub()
                    Dim p As EncodedPacket
                    p = New EncodedPacket(metadata, Nothing, 0)
                End Sub,
                "Nothing payload must throw ArgumentNullException")
        End Sub

        Private Shared Sub Test_NegativeLength()
            Dim metadata As New PacketMetadata(0, 0, 0, 0, True, True, "NVENC_H264", 0)
            Dim payload As Byte() = New Byte(15) {}
            TestHelpers.AssertThrows(Of ArgumentOutOfRangeException)(
                Sub()
                    Dim p As EncodedPacket
                    p = New EncodedPacket(metadata, payload, -1)
                End Sub,
                "negative payloadLength must throw ArgumentOutOfRangeException")
        End Sub

        Private Shared Sub Test_LengthTooBig()
            Dim metadata As New PacketMetadata(0, 0, 0, 0, True, True, "NVENC_H264", 0)
            Dim payload As Byte() = New Byte(15) {}
            TestHelpers.AssertThrows(Of ArgumentOutOfRangeException)(
                Sub()
                    Dim p As EncodedPacket
                    p = New EncodedPacket(metadata, payload, 20)
                End Sub,
                "payloadLength > payload.Length must throw ArgumentOutOfRangeException")
        End Sub

        Private Shared Sub Test_DisposeSetsFlag()
            Dim pkt = CreateTestPacket()
            TestHelpers.AssertEqual(False, pkt.IsDisposed, "before Dispose")
            pkt.Dispose()
            TestHelpers.AssertEqual(True, pkt.IsDisposed, "after Dispose")
            TestHelpers.AssertEqual(1, pkt.DisposeCount, "DisposeCount must be 1")
        End Sub

        Private Shared Sub Test_DoubleDispose()
            Dim pkt = CreateTestPacket()
            pkt.Dispose()
            TestHelpers.AssertEqual(1, pkt.DisposeCount, "after first Dispose")
            pkt.Dispose() ' must NOT throw
            TestHelpers.AssertEqual(2, pkt.DisposeCount, "after second Dispose")
        End Sub

        Private Shared Sub Test_ConcurrentDispose()
            Dim pkt = CreateTestPacket()
            Dim threads(3) As Thread
            For i As Integer = 0 To 3
                threads(i) = New Thread(Sub() pkt.Dispose())
                threads(i).Start()
            Next
            For i As Integer = 0 To 3
                threads(i).Join()
            Next
            TestHelpers.Assert(pkt.DisposeCount >= 1, "DisposeCount must be >= 1 after concurrent Dispose")
            TestHelpers.Assert(pkt.IsDisposed, "packet must be disposed")
        End Sub

        Private Shared Sub Test_MetadataImmutable()
            Dim metadata As New PacketMetadata(42, 1000, 1000, 166667, False, True, "NVENC_HEVC", &H1)
            TestHelpers.AssertEqual(42L, metadata.Sequence, "Sequence")
            TestHelpers.AssertEqual(1000L, metadata.PresentationTimestampTicks, "PTS")
            TestHelpers.AssertEqual(1000L, metadata.DecodingTimestampTicks, "DTS")
            TestHelpers.AssertEqual(166667L, metadata.DurationTicks, "Duration")
            TestHelpers.AssertEqual(False, metadata.IsKeyFrame, "IsKeyFrame")
            TestHelpers.AssertEqual(True, metadata.IsReferenceFrame, "IsReferenceFrame")
            TestHelpers.AssertEqual("NVENC_HEVC", metadata.CodecKey, "CodecKey")
            TestHelpers.AssertEqual(&H1, metadata.CodecSpecificFlags, "CodecSpecificFlags")
        End Sub

        Private Shared Sub Test_NullCodecKey()
            Dim metadata As New PacketMetadata(0, 0, 0, 0, True, True, Nothing, 0)
            TestHelpers.AssertEqual(String.Empty, metadata.CodecKey,
                "null codecKey must become empty string")
        End Sub

        ' Helper used by this test class only (instance method, so static fields work)
        Private Shared Function CreateTestPacket(Optional seq As Long = 0, Optional pts As Long = 0) As EncodedPacket
            Dim metadata As New PacketMetadata(
                sequence:=seq,
                presentationTimeTicks:=pts,
                decodingTimeTicks:=pts,
                durationTicks:=166667L,
                isKeyFrame:=True,
                isReferenceFrame:=True,
                codecKey:="NVENC_H264",
                codecSpecificFlags:=0)
            Dim payload(31) As Byte
            For i As Integer = 0 To 31
                payload(i) = CByte(i)
            Next
            Return New EncodedPacket(metadata, payload, 32)
        End Function
    End Class
End Namespace
