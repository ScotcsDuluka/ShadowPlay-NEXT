Option Strict On
Option Explicit On
Option Infer On

Namespace DulukaEngine.Video

    ' ExclusiveCapturePolicy.vb — SPN-ADR-2026-10-24-SESSION-001 §C/§D.
    '
    ' The Exclusive Capture API is an INDEPENDENT selection from the desktop
    ' capture API. It names the source that must prove it captures the
    ' selected game's tested presentation path. A label is never proof:
    ' every value here resolves to either an implemented verified-native
    ' source or a precise not-implemented failure. No silent substitution.
    Public NotInheritable Class ExclusiveCapturePolicy
        Private Sub New()
        End Sub

        ''' <summary>The only exclusive-capable verified native source today:
        ''' DXGI Desktop Duplication (captures many exclusive presentations
        ''' without any special frame source). It resolves to the same
        ''' production backend as the desktop route, per contract §B-3:
        ''' separate selections MAY resolve to one implementation.</summary>
        Public Const DxgiDesktopDuplication As String = "dxgi_desktop_duplication"

        Public Const Auto As String = "auto"

        ''' <summary>
        ''' Resolve the requested exclusive capture API.
        ''' Returns the resolved API plus a truthful reason string, or throws
        ''' NotSupportedException for anything not implemented. Empty input
        ''' means "no explicit exclusive request" and resolves to "" — the
        ''' desktop route stays the only authority for that session.
        ''' </summary>
        Public Shared Function Resolve(requested As String) As Tuple(Of String, String)
            Dim value = If(requested, "").Trim().ToLowerInvariant()
            Select Case value
                Case ""
                    Return Tuple.Create("", "exclusive_not_requested")
                Case Auto
                    ' Auto may only pick from implemented, verified candidates.
                    ' Exactly one exists today, so auto is deterministic.
                    Return Tuple.Create(DxgiDesktopDuplication, "auto_resolved_desktop_duplication")
                Case DxgiDesktopDuplication, "ddagrab"
                    Return Tuple.Create(DxgiDesktopDuplication, "explicit_desktop_duplication_for_exclusive")
                Case Else
                    ' wgc_monitor, game_hook_source, anything else — fail
                    ' closed. Never silently cross to a different source.
                    Throw New NotSupportedException("exclusive_capture_api_not_implemented")
            End Select
        End Function

        ''' <summary>True when the resolved value means "desktop route stays
        ''' authoritative; no separate exclusive source was requested".</summary>
        Public Shared Function IsNoRequest(resolvedApi As String) As Boolean
            Return String.IsNullOrEmpty(resolvedApi)
        End Function
    End Class

    ''' <summary>
    ''' Evidence bundle for the exclusive verification gate. Mirrors contract
    ''' §H: Declared/Wired &lt; DeviceEligible &lt; LiveFrameVerified. An
    ''' exclusive route is "verified" only after real, timestamped frames on
    ''' the pinned target — never from a successful start call.
    ''' </summary>
    Public NotInheritable Class ExclusiveCaptureEvidence
        Public Property TargetIdentityMatched As Boolean
        Public Property RealFramesDelivered As Long
        Public Property TimestampsMonotonic As Boolean
        Public Property TerminalFault As Boolean
    End Class

    Public NotInheritable Class ExclusiveVerificationGate
        Private Sub New()
        End Sub

        Public Const MinimumRealFrames As Integer = 1

        ''' <summary>Returns "" when the exclusive route is verified, or a
        ''' precise reason code when it is not. Failure is the default.</summary>
        Public Shared Function Evaluate(evidence As ExclusiveCaptureEvidence,
                                        Optional minimumRealFrames As Integer = MinimumRealFrames) As String
            If evidence Is Nothing Then Return "exclusive_evidence_missing"
            If Not evidence.TargetIdentityMatched Then Return "exclusive_target_identity_mismatch"
            If evidence.TerminalFault Then Return "exclusive_terminal_fault"
            If Not evidence.TimestampsMonotonic Then Return "exclusive_timestamps_invalid"
            If evidence.RealFramesDelivered < minimumRealFrames Then Return "exclusive_real_frames_insufficient"
            Return ""
        End Function
    End Class
End Namespace
