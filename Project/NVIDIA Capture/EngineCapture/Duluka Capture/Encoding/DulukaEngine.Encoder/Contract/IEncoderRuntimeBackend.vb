Option Strict On
Imports DulukaEngine.Video
Namespace DulukaEngine.Encoder
    Public Interface IEncoderRuntimeBackend
        Inherits IEncoderBackend
        ReadOnly Property ActualAdapter As AdapterIdentity
        ReadOnly Property FrameRateFps As Integer
        ReadOnly Property BitrateBpsOutput As Long
        ReadOnly Property EncodeWidthOutput As Integer
        ReadOnly Property EncodeHeightOutput As Integer
    End Interface
End Namespace
