Option Strict On
Option Explicit On
Option Infer On

' SessionEndBroadcastPolicy.vb
'
' Pure decision policy for the Duluka session-end broadcast contract
' (Engine → Hub → Overlay). Extracted from RecordingEngineHost so the
' decision matrix is unit-testable without WinForms/TCP/hardware.
'
' CONTRACT (audit 2026-09-06, P1 × 2):
'   A RecordingEngine session can end three ways:
'     1. Manual stop  — HandleRecordingStop awaits the task and owns the
'        engine_recording_saved/error + engine_record_stop response.
'     2. Natural expiry — DurationSeconds elapses with no Stop() call.
'     3. Async failure — StartSession faults (LiveMux start abort, NVENC
'        fault, session exception).
'   Endings 2 and 3 previously ended SILENTLY: no event ever reached the
'   Overlay, which kept showing "Recording" until the next user action.
'   The completion watcher therefore broadcasts for every UNOWNED ending;
'   the manual-stop path claims ownership first and the watcher stands
'   down (exactly-once semantics, no double toast).

Namespace CaptureEngine.Recording

    ''' <summary>Which Hub event a session ending must produce.</summary>
    Public Enum SessionEndAction
        ''' <summary>No broadcast — the owning path (manual stop) handles it.</summary>
        None
        ''' <summary>Broadcast engine_recording_saved (file really written).</summary>
        Saved
        ''' <summary>Broadcast engine_recording_error (failed or faulted session).</summary>
        [Error]
    End Enum

    Public NotInheritable Class SessionEndBroadcastPolicy

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Decide the broadcast action for a completed session.
        ''' </summary>
        ''' <param name="result">
        ''' The SessionResult of a RanToCompletion session task, or Nothing
        ''' when the task faulted (no result exists).</param>
        ''' <param name="stopPathOwnsBroadcast">
        ''' True when the manual-stop path claimed this session's end —
        ''' it responds via engine_record_stop and broadcasts itself; the
        ''' watcher must stay silent to keep exactly-once semantics.</param>
        Public Shared Function Decide(result As SessionResult,
                                      stopPathOwnsBroadcast As Boolean) As SessionEndAction
            If stopPathOwnsBroadcast Then Return SessionEndAction.None
            If result Is Nothing Then Return SessionEndAction.[Error]
            If result.Pass Then Return SessionEndAction.Saved
            Return SessionEndAction.[Error]
        End Function

        ''' <summary>
        ''' Human-readable failure detail for the engine_recording_error
        ''' payload — mirrors the stop path's "pass=False: ..." evidence line
        ''' so both failure shapes carry the same diagnostics.
        ''' </summary>
        Public Shared Function DescribeFailure(result As SessionResult, faultMessage As String) As String
            If result IsNot Nothing Then
                Return $"pass=False: frames={result.FramesEncoded}, file={result.FileExists}, " &
                       $"video={result.VideoStreamFound}, audio={result.AudioStreamFound}" &
                       If(String.IsNullOrEmpty(result.ErrorMessage), "", $", err={result.ErrorMessage}")
            End If
            If Not String.IsNullOrEmpty(faultMessage) Then
                Return $"session faulted: {faultMessage}"
            End If
            Return "session ended without a result"
        End Function

    End Class

End Namespace
