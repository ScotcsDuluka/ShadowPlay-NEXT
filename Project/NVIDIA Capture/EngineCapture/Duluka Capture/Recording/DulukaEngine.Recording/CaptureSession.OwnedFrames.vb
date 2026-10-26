Option Strict On
Imports System.Collections.Generic
Imports System.IO
Imports DulukaEngine.Media

Namespace DulukaEngine.Recording
    Partial Public NotInheritable Class CaptureSession
        Private _activeSource As DulukaEngine.Video.Transport.D3D11.BgraSessionSource
        Public ReadOnly Property SourceLifecycleState As String
            Get
                Return _activeSource?.State
            End Get
        End Property
        Public Function RunSourceFrames(source As DulukaEngine.Video.Transport.D3D11.BgraSessionSource) As SessionResult
            _activeSource = source
            Dim result As SessionResult = Nothing
            Try
                If Threading.Volatile.Read(_stopSignal) Then source.Stop()
                result = RunOwnedFrames(SourceInputs(source), source.InitialGeneration)
            Finally
                source.Stop() : _activeSource = Nothing
                If result IsNot Nothing Then
                    result.SourceAccepted = source.Accepted : result.SourceRejected = source.Rejected
                    result.SourceQueueDrops = source.QueueDrops : result.SourceSequenceGaps = source.SequenceGaps
                    result.SourceDuplicateFrames = source.DuplicateFrames : result.SourceRecoveries = source.Recoveries
                End If
            End Try
            Return result
        End Function
        Private Iterator Function SourceInputs(source As DulukaEngine.Video.Transport.D3D11.BgraSessionSource) As IEnumerable(Of SessionFrameInput)
            While True
                Dim item = source.Read()
                If item Is Nothing Then Return
                Yield New SessionFrameInput(item.Frame, item.Target, item.Generation, item.PreviousGeneration)
            End While
        End Function
        ' A generic session input entry for controlled, source-owned frames.
        ' It uses this SAME session, packet sink and LiveMuxSession. The encoder
        ' never gains file ownership. No desktop or audio device is started here.
        Public Function RunOwnedFrames(inputs As IEnumerable(Of SessionFrameInput), generation As Long) As SessionResult
            If inputs Is Nothing Then Throw New ArgumentNullException(NameOf(inputs))
            If _config.RequestsAnyAudio Then Throw New NotSupportedException("owned_frame_input_audio_not_implemented")
            Dim result As New SessionResult With {.SessionId = SessionId, .OutputPath = _config.OutputPath,
                .RequestedDurationSec = _config.DurationSeconds, .MediaVerificationRequired = True, .FinalizationState = "Preparing"}
            Dim expectedFrames As Long = CLng(_config.DurationSeconds) * _config.TargetFps
            Dim running As Boolean = False
            Try
                If expectedFrames < 1 OrElse expectedFrames > 3600 Then Throw New ArgumentException("bounded_owned_frame_budget_required")
                Dim router As New SessionFrameRouter(_capture.ActualTarget, _config.TargetFps, generation,
                                                    _config.EncodeWidth, _config.EncodeHeight)
                _liveMux = New FFmpegBackend.LiveMuxSession(_config.FFmpegPath, _config.OutputPath, _config.TargetFps,
                    0, 0, 0, 0, False, 1.0F, 1.0F, Sub(message) _logger.Info(message), _config.OnProcessStarted, sessionCfr:=True)
                If Not _liveMux.Start() Then Throw New InvalidOperationException("session_mux_start_failed")
                _liveMux.BeginTimelines(0, 0)
                Dim packets As New SessionPacketSink(_encoder, result, Sub(data, count) _liveMux.FeedVideo(data, count), _config.TargetFps)
                running = True : _encoder.Start()
                result.FinalizationState = "AwaitingFirstRealFrame"
                For Each item As SessionFrameInput In inputs
                    Using item
                        If Threading.Volatile.Read(_stopSignal) Then Exit For
                        If result.FramesSubmitted >= expectedFrames Then Throw New InvalidDataException("session_source_exceeded_frame_budget")
                        If item.PreviousGeneration > 0 Then router.AuthorizeGeneration(item.PreviousGeneration, item.Generation, item.Target)
                        Dim frame = If(_activeSource Is Nothing, router.Route(item), router.RouteSource(item))
                        If _config.Color IsNot Nothing AndAlso (Not TypeOf frame Is DulukaEngine.Video.IColorVideoFrame OrElse
                            Not _config.Color.SameColor(DirectCast(frame, DulukaEngine.Video.IColorVideoFrame).Color)) Then
                            Throw New InvalidDataException("session_frame_color_mismatch")
                        End If
                        If Not packets.Submit(frame) Then Throw New InvalidOperationException("session_frame_not_accepted")
                        result.SyntheticFramesConsumed += 1
                        result.FinalizationState = "Recording"
                    End Using ' encoder borrowed the frame and copied any retained data
                Next
                result.FinalizationState = "Stopping"
                packets.Drain()
                _encoder.Stop() : running = False
                result.FinalizationState = "Finalizing"
                Dim mux = _liveMux.Stop(30000)
                result.MuxDroppedBytes = mux.DroppedBytes
                result.MuxPartialSalvaged = mux.PartialSalvaged
                If Not mux.Succeeded OrElse mux.DroppedBytes <> 0 Then Throw New InvalidDataException("session_mux_finalize_failed:" & mux.ErrorMessage)
                result.FileExists = File.Exists(_config.OutputPath)
                result.FileSize = If(result.FileExists, New FileInfo(_config.OutputPath).Length, 0)
                result.ActualDurationSec = CDbl(result.FramesSubmitted) / _config.TargetFps
                result.WrapFps = _config.TargetFps
                If result.FramesSubmitted <> expectedFrames Then Throw New InvalidDataException("session_source_frame_budget_incomplete")
                result.FinalizationState = "Verifying"
                Dim media = SessionMediaVerifier.VerifyWithColor(_config.OutputPath, _config.FFmpegPath, expectedFrames,
                    _config.EncodeWidth, _config.EncodeHeight, _config.TargetFps, _config.OnProcessStarted, _config.Color)
                result.FullDecodeVerified = media.Verified
                result.DecodedFrameCount = media.DecodedFrames
                result.MuxedPacketCount = media.Packets
                result.VideoStreamFound = media.Verified
                If Not media.Verified Then Throw New InvalidDataException(media.Reason)
                result.FinalizationState = If(result.Pass, "Saved", "Failed")
            Catch ex As Exception
                result.ErrorMessage = ex.Message
                result.FinalizationState = If(Threading.Volatile.Read(_stopSignal) OrElse TypeOf ex Is OperationCanceledException, "Cancelled", "Failed")
                _logger.Error("[session] owned frames failed: " & ex.Message)
            Finally
                If running Then
                    Try : _encoder.Stop() : Catch : End Try
                End If
                Try : _liveMux?.Dispose() : Catch : End Try
            End Try
            Return result
        End Function
    End Class
End Namespace
