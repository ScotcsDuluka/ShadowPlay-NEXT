Option Strict On
Option Explicit On
Option Infer On

Imports CaptureEngine.Video.Backends.Fake

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Test-only factory that constructs FakeVideoCaptureBackend instances.
    ''' (P1-A v1.3.1 §7.3)
    ''' </summary>
    Public NotInheritable Class FakeVideoCaptureBackendFactory
        ' This class is intentionally NOT in the production factory's surface —
        ' tests reference FakeVideoCaptureBackend directly. It exists to
        ' demonstrate the factory pattern and to allow future tests to
        ' inject fake construction via a common IVideoCaptureBackendFactory
        ' interface.
        Public Function Create() As FakeVideoCaptureBackend
            Return New FakeVideoCaptureBackend()
        End Function
    End Class
End Namespace
