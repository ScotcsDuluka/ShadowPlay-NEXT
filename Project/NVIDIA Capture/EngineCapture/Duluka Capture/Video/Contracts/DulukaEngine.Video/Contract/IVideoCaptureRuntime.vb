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

    ' Optional additive health evidence. Empty means that no terminal source
    ' fault has been observed; a transient acquire timeout is not a fault.
    ' The value remains latched across Stop so a session cannot lose it while
    ' finalizing. Backends that do not provide this surface remain unchanged.
    Public Interface IVideoCaptureTerminalHealth
        ReadOnly Property TerminalFault As String
    End Interface
End Namespace
