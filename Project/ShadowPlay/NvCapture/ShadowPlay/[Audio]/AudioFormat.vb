''' <summary>
''' Audio sample format contract — used by AudioFileWriter (producer) to
''' report the real WASAPI format, and by CaptureEngine for diagnostics.
''' </summary>
Public Class AudioFormat

    Public Property SampleRate As Integer = 48000
    Public Property Channels As Integer = 2
    Public Property BitsPerSample As Integer = 32
    Public Property IsFloat As Boolean = True

    ''' <summary>
    ''' Channel layout string — FFmpeg-compatible (e.g. "stereo", "mono",
    ''' "5.1", "7.1"). Defaults to "stereo" since 2-channel PCM is by far
    ''' the most common case. AudioFileWriter fills this from the WASAPI
    ''' WaveFormat's channel mask when possible.
    ''' </summary>
    Public Property ChannelLayout As String = "stereo"

    Public ReadOnly Property FFmpegFormatArg As String
        Get
            If IsFloat Then Return "f32le"
            Select Case BitsPerSample
                Case 16 : Return "s16le"
                Case 24 : Return "s24le"
                Case 32 : Return "s32le"
                Case Else : Return "s16le"
            End Select
        End Get
    End Property

    ''' <summary>
    ''' Guess a channel layout string from the channel count. Used as a
    ''' fallback when WASAPI doesn't provide a channel mask. Returns
    ''' "unspecified" for ambiguous counts (e.g. 5 channels could be 4.1
    ''' OR 5.0) — caller should let downstream (FFmpeg) decide based on
    ''' explicit count, not guess a wrong layout.
    ''' FFmpeg accepts: mono, stereo, 2.1, 3.0, 3.0(back), 4.0, quad,
    ''' 5.0, 5.0(side), 4.1, 5.1, 5.1(side), 6.0, 6.0(front), hexagonal,
    ''' 6.1, 6.1(back), 7.0, 7.0(front), 7.1, 7.1(wide), octagonal.
    ''' </summary>
    Public Shared Function LayoutFromChannelCount(channels As Integer) As String
        Select Case channels
            Case 1 : Return "mono"
            Case 2 : Return "stereo"
            Case 3 : Return "2.1"
            Case 4 : Return "quad"
            Case 6 : Return "5.1"
            Case 7 : Return "6.1"
            Case 8 : Return "7.1"
            Case Else : Return "unspecified"
        End Select
    End Function

    Public Overrides Function ToString() As String
        Dim enc As String = If(IsFloat, "float", "int")
        Return $"{SampleRate}Hz/{Channels}ch/{BitsPerSample}bit {enc} [{ChannelLayout}]"
    End Function

End Class

