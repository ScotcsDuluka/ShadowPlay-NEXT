Option Strict On
Imports DulukaEngine.Video
Namespace DulukaEngine.Encoder
    ' Additive contract: Accepted=True/Packet=Nothing means delayed, never retry.
    Public Interface IEncoderSubmissionBackend
        Function Submit(frame As IVideoFrame) As EncoderSubmission
    End Interface
    Public NotInheritable Class EncoderSubmission
        Public ReadOnly Property Accepted As Boolean
        Public ReadOnly Property Packet As EncodedPacket
        Public Sub New(accepted As Boolean, packet As EncodedPacket)
            Me.Accepted = accepted : Me.Packet = packet
        End Sub
    End Class
End Namespace
