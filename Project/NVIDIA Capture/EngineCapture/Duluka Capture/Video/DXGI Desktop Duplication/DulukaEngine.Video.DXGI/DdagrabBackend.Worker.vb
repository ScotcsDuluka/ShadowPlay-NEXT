Option Strict On
Option Explicit On

Imports System.Diagnostics
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports SharpGen.Runtime
Imports Vortice.Direct3D11
Imports Vortice.DXGI
Imports ResultCode = Vortice.DXGI.ResultCode

Namespace DulukaEngine.Video.Backends.Ddagrab
    Partial Public NotInheritable Class DdagrabBackend
        Private _lastSourceQpc100ns As Long = -1
        Private _recoveryStarted As Long

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(handle As IntPtr) As Boolean
        End Function

        Private Function StopRequested() As Boolean
            SyncLock _sync
                Return _stopSignal
            End SyncLock
        End Function

        Private Sub WorkerLoop()
            Dim run As Long
            SyncLock _sync
                run = _runId
            End SyncLock
            Dim deviceRemovedRebuilds As Integer = 0
            Do While Not StopRequested()
                SyncLock _sync
                    If _workerCrashRequested Then
                        _workerCrashRequested = False
                        Throw New InvalidOperationException("INJECTED worker crash")
                    End If
                End SyncLock
                If _duplication Is Nothing Then
                    RecreateDuplication()
                    If _duplication Is Nothing Then
                        Thread.Sleep(250)
                        Continue Do
                    End If
                End If
                If StopRequested() Then Exit Do
                Dim sequence = Interlocked.Increment(_nextSequence) - 1
                Dim attemptTime = QpcTicksTo100ns(Stopwatch.GetTimestamp())
                Dim generation = SourceGeneration
                Dim duplication = _duplication
                Dim info As OutduplFrameInfo
                Dim resource As IDXGIResource = Nothing
                Dim result As Result
                Dim acquired As Boolean
                Try
                    acquired = FrameTransaction.Execute(
                        Function()
                            result = duplication.AcquireNextFrame(100, info, resource)
                            Return result.Success
                        End Function,
                        Function() BuildOwnedFrame(resource, info, sequence, generation),
                        Sub()
                            Try
                                Dim borrowed = resource
                                resource = Nothing
                                borrowed?.Dispose()
                            Finally
                                duplication.ReleaseFrame().CheckError()
                            End Try
                        End Sub,
                        Function(frame) PushOwnedFrame(frame, sequence, attemptTime, run, generation))
                Catch ex As SharpGenException When ex.ResultCode.Code = ResultCode.DeviceRemoved.Code
                    ' DXGI_ERROR_DEVICE_REMOVED killed the device mid-frame: a
                    ' duplication rebuild on a dead device cannot succeed — the
                    ' D3D11 device itself must be recreated (ADR §D recovery).
                    ' The encoder owns its own device and re-opens per-frame
                    ' shared handles, so a capture-side rebuild restores the
                    ' pipeline without touching the encoder session.
                    deviceRemovedRebuilds += 1
                    If deviceRemovedRebuilds > MaxDeviceRemovedRebuilds Then Throw
                    _logger.Warning($"DdagrabBackend: DeviceRemoved — full device+duplication rebuild ({deviceRemovedRebuilds}/{MaxDeviceRemovedRebuilds}): {ex.Message}")
                    RecreateDeviceAndDuplication()
                    Continue Do
                Catch ex As SourceFailureException When ex.Message.Contains("887A0005") OrElse
                                                        ex.Message.Contains("source_recreation_failed")
                    ' Covers both the raw HRESULT path and the duplication
                    ' recreation budget throw right after a device reset —
                    ' both mean the same thing: rebuild and keep the session.
                    deviceRemovedRebuilds += 1
                    If deviceRemovedRebuilds > MaxDeviceRemovedRebuilds Then Throw
                    _logger.Warning($"DdagrabBackend: device-level rebuild ({deviceRemovedRebuilds}/{MaxDeviceRemovedRebuilds}): {ex.Message}")
                    RecreateDeviceAndDuplication()
                    Continue Do
                Finally
                    ' A failed acquisition may still have returned a COM wrapper.
                    resource?.Dispose()
                End Try
                If acquired Then
                    deviceRemovedRebuilds = 0
                    Continue Do
                End If
                If result = ResultCode.WaitTimeout Then
                    Interlocked.Increment(_noFrameCount)
                ElseIf result = ResultCode.AccessLost Then
                    Interlocked.Increment(_accessLostCount)
                    NotifySourceFault(SourceFaultKind.AccessLost, run, generation)
                    RecreateDuplication()
                ElseIf result = ResultCode.DeviceRemoved Then
                    deviceRemovedRebuilds += 1
                    If deviceRemovedRebuilds > MaxDeviceRemovedRebuilds Then
                        Throw New SourceFailureException(SourceFaultKind.DeviceRemoved,
                            $"acquire_failed:0x{result.Code:x8} rebuild_budget_exhausted")
                    End If
                    _logger.Warning($"DdagrabBackend: DeviceRemoved at acquire — full device+duplication rebuild ({deviceRemovedRebuilds}/{MaxDeviceRemovedRebuilds})")
                    RecreateDeviceAndDuplication()
                Else
                    Throw New SourceFailureException(ClassifyFailure(result.Code), $"acquire_failed:0x{result.Code:x8}")
                End If
                If _interAttemptDelayMs > 0 Then Thread.Sleep(_interAttemptDelayMs)
            Loop
        End Sub

        Private Const MaxDeviceRemovedRebuilds As Integer = 5

        ''' <summary>
        ''' DeviceRemoved recovery: the D3D11 device is dead, so dispose the
        ''' duplication, per-device scratch and the device itself, then re-run
        ''' the full resource initialization (re-resolves the pinned target
        ''' against the LIVE inventory — never silently retargets) and the
        ''' duplication. Bounded by the caller's rebuild budget.
        ''' </summary>
        Private Sub RecreateDeviceAndDuplication()
            DisposeDeviceResources()
            Dim attempts As Integer = 0
            Do
                attempts += 1
                Try
                    InitializeResources(_context)
                    RecreateDuplication()
                    Exit Do
                Catch ex As Exception When attempts < 3
                    ' Transient driver state right after a device loss settles
                    ' within a beat — bounded retry before giving up.
                    _logger.Warning($"DdagrabBackend: device rebuild attempt {attempts} failed: {ex.Message}")
                    Thread.Sleep(800)
                    DisposeDeviceResources()
                End Try
            Loop
            CompleteRecreation()
            _logger.Info("DdagrabBackend: device+duplication rebuilt after DeviceRemoved — source generation " & SourceGeneration)
        End Sub

        Private Sub DisposeDeviceResources()
            SyncLock _sync
                Try : _duplication?.Dispose() : Catch : End Try
                _duplication = Nothing
                Try : _cursorStage?.Dispose() : Catch : End Try
                _cursorStage = Nothing
                Try : _output?.Dispose() : Catch : End Try
                _output = Nothing
                Try : _dxgiFactory?.Dispose() : Catch : End Try
                _dxgiFactory = Nothing
                Try : _deviceContext?.Dispose() : Catch : End Try
                _deviceContext = Nothing
                Try : _device?.Dispose() : Catch : End Try
                _device = Nothing
            End SyncLock
        End Sub

        Private Function BuildOwnedFrame(resource As IDXGIResource, info As OutduplFrameInfo,
                                         sequence As Long, generation As Long) As IVideoFrame
            ' DXGI reports LastPresentTime=0 when NOTHING has presented since the
            ' duplication was created — the frame is still the CURRENT desktop
            ' image and is the only frame a static desktop will ever produce.
            ' Rejecting it made every session on an idle desktop die at frame 0.
            ' Stamp the acquisition QPC instead (counted as a timestamp fallback,
            ' never described as a real present-timestamp).
            Dim qpc = info.LastPresentTime
            Dim timestamp As Long
            If qpc <= 0 Then
                timestamp = QpcTicksTo100ns(Stopwatch.GetTimestamp())
                Dim first = Interlocked.Increment(_timestampFallbackCount) = 1
                If first Then _logger.Info($"DdagrabBackend: LastPresentTime=0 on frame {sequence} — acquisition-QPC fallback stamp {timestamp} (static desktop)")
            Else
                timestamp = ValidateSourceTimestamp(qpc)
            End If
            If sequence < 3 OrElse sequence Mod 60 = 0 Then _logger.Info($"DdagrabBackend: source QPC={info.LastPresentTime} sequence={sequence} ts100={timestamp}")
            Using desktop = resource.QueryInterface(Of ID3D11Texture2D)()
                Return OwnedFrameBuilder.Build(AddressOf CreateFrameTexture,
                    Sub(texture)
                        _deviceContext.CopyResource(texture, desktop)
                        BlendCursorOnto(texture, info)
                    End Sub,
                    Sub(destination, source) _deviceContext.CopyResource(destination, source), _useSharedHandle,
                    Function(texture)
                        Using dxgi = texture.QueryInterface(Of IDXGIResource1)()
                            Return dxgi.CreateSharedHandle(Nothing, Vortice.DXGI.SharedResourceFlags.Read Or Vortice.DXGI.SharedResourceFlags.Write, Nothing)
                        End Using
                    End Function,
                    Sub(value) CloseHandle(value),
                    Function(texture, handle)
                        Dim frame = New D3D11VideoFrame(texture, _outputWidth, _outputHeight, sequence,
                            timestamp, timestamp, handle, _actualTarget, generation)
                        frame.OnDisposed = AddressOf TextureReleased
                        Return frame
                    End Function, AddressOf TextureReleased)
            End Using
        End Function

        Private Function CreateFrameTexture(useShared As Boolean) As ID3D11Texture2D
            Dim texture = _device.CreateTexture2D(If(useShared, _sharedDesc, _stagingDesc))
            Interlocked.Increment(_texturesCreated)
            Return texture
        End Function

        Private Sub TextureReleased()
            Interlocked.Increment(_texturesDisposed)
        End Sub

        Friend Function ValidateSourceTimestamp(qpc As Long) As Long
            Dim timestamp = QpcTicksTo100ns(qpc)
            If qpc <= 0 OrElse qpc <= _lastSourceQpcTicks OrElse timestamp <= _lastSourceQpc100ns Then
                ' Values in the message: first-frame rejections (LastPresentTime=0
                ' on a static desktop, cross-boot QPC reuse, conversion drift) are
                ' undiagnosable without the actual numbers.
                Throw New SourceFailureException(SourceFaultKind.InvalidFrame,
                    $"source_nonmonotonic_or_missing_qpc qpc={qpc} lastQpc={_lastSourceQpcTicks} ts100={timestamp} lastTs100={_lastSourceQpc100ns}")
            End If
            _lastSourceQpcTicks = qpc
            _lastSourceQpc100ns = timestamp
            Return timestamp ' Session alone selects the epoch and encoder PTS.
        End Function

        Friend Function PushOwnedFrame(frame As IVideoFrame, sequence As Long, attemptTime As Long,
                                       run As Long, generation As Long) As PushOutcome
            Dim sink As IVideoFrameSink
            SyncLock _sync
                If Not _callbacksEnabled OrElse _disposed OrElse _stopSignal OrElse
                    run <> _runId OrElse generation <> _sourceGeneration Then Return PushOutcome.Dropped
                sink = _sink
            End SyncLock
            Dim outcome = sink.TryPush(FrameAcquisitionResult.Available(frame, sequence, attemptTime))
            Select Case outcome
                Case PushOutcome.Pushed, PushOutcome.Replaced
                    Interlocked.Increment(_emittedFrames)
                    Interlocked.Increment(_framesPushed)
                    If outcome = PushOutcome.Replaced Then Interlocked.Increment(_replacedFrames)
                Case PushOutcome.Dropped
                    Interlocked.Increment(_droppedFrames)
                    Interlocked.Increment(_framesDropped)
            End Select
            Return outcome
        End Function

        Friend Shared Function ClassifyFailure(code As Integer) As SourceFaultKind
            If code = ResultCode.AccessLost.Code OrElse code = ResultCode.NotCurrentlyAvailable.Code Then Return SourceFaultKind.AccessLost
            If code = ResultCode.DeviceRemoved.Code OrElse code = ResultCode.DeviceReset.Code OrElse
                code = ResultCode.DeviceHung.Code Then Return SourceFaultKind.DeviceRemoved
            If code = ResultCode.SessionDisconnected.Code Then Return SourceFaultKind.Disconnected
            Return SourceFaultKind.InvalidFrame ' Unknown/denied/OOM is not proof of disconnect.
        End Function

        Private Sub ValidateRecoveryTarget()
            Dim current = DxgiDisplayInventory.Collect().Outputs
            If Not current.Any(Function(t) t.Attached AndAlso t.Adapter.SameDevice(_actualTarget.Adapter) AndAlso
                String.Equals(t.MonitorDevicePath, _actualTarget.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)) Then
                Throw New SourceFailureException(SourceFaultKind.Disconnected, "capture_target_disconnected")
            End If
            Try
                CaptureTarget.RequireCurrent(_actualTarget, current)
            Catch ex As Exception
                Throw New SourceFailureException(SourceFaultKind.OutputChanged, "capture_target_changed", ex)
            End Try
        End Sub

        Private Sub RecreateDuplication()
            If StopRequested() Then Return
            Try
                DuplicationReplacement.Replace(_duplication,
                    Function()
                        ValidateRecoveryTarget()
                        Return _output.QueryInterface(Of IDXGIOutput1)()
                    End Function,
                    Function(output) output.DuplicateOutput(_device))
                CompleteRecreation()
            Catch ex As SourceFailureException
                Throw
            Catch ex As Exception
                Dim native = TryCast(ex, SharpGenException)
                Dim kind = If(native Is Nothing, SourceFaultKind.InvalidFrame, ClassifyFailure(native.ResultCode.Code))
                RecordRecreationFailure(kind, ex)
            End Try
        End Sub

        Friend Sub CompleteRecreation()
            SyncLock _sync
                _duplicationRecreateFailures = 0
                _recoveryStarted = 0
                _sourceGeneration = If(_reservedGeneration > 0, _reservedGeneration, SourceGenerationIds.Allocate())
                _reservedGeneration = 0
            End SyncLock
        End Sub

        Friend Sub RecordRecreationFailure(kind As SourceFaultKind, failure As Exception)
            Dim attempts As Long
            Dim expired As Boolean
            SyncLock _sync
                If _recoveryStarted = 0 Then _recoveryStarted = Stopwatch.GetTimestamp()
                _duplicationRecreateFailures += 1
                attempts = _duplicationRecreateFailures
                expired = Stopwatch.GetElapsedTime(_recoveryStarted).TotalSeconds >= 30
            End SyncLock
            If kind <> SourceFaultKind.AccessLost OrElse attempts >= MaxDuplicationRecreateFailures OrElse expired Then
                Throw New SourceFailureException(kind, "source_recreation_failed_or_budget_exhausted", failure)
            End If
            NotifySourceFault(kind, _runId, SourceGeneration)
        End Sub
    End Class
End Namespace
