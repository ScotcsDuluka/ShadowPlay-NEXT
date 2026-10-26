Option Strict On
Namespace DulukaEngine.Video
    ' Borrowed CPU-memory NV12 input; no device or capture ownership is implied.
    Public Interface INv12VideoFrame
        Inherits IVideoFrame
        ReadOnly Property Data As ReadOnlyMemory(Of Byte)
        ReadOnly Property Stride As Integer
        ReadOnly Property IsDisposed As Boolean
    End Interface
End Namespace
