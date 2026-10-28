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
        Private ReadOnly _progress As Action(Of Long, Long, Long)
        Private _drained As Boolean
        Private _faulted As Boolean
        Private _lastSequence As Long = -1
        Private _lastPts As Long = -1
        Public Sub New(encoder As IEncoderBackend, result As SessionResult,
                       feed As Action(Of Byte(), Integer), Optional strictCfr As Integer = 0,
                       Optional progress As Action(Of Long, Long, Long) = Nothing)
            If encoder Is Nothing Then Throw New ArgumentNullException(NameOf(encoder))
            If result Is Nothing Then Throw New ArgumentNullException(NameOf(result))
            If feed Is Nothing Then Throw New ArgumentNullException(NameOf(feed))
            If strictCfr < 0 Then Throw New ArgumentOutOfRangeException(NameOf(strictCfr))
            _encoder = encoder : _result = result : _feed = feed : _strictCfr = strictCfr : _progress = progress
        End Sub
        Public Function Submit(frame As IVideoFrame) As Boolean
            If _drained OrElse _faulted Then Throw New InvalidOperationException("session_packet_sink_terminal")
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
                PublishProgress()
                Return accepted
            Catch
                _faulted = True
                _result.EncoderErrors += 1
                Throw
            End Try
        End Function
        Private Sub Consume(packet As EncodedPacket, flushed As Boolean)
            If packet Is Nothing Then Throw New InvalidOperationException("session_invalid_encoded_packet")
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
                PublishProgress()
            End Using
        End Sub
        Private Sub PublishProgress()
            _progress?.Invoke(_result.FramesSubmitted, _result.EncoderPacketCount, _result.PacketsQueuedForMux)
        End Sub
        Public Sub Drain()
            If _drained Then Return
            If _faulted Then Throw New InvalidOperationException("session_packet_sink_faulted")
            _result.FinalizationState = "Draining"
            Try
                _encoder.Flush(Sub(packet) Consume(packet, True))
                If _strictCfr > 0 AndAlso _result.EncoderPacketCount <> _result.FramesSubmitted Then
                    Throw New InvalidOperationException("session_encoder_drain_frame_count_mismatch")
                End If
                _drained = True
            Catch
                _faulted = True
                _result.EncoderErrors += 1
                Throw
            End Try
        End Sub
    End Class
End Namespace
