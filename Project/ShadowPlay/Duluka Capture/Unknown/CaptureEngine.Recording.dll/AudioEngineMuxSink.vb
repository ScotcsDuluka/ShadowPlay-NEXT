Option Explicit On
Option Strict On

Imports System.Threading
Imports CaptureEngine.Audio
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording

    ''' <summary>
    ''' Bridges the shared Audio Engine timeline into a video's LiveMux pipe.
    ''' Audio is buffered until video t0 exists, then the head is trimmed or
    ''' silence-padded from packet PTS. WAV evidence, when enabled, receives
    ''' the exact same aligned byte stream.
    ''' </summary>
    Public NotInheritable Class AudioEngineMuxSink
        Implements IAudioSink

        Private ReadOnly _track As AudioTrackKind
        Private ReadOnly _wav As AudioWavSink
        Private _sampleRate As Integer
        Private _channels As Integer
        Private ReadOnly _pending As New List(Of AudioPacket)()
        Private ReadOnly _sync As New Object()
        Private _mux As LiveMuxSession
        Private _videoStartQpc100ns As Long
        Private _expectedPts100ns As Long
        Private _attached As Boolean
        Private _aligned As Boolean
        Private _pendingBytes As Long
        Private Const MaxPendingBytes As Long = 16L * 1024 * 1024

        ' ★ Accounting fix: packets discarded because the pending cap was
        ' full used to vanish silently — the same "pass=True หลอก" class as
        ' the mux-drop hole. Counted here, folded into the session result.
        Private _droppedBytes As Long

        ''' <summary>Bytes discarded while unaligned/unattached because the
        ''' pending buffer was full (16 MB cap). Read by the session at stop.</summary>
        Public ReadOnly Property PendingDroppedBytes As Long
            Get
                Return Interlocked.Read(_droppedBytes)
            End Get
        End Property

        Public Sub New(track As AudioTrackKind, Optional wav As AudioWavSink = Nothing)
            _track = track
            _wav = wav
        End Sub

        Public Sub AttachMux(mux As LiveMuxSession)
            SyncLock _sync
                _mux = mux
                _attached = mux IsNot Nothing
                FlushPendingLocked()
            End SyncLock
        End Sub

        Public Sub SetVideoStart(videoStartQpc100ns As Long)
            If videoStartQpc100ns <= 0 Then Return
            SyncLock _sync
                _videoStartQpc100ns = videoStartQpc100ns
                _expectedPts100ns = videoStartQpc100ns
                _aligned = True
                FlushPendingLocked()
            End SyncLock
        End Sub

        Public Sub Write(packet As AudioPacket) Implements IAudioSink.Write
            If packet.Data Is Nothing OrElse packet.Data.Length = 0 Then Return
            SyncLock _sync
                If _sampleRate <= 0 Then _sampleRate = packet.SampleRate
                If _channels <= 0 Then _channels = Math.Max(1, packet.Channels)
                If Not _aligned OrElse Not _attached Then
                    If _pendingBytes + packet.Data.Length <= MaxPendingBytes Then
                        _pending.Add(packet)
                        _pendingBytes += packet.Data.Length
                    Else
                        Interlocked.Add(_droppedBytes, packet.Data.Length)
                    End If
                    Return
                End If
                WriteAlignedLocked(packet)
            End SyncLock
        End Sub

        Private Sub FlushPendingLocked()
            If Not _attached OrElse Not _aligned OrElse _pending.Count = 0 Then Return
            _pending.Sort(Function(a, b) a.Pts100ns.CompareTo(b.Pts100ns))
            Dim copy = _pending.ToArray()
            _pending.Clear()
            _pendingBytes = 0
            For Each packet In copy
                WriteAlignedLocked(packet)
            Next
        End Sub

        Private Sub WriteAlignedLocked(packet As AudioPacket)
            Dim bytesPerFrame As Integer = _channels * 2
            Dim duration100ns As Long = CLng(packet.Frames) * 10000000L \ _sampleRate
            Dim packetEnd As Long = packet.Pts100ns + duration100ns
            If packetEnd <= _expectedPts100ns Then Return

            Dim data As Byte() = packet.Data
            Dim startOffsetFrames As Integer = 0

            If packet.Pts100ns > _expectedPts100ns Then
                WriteSilenceLocked(packet.Pts100ns - _expectedPts100ns)
            ElseIf packet.Pts100ns < _expectedPts100ns Then
                Dim trim100ns As Long = _expectedPts100ns - packet.Pts100ns
                startOffsetFrames = Math.Min(packet.Frames,
                    CInt((trim100ns * _sampleRate + 9999999L) \ 10000000L))
            End If

            Dim remainingFrames As Integer = packet.Frames - startOffsetFrames
            If remainingFrames > 0 Then
                Dim offsetBytes As Integer = startOffsetFrames * bytesPerFrame
                Dim countBytes As Integer = Math.Min(data.Length - offsetBytes, remainingFrames * bytesPerFrame)
                If countBytes > 0 Then
                    WriteBytesLocked(data, offsetBytes, countBytes)
                    _expectedPts100ns += CLng(remainingFrames) * 10000000L \ _sampleRate
                End If
            End If
            If _expectedPts100ns < packetEnd Then _expectedPts100ns = packetEnd
        End Sub

        Private Sub WriteSilenceLocked(duration100ns As Long)
            If duration100ns <= 0 Then Return
            Dim frames As Long = (duration100ns * _sampleRate) \ 10000000L
            If frames <= 0 Then Return
            Dim bytesPerFrame As Integer = _channels * 2
            Dim remainingBytes As Long = frames * bytesPerFrame
            Dim zeros(65535) As Byte
            While remainingBytes > 0
                Dim n As Integer = CInt(Math.Min(remainingBytes, zeros.Length))
                n -= n Mod bytesPerFrame
                If n <= 0 Then Exit While
                WriteBytesLocked(zeros, 0, n)
                remainingBytes -= n
            End While
            _expectedPts100ns += frames * 10000000L \ _sampleRate
        End Sub

        Private Sub WriteBytesLocked(data As Byte(), offset As Integer, count As Integer)
            If count <= 0 Then Return
            If _wav IsNot Nothing Then _wav.Write(New AudioPacket(_track, _expectedPts100ns, count \ (_channels * 2), data, False, 0, 0, 0))
            If _mux Is Nothing Then Return
            If _track = AudioTrackKind.System Then
                _mux.FeedSystemAudioSegment(data, offset, count)
            Else
                _mux.FeedMicAudioSegment(data, offset, count)
            End If
        End Sub

    End Class

End Namespace
