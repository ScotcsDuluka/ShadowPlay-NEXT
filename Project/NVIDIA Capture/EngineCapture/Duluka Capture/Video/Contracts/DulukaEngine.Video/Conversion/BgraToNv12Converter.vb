Option Strict On
Option Explicit On

Namespace DulukaEngine.Video
    ''' <summary>
    ''' Explicit CPU-staging BGRA8 to packed NV12 conversion for test-owned input.
    ''' Input: borrowed top-down BGRA rows (possibly padded row pitch).
    ''' Output: packed NV12, BT.709. Original overload is LIMITED; explicit
    ''' fullRange overload supports FULL with an accompanying color descriptor.
    '''
    ''' I3 synthetic-only transport. Does NOT map a D3D11 texture, capture pixels,
    ''' select a display or prove a live Desktop source/encoder pairing.
    ''' Device synchronization, adapter LUID and source-generation validation
    ''' remain the responsibility of the caller. No screen/audio input is opened.
    ''' </summary>
    Public NotInheritable Class BgraToNv12Converter
        Private Sub New()
        End Sub

        Public Shared Function ConvertTopDown(bgra As Byte(),
                                               width As Integer, height As Integer, bgraRowPitch As Integer) As Byte()
            Return ConvertTopDown(bgra, width, height, bgraRowPitch, False)
        End Function
        Public Shared Function ConvertTopDown(bgra As Byte(),
                                               width As Integer,
                                               height As Integer,
                                               bgraRowPitch As Integer, fullRange As Boolean) As Byte()
            If width <= 0 OrElse height <= 0 OrElse
                (width And 1) <> 0 OrElse (height And 1) <> 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(width), "nv12_requires_positive_even_dimensions")
            End If
            Dim minPitch As Long = CLng(width) * 4L
            If CLng(bgraRowPitch) < minPitch Then
                Throw New ArgumentOutOfRangeException(NameOf(bgraRowPitch), "bgra_row_pitch_too_small")
            End If
            Dim requiredSource As Long = CLng(height - 1) * bgraRowPitch + minPitch
            Dim outputBytes As Long = CLng(width) * height * 3L \ 2L
            If requiredSource > Integer.MaxValue OrElse outputBytes > Integer.MaxValue Then
                Throw New ArgumentOutOfRangeException(NameOf(width), "frame_allocation_overflow")
            End If
            If bgra Is Nothing OrElse bgra.LongLength < requiredSource Then
                Throw New ArgumentException("bgra_buffer_short_for_pitch", NameOf(bgra))
            End If
            Dim packed(CInt(outputBytes) - 1) As Byte
            Dim uvStart As Integer = CInt(CLng(width) * height)
            For y As Integer = 0 To height - 1
                Dim sourceRow As Integer = CInt(CLng(y) * bgraRowPitch)
                Dim targetRow As Integer = y * width
                For x As Integer = 0 To width - 1
                    Dim p As Integer = sourceRow + x * 4
                    Dim b As Integer = CInt(bgra(p))
                    Dim g As Integer = CInt(bgra(p + 1))
                    Dim r As Integer = CInt(bgra(p + 2))
                    packed(targetRow + x) = If(fullRange,
                        Quantize(0.2126R * r + 0.7152R * g + 0.0722R * b, 0, 255),
                        Quantize(16.0R + 0.182586R * r + 0.614231R * g + 0.062007R * b, 16, 235))
                Next
            Next
            For y As Integer = 0 To height - 1 Step 2
                Dim uvRow As Integer = uvStart + (y \ 2) * width
                For x As Integer = 0 To width - 1 Step 2
                    Dim rr As Integer = 0, gg As Integer = 0, bb As Integer = 0
                    For dy As Integer = 0 To 1
                        For dx As Integer = 0 To 1
                            Dim p As Integer = CInt(CLng(y + dy) * bgraRowPitch) + (x + dx) * 4
                            bb += CInt(bgra(p))
                            gg += CInt(bgra(p + 1))
                            rr += CInt(bgra(p + 2))
                        Next
                    Next
                    Dim r As Double = rr / 4.0R, g As Double = gg / 4.0R, b As Double = bb / 4.0R
                    packed(uvRow + x) = If(fullRange, Quantize(128.0R - 0.114572R * r - 0.385428R * g + 0.5R * b, 0, 255),
                        Quantize(128.0R - 0.100644R * r - 0.338572R * g + 0.439216R * b, 16, 240))
                    packed(uvRow + x + 1) = If(fullRange, Quantize(128.0R + 0.5R * r - 0.454153R * g - 0.045847R * b, 0, 255),
                        Quantize(128.0R + 0.439216R * r - 0.398942R * g - 0.040274R * b, 16, 240))
                Next
            Next
            Return packed
        End Function

        Private Shared Function Quantize(value As Double, minimum As Integer, maximum As Integer) As Byte
            Return CByte(Math.Clamp(CInt(Math.Round(value, MidpointRounding.AwayFromZero)), minimum, maximum))
        End Function
    End Class
End Namespace
