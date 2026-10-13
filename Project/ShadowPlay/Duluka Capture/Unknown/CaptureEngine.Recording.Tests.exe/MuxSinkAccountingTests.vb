Option Strict On
Option Explicit On
Option Infer On

' MuxSinkAccountingTests.vb — audit fix regression (2026-09-07).
'
' The pending-cap discard in AudioEngineMuxSink used to vanish silently:
' packets arriving while the sink is unaligned/unattached are buffered up
' to MaxPendingBytes (16 MB); anything beyond was dropped WITHOUT a counter
' — the same "pass=True หลอก" class as the mux-drop hole fixed in the
' clap-sync round. The sink now counts them (PendingDroppedBytes) and the
' session folds the total into result.AudioDroppedBytes / MicDroppedBytes,
' so AudioAccountingOk is a real invariant on every window.
'
' Cap rule under test (AudioEngineMuxSink.Write): a packet is buffered iff
' _pendingBytes + len ≤ MaxPendingBytes; otherwise it is counted dropped.
' _pendingBytes only grows on ACCEPT — a later small packet can fit again.
'
' Pure VB — no hardware, no WASAPI, deterministic.

Imports System
Imports CaptureEngine.Audio
Imports CaptureEngine.FFmpegBackend
Imports CaptureEngine.Recording

Namespace CaptureEngine.Recording.Tests

    Friend Module MuxSinkAccountingTests

        Private Const MaxPendingBytes As Long = 16L * 1024 * 1024
        Private Const PacketBytes As Integer = 192000   ' 48kHz stereo PCM16 → 50ms

        Public Sub RunAll()
            Console.WriteLine("── MuxSink accounting (pending-cap drops are counted) ──")
            TestRunner.RunTest("MUXSINK: pending-cap discard is counted, not silent", AddressOf Test_PendingCapDropCounted)
            TestRunner.RunTest("MUXSINK: drop total mirrors the cap rule exactly", AddressOf Test_DropMirrorsCapRule)
            TestRunner.RunTest("MUXSINK: aligned+attached writes never count as drops", AddressOf Test_AlignedWritesNotCountedAsDrops)
        End Sub

        ''' <summary>48kHz stereo PCM16 packet.</summary>
        Private Function MakePacket(pts100ns As Long, bytes As Integer) As AudioPacket
            Dim frames As Integer = bytes \ 4   ' 2ch × 2B
            Dim data(bytes - 1) As Byte
            Return New AudioPacket(AudioTrackKind.System, pts100ns, frames, data, False,
                                   0, 0, 0, 48000, 2)
        End Function

        ''' <summary>Unaligned/unattached sink: the first 9 MB packet fits
        ''' under the 16 MB cap, the second (18 MB cumulative) must be
        ''' REJECTED and counted. A small packet then fits again (pending
        ''' only grew on accept) and must NOT be counted.</summary>
        Private Sub Test_PendingCapDropCounted()
            Dim sink As New AudioEngineMuxSink(AudioTrackKind.System)

            Const big As Integer = 9 * 1024 * 1024
            sink.Write(MakePacket(0, big))                  ' accepted (9 MB ≤ 16 MB)
            TestRunner.Assert(sink.PendingDroppedBytes = 0,
                              $"first packet under the cap must not count as dropped (got {sink.PendingDroppedBytes})")

            sink.Write(MakePacket(10000000, big))           ' rejected (18 MB > 16 MB)
            TestRunner.Assert(sink.PendingDroppedBytes = big,
                              $"over-cap packet must be counted as dropped (expected {big}, got {sink.PendingDroppedBytes})")

            sink.Write(MakePacket(20000000, 1024))          ' accepted again (9 MB + 1 KB ≤ 16 MB)
            TestRunner.Assert(sink.PendingDroppedBytes = big,
                              $"post-rejection small packet must be accepted, not counted (expected {big}, got {sink.PendingDroppedBytes})")
        End Sub

        ''' <summary>Feed 100 × 50ms packets (19.2 MB total) into an
        ''' unaligned sink: 87 fit (16,704,000 ≤ 16 MB), the remaining 13
        ''' must be counted dropped — exactly the cap rule, byte-exact.</summary>
        Private Sub Test_DropMirrorsCapRule()
            Dim sink As New AudioEngineMuxSink(AudioTrackKind.System)
            For i As Integer = 0 To 99
                sink.Write(MakePacket(CLng(i) * 500000, PacketBytes))
            Next

            Dim accepted As Long = 0
            Dim dropped As Long = 0
            For i As Integer = 0 To 99
                If accepted + PacketBytes <= MaxPendingBytes Then
                    accepted += PacketBytes
                Else
                    dropped += PacketBytes
                End If
            Next
            TestRunner.Assert(dropped > 0, "test precondition: the 100-packet run must overflow the cap")
            TestRunner.Assert(sink.PendingDroppedBytes = dropped,
                              $"drop total must mirror the cap rule exactly (expected {dropped}, got {sink.PendingDroppedBytes})")
        End Sub

        ''' <summary>Production order is AttachMux → SetVideoStart → stream.
        ''' With a mux attached (started=False → writes are pipe no-ops) and
        ''' alignment set, streaming writes take the aligned path and must
        ''' NEVER inflate the pending-drop counter.</summary>
        Private Sub Test_AlignedWritesNotCountedAsDrops()
            Dim sink As New AudioEngineMuxSink(AudioTrackKind.Microphone)
            Dim mux As New LiveMuxSession("ffmpeg", "sink-test.mp4", 30, 48000, 2, 0, 0, False, 1.0F, 1.0F)
            sink.AttachMux(mux)
            sink.SetVideoStart(10000000)

            Dim pts As Long = 10000000
            For i As Integer = 0 To 199
                Dim pkt = MakePacket(pts, PacketBytes)
                sink.Write(pkt)
                pts += CLng(pkt.Frames) * 10000000L \ 48000
            Next

            TestRunner.Assert(sink.PendingDroppedBytes = 0,
                              $"aligned+attached writes must not count as drops (got {sink.PendingDroppedBytes})")

            ' The dummy mux never started ffmpeg — dispose is a clean no-op.
            Try : mux.Dispose() : Catch : End Try
        End Sub

    End Module

End Namespace
