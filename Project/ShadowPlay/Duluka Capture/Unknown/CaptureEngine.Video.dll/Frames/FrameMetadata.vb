Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Video.Frames
    ''' <summary>
    ''' Metadata for a single video frame.
    '''
    ''' Designed to be extensible — new fields can be added in future phases
    ''' without breaking existing consumers (they just ignore unknown fields).
    '''
    ''' All timestamps are in 100-nanosecond QPC-derived ticks (P1-B.2 §16.3
    ''' Option β). Both are 0 if the producer does not provide them.
    ''' </summary>
    Public Structure FrameMetadata
        ''' <summary>
        ''' When the frame was captured (QPC-derived, 100-ns ticks).
        ''' Set by the capture backend at acquisition time.
        ''' </summary>
        Public ReadOnly Property CaptureTimestamp As Long

        ''' <summary>
        ''' When the frame should be presented (QPC-derived, 100-ns ticks).
        ''' May equal CaptureTimestamp (no PTS concept) or be offset
        ''' (e.g. for frame-rate conversion).
        ''' </summary>
        Public ReadOnly Property PresentationTimestamp As Long

        ''' <summary>
        ''' Source identifier: "ddagrab", "gdigrab", "gfxcapture", "dxgi",
        ''' "wgc", "test", etc. Free-form string — consumers use it for
        ''' diagnostics only, not for dispatching.
        ''' </summary>
        Public ReadOnly Property Source As String

        ''' <summary>
        ''' Bitwise flags for frame attributes (keyframe, corrupt, etc.).
        ''' Currently no flags are defined — reserved for future use.
        ''' </summary>
        Public ReadOnly Property Flags As Integer

        Public Sub New(captureTimestamp As Long,
                       presentationTimestamp As Long,
                       source As String,
                       flags As Integer)
            Me.CaptureTimestamp = captureTimestamp
            Me.PresentationTimestamp = presentationTimestamp
            Me.Source = If(source, "")
            Me.Flags = flags
        End Sub

        ''' <summary>Convenience factory with PTS = capture timestamp.</summary>
        Public Shared Function Create(captureTimestamp As Long,
                                      source As String,
                                      Optional flags As Integer = 0) As FrameMetadata
            Return New FrameMetadata(captureTimestamp, captureTimestamp, source, flags)
        End Function
    End Structure
End Namespace
