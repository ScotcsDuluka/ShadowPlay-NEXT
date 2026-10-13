Option Strict On
Option Explicit On
Option Infer On

' SilenceKeepAlive.vb — the OBS trick that solves silent-stream WASAPI gaps.
'
' PROBLEM (owner evidence all day): WASAPI loopback delivers DataAvailable
' callbacks ONLY while audio is actually rendering. Silence = no callbacks.
' Every downstream hack (gap-fill, pre-roll, clock steering, noise issues
' when callbacks resume) exists because of this one behavior.
'
' OBS'S SOLUTION (audio-io loopback source): continuously render SILENT PCM
' to the SAME output device the loopback captures from. The mixer then always
' has something to play, so the loopback always has something to deliver —
' the callback stream becomes perfectly continuous, and when nothing else is
' playing the delivered buffers ARE the silence we rendered (real, clean,
' correctly-timed silence instead of reconstructed silence).
'
' With this active:
'   - gap-fill inserts nothing (no gaps exist)
'   - clock steering has nothing to correct (the stream is wall-clock by
'     construction — the device paces itself)
'   - no pops/clicks at silence→sound transitions (no reconstruction)
'   - 'noise when quiet' disappears (no misaligned reconstruction)
'
' Implementation: NAudio WasapiLoopbackCapture is capture-only, so the keep-
' alive needs its own RENDER side. We open a WasapiOut on the DEFAULT device
' (the same device loopback captures by default) and play an endless stream
' of zero samples. NAudio's WasapiOut needs real data feeding, so we use a
' SilenceProvider (infinite zero stream) — no thread, no CPU, the mixer just
' gets zeros.
'
' Volume note: WasapiOut at volume 1.0 renders true zeros — inaudible by
' definition. It does NOT touch the user's volume settings.

Imports NAudio.CoreAudioApi
Imports NAudio.Wave

Namespace CaptureEngine.Recording

    ''' <summary>
    ''' Renders endless silence to the loopback device so the capture stream
    ''' never stops. Dispose when recording ends.
    ''' </summary>
    Public NotInheritable Class SilenceKeepAlive
        Implements IDisposable

        Private ReadOnly _out As WasapiOut
        Private _disposed As Boolean = False
        Private ReadOnly _started As Boolean = False

        ''' <summary>Device name used for the keep-alive (evidence).</summary>
        Public ReadOnly Property DeviceName As String

        Public Sub New(Optional device As MMDevice = Nothing)
            Try
                Dim target As MMDevice = device
                If target Is Nothing Then
                    Using enumr As New MMDeviceEnumerator()
                        target = enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    End Using
                End If
                DeviceName = If(target?.FriendlyName, "?")

                ' WaveFormat from the DEVICE (loopback captures exactly this)
                Dim fmt As WaveFormat = target.AudioClient.MixFormat

                ' SilenceProvider: infinite, zero-filled, correctly formatted
                Dim silence As New SilenceProvider(fmt)

                ' Ctor: (device, shareMode, useEventSync, latencyMs)
                _out = New WasapiOut(target, AudioClientShareMode.Shared, True, 50)
                _out.Init(New WaveProviderToWaveStreamAdaptor(silence))
                _out.Volume = 1.0F
                _out.Play()
                _started = True
            Catch
                ' Keep-alive is best-effort: if it cannot start, the AudioTap
                ' gap-fill still handles silence the old way.
                _out = Nothing
            End Try
        End Sub

        Public ReadOnly Property IsActive As Boolean
            Get
                Return _started AndAlso _out IsNot Nothing AndAlso _out.PlaybackState = PlaybackState.Playing
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try
                If _out IsNot Nothing Then
                    _out.Stop()
                    _out.Dispose()
                End If
            Catch
            End Try
        End Sub

    End Class

    ''' <summary>
    ''' Adapts an IWaveProvider into a read-through WaveStream so WasapiOut
    ''' can pull from it forever (SilenceProvider never ends and never blocks:
    ''' it just hands out zeros).
    ''' </summary>
    Friend NotInheritable Class WaveProviderToWaveStreamAdaptor
        Inherits WaveStream

        Private ReadOnly _provider As IWaveProvider
        Private _position As Long = 0

        Public Sub New(provider As IWaveProvider)
            _provider = provider
        End Sub

        Public Overrides ReadOnly Property WaveFormat As WaveFormat
            Get
                Return _provider.WaveFormat
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return Long.MaxValue \ 2
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return _position
            End Get
            Set(value As Long)
                _position = value
            End Set
        End Property

        Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
            Dim n As Integer = _provider.Read(buffer, offset, count)
             _position += n
            Return n
        End Function

    End Class

End Namespace
