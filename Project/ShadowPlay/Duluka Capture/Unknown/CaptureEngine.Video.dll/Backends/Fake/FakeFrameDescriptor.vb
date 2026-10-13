Option Strict On
Option Explicit On
Option Infer On

Imports System

Namespace CaptureEngine.Video.Backends.Fake
    ''' <summary>
    ''' Describes one step of a FakeVideoCaptureBackend script.
    ''' Tests build a list of these and pass it to WithScript().
    ''' </summary>
    Public NotInheritable Class FakeFrameDescriptor

        Public ReadOnly Property Kind As FakeFrameDescriptorKind
        Public ReadOnly Property [Error] As Exception

        Private Sub New(kind As FakeFrameDescriptorKind, [error] As Exception)
            Me.Kind = kind
            Me.Error = [error]
        End Sub

        Public Shared Function FrameAvailable() As FakeFrameDescriptor
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.FrameAvailable, Nothing)
        End Function

        Public Shared Function NoFrame() As FakeFrameDescriptor
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.NoFrame, Nothing)
        End Function

        Public Shared Function FromError([error] As Exception) As FakeFrameDescriptor
            If [error] Is Nothing Then Throw New ArgumentNullException(NameOf([error]))
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.Error, [error])
        End Function

        Public Shared Function FromError(message As String) As FakeFrameDescriptor
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.Error, New InvalidOperationException(message))
        End Function

        Public Shared Function ThrowRuntime([error] As Exception) As FakeFrameDescriptor
            If [error] Is Nothing Then Throw New ArgumentNullException(NameOf([error]))
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.ThrowRuntime, [error])
        End Function

        Public Shared Function ThrowRuntime(message As String) As FakeFrameDescriptor
            Return New FakeFrameDescriptor(FakeFrameDescriptorKind.ThrowRuntime, New InvalidOperationException(message))
        End Function
    End Class

    Public Enum FakeFrameDescriptorKind
        FrameAvailable
        NoFrame
        [Error]
        ThrowRuntime
    End Enum
End Namespace
