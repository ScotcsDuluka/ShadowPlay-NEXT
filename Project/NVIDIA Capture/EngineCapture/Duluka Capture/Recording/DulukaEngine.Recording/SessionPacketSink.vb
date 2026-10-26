Option Strict On
Imports DulukaEngine.Encoder
Imports DulukaEngine.Video

Namespace DulukaEngine.Recording
    ' One session-owned packet path for immediate output AND delayed/Flush output.
    Public NotInheritable Class SessionPacketSink
        Private ReadOnly _encoder As IEncoderBackend
        Private ReadOnly _result As SessionResult
        Private ReadOnly _feed As Action(Of Byte(), Integer)
        Private ReadOnly _strictCfr As Integer
        Private _lastSequence As Long = -1
        Private _lastPts As Long = -1
        Public Sub New(encoder As IEncoderBackend, result As SessionResult,
                       feed As Action(Of Byte(), Integer), Optional strictCfr As Integer = 0)
            _encoder = encoder : _result = result : _feed = feed : _strictCfr = strictCfr
        End Sub
        Public Function Submit(frame As IVideoFrame) As Boolean
            Try
                Dim accepted As Boolean
                Dim packet As EncodedPacket = Nothing
                If TypeOf _encoder Is IEncoderSubmissionBackend Then
                    Dim submission = DirectCast(_encoder, IEncoderSubmissionBackend).Submit(frame)
                    accepted = submission.Accepted : packet = submission.Packet
                Else
                    Dim before = _encoder.Diagnostics.SubmittedFrames
                    Dim produced = _encoder.Encode(frame, packet)
                    accepted = produced OrElse _encoder.Diagnostics.SubmittedFrames > before
                End If
                If accepted Then _result.FramesSubmitted += 1
                If packet IsNot Nothing Then Consume(packet, False)
                Return accepted
            Catch
                _result.EncoderErrors += 1
                Throw
            End Try
        End Function
        Private Sub Consume(packet As EncodedPacket, flushed As Boolean)
            Using packet
                If packet.IsDisposed OrElse packet.PayloadLength <= 0 Then Throw New InvalidOperationException("session_invalid_encoded_packet")
                Dim m = packet.Metadata
                If _strictCfr > 0 Then
                    Dim expectedPts = CLng(Math.Floor(CDec(_result.EncoderPacketCount) * 10000000D / _strictCfr))
                    If m.Sequence <= _lastSequence OrElse m.PresentationTimestampTicks <= _lastPts OrElse
                        m.PresentationTimestampTicks <> expectedPts OrElse m.DecodingTimestampTicks <> m.PresentationTimestampTicks Then
                        Throw New InvalidOperationException("session_packet_order_or_pts_dts_invalid")
                    End If
                End If
                _lastSequence = m.Sequence : _lastPts = m.PresentationTimestampTicks
                _result.EncoderPacketCount += 1
                _feed(packet.Payload, packet.PayloadLength) ' existing mux copies payload before return
                _result.PacketsQueuedForMux += 1
                _result.FramesEncoded += 1 ' compatibility alias: completed video access units
                _result.TotalVideoBytes += packet.PayloadLength
                If flushed Then _result.FlushedPackets += 1
            End Using
        End Sub
        Public Sub Drain()
            _result.FinalizationState = "Draining"
            Try
                _encoder.Flush(Sub(packet) Consume(packet, True))
            Catch
                _result.EncoderErrors += 1
                Throw
            End Try
        End Sub
    End Class
End Namespace
