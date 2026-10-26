Option Strict On
Imports DulukaEngine.Video
Namespace DulukaEngine.Recording
    ' Owned source envelope: acquisition never owns an encoder or recording file.
    Public NotInheritable Class SessionFrameInput
        Implements IDisposable
        Public ReadOnly Property Frame As IVideoFrame
        Public ReadOnly Property Target As CaptureTarget
        Public ReadOnly Property Generation As Long
        Public ReadOnly Property PreviousGeneration As Long
        Private _disposed As Boolean
        Public Sub New(frame As IVideoFrame, target As CaptureTarget, generation As Long)
            Me.New(frame, target, generation, 0)
        End Sub
        Public Sub New(frame As IVideoFrame, target As CaptureTarget, generation As Long, previousGeneration As Long)
            Me.Frame = frame : Me.Target = target : Me.Generation = generation
            Me.PreviousGeneration = previousGeneration
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True : Frame.Dispose()
            End If
        End Sub
    End Class
    Public NotInheritable Class SessionFrameRouter
        Private ReadOnly _target As CaptureTarget
        Private ReadOnly _fps As Integer
        Private _generation As Long
        Private _frames As Long
        Private _lastSequence As Long = -1
        Private _qpcOrigin As Long = -1
        Private _lastQpc As Long = -1
        Private ReadOnly _width As Integer
        Private ReadOnly _height As Integer
        Public Sub New(target As CaptureTarget, fps As Integer, generation As Long,
                       Optional surfaceWidth As Integer = 0, Optional surfaceHeight As Integer = 0)
            If target Is Nothing OrElse Not target.Eligible OrElse fps <= 0 OrElse generation <= 0 Then
                Throw New ArgumentException("session_invalid_frame_plan")
            End If
            _target = target : _fps = fps : _generation = generation
            _width = If(surfaceWidth > 0, surfaceWidth, target.Width)
            _height = If(surfaceHeight > 0, surfaceHeight, target.Height)
        End Sub
        Public Function Route(input As SessionFrameInput) As IVideoFrame
            If input Is Nothing OrElse input.Frame Is Nothing OrElse input.Target Is Nothing OrElse
                Not _target.SameOutput(input.Target) Then Throw New InvalidOperationException("session_source_target_mismatch")
            If input.Generation <> _generation Then Throw New InvalidOperationException("session_stale_source_generation")
            Dim f = input.Frame
            If f.Dimensions.Width <> _width OrElse f.Dimensions.Height <> _height Then
                Throw New InvalidOperationException("session_source_surface_size_mismatch")
            End If
            Dim expected = CLng(Math.Floor(CDec(_frames) * 10000000D / _fps))
            If f.Diagnostics.PresentationTimestampTicks <> expected Then Throw New InvalidOperationException("session_source_pts_discontinuity")
            If f.Diagnostics.Sequence <= _lastSequence Then Throw New InvalidOperationException("session_source_sequence_not_monotonic")
            _lastSequence = f.Diagnostics.Sequence
            _frames += 1
            Return f ' borrowed; enclosing input remains sole owner until Submit returns
        End Function
        Public Sub AuthorizeGeneration(previous As Long, nextGeneration As Long, current As CaptureTarget)
            If previous <> _generation OrElse nextGeneration <= previous OrElse Not _target.SameOutput(current) OrElse
                _target.TopologyId <> current.TopologyId Then Throw New InvalidOperationException("session_generation_transition_not_authorized")
            _generation = nextGeneration
            _lastSequence = -1 ' Source ordinal may reset; session time never does.
        End Sub
        Public Function RouteSource(input As SessionFrameInput) As IVideoFrame
            If input Is Nothing OrElse input.Target Is Nothing OrElse Not _target.SameOutput(input.Target) OrElse
                _target.TopologyId <> input.Target.TopologyId Then Throw New InvalidOperationException("session_source_target_mismatch")
            If input.Generation <> _generation Then Throw New InvalidOperationException("session_stale_source_generation")
            Dim frame = TryCast(input.Frame, INv12VideoFrame)
            If frame Is Nothing OrElse frame.IsDisposed OrElse frame.Dimensions.Width <> _width OrElse frame.Dimensions.Height <> _height Then
                Throw New InvalidOperationException("session_source_surface_size_mismatch")
            End If
            Dim qpc = frame.Diagnostics.CaptureTimeTicks
            Dim seq = frame.Diagnostics.Sequence
            If qpc < 0 OrElse qpc <= _lastQpc OrElse seq <= _lastSequence Then Throw New InvalidOperationException("session_source_clock_or_sequence_reordered")
            If _qpcOrigin < 0 Then _qpcOrigin = qpc
            Dim expected = CLng(Math.Floor(CDec(_frames) * 10000000D / _fps))
            If Math.Abs(CDec(qpc) - _qpcOrigin - expected) > 10000000D / _fps / 2 Then
                Throw New InvalidOperationException("session_source_timestamp_gap_requires_policy")
            End If
            _lastQpc = qpc : _lastSequence = seq : _frames += 1
            Return New SessionNv12View(frame, New FrameDiagnostics(_frames - 1, qpc, expected))
        End Function
        ' Borrowed metadata view: SessionFrameInput remains the single owner.
        Private NotInheritable Class SessionNv12View
            Implements INv12VideoFrame, IColorVideoFrame
            Private ReadOnly _frame As INv12VideoFrame
            Public Sub New(frame As INv12VideoFrame, timestamps As FrameDiagnostics)
                _frame = frame : Diagnostics = timestamps
            End Sub
            Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
            Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
                Get
                    Return _frame.Origin
                End Get
            End Property
            Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
                Get
                    Return _frame.PixelFormat
                End Get
            End Property
            Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
                Get
                    Return _frame.Dimensions
                End Get
            End Property
            Public ReadOnly Property Data As ReadOnlyMemory(Of Byte) Implements INv12VideoFrame.Data
                Get
                    Return _frame.Data
                End Get
            End Property
            Public ReadOnly Property Stride As Integer Implements INv12VideoFrame.Stride
                Get
                    Return _frame.Stride
                End Get
            End Property
            Public ReadOnly Property IsDisposed As Boolean Implements INv12VideoFrame.IsDisposed
                Get
                    Return _frame.IsDisposed
                End Get
            End Property
            Public ReadOnly Property Color As VideoColorDescriptor Implements IColorVideoFrame.Color
                Get
                    Return DirectCast(_frame, IColorVideoFrame).Color
                End Get
            End Property
            Public Sub Dispose() Implements IDisposable.Dispose
                ' A borrowed view does not dispose its owner's payload.
            End Sub
        End Class
    End Class
End Namespace
