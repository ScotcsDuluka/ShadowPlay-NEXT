Option Strict On
Namespace DulukaEngine.Video
    Public Enum SourceFaultKind
        AccessLost
        Disconnected
        DeviceRemoved
        OutputChanged
        InvalidFrame
    End Enum
    Public Interface ISourceLifecycleSink
        Sub SourceFault(kind As SourceFaultKind)
    End Interface
    Public Interface ISourceStampedVideoFrame
        Inherits ID3D11VideoFrame
        ReadOnly Property SourceTarget As CaptureTarget
        ReadOnly Property SourceGeneration As Long
    End Interface
    Public NotInheritable Class SourceGenerationIds
        Private Shared _next As Long
        Public Shared Function Allocate() As Long
            Return Threading.Interlocked.Increment(_next)
        End Function
    End Class
    Public NotInheritable Class SourceTimeUnits
        Public Shared Function QpcTo100ns(value As Long, frequency As Long) As Long
            ' Signed deltas are also used by existing DXGI acquire diagnostics.
            ' Absolute frame stamps are validated separately by the session source.
            If frequency <= 0 Then Throw New ArgumentException("source_invalid_qpc")
            Return Decimal.ToInt64(Decimal.Truncate(CDec(value) * 10000000D / CDec(frequency)))
        End Function
    End Class
    Public NotInheritable Class VideoColorDescriptor
        Public ReadOnly Property FullRange As Boolean
        Public ReadOnly Property Primaries As Integer = 1
        Public ReadOnly Property Transfer As Integer = 1
        Public ReadOnly Property Matrix As Integer = 1
        Public Sub New(fullRange As Boolean)
            Me.FullRange = fullRange
        End Sub
        Public Function SameColor(other As VideoColorDescriptor) As Boolean
            Return other IsNot Nothing AndAlso FullRange = other.FullRange AndAlso Primaries = other.Primaries AndAlso
                Transfer = other.Transfer AndAlso Matrix = other.Matrix
        End Function
    End Class
    Public Interface IColorVideoFrame
        ReadOnly Property Color As VideoColorDescriptor
    End Interface
End Namespace
