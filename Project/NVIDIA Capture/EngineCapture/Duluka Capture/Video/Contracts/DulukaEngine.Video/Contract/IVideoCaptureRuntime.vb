Option Strict On
Namespace DulukaEngine.Video
    ' Supplemental runtime information; the original backend/frame ABI stays intact.
    Public Interface IVideoCaptureRuntime
        Inherits IVideoCaptureBackend
        ReadOnly Property ActualTarget As CaptureTarget
        ReadOnly Property OutputWidth As Integer
        ReadOnly Property OutputHeight As Integer
        ReadOnly Property OutputRefreshRate As Integer
        ReadOnly Property AccessLostCount As Long
        ReadOnly Property TexturesCreated As Long
        ReadOnly Property TexturesDisposed As Long
        ReadOnly Property FramesPushed As Long
    End Interface
End Namespace
