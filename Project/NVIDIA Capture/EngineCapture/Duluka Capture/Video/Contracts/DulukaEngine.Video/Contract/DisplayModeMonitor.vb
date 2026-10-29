Option Strict On
Option Explicit On
Option Infer On

Namespace DulukaEngine.Video

    ' DisplayModeMonitor.vb — contract §B-7 / §D.
    '
    ' Mode/topology supervision is EVENT + BOUNDED POLL based. There is no
    ' per-frame mode detection anywhere: the monitor classifies two sparse
    ' inventory snapshots (or an injected synthetic pair in tests) and the
    ' session reacts to transitions only. Source health itself (frame
    ' progress, access-lost) stays with the capture backend's existing
    ' source-fault plumbing.
    Public Enum DisplayModeEventKind
        ''' <summary>The pinned target is still present and eligible.</summary>
        Stable
        ''' <summary>The output set changed but the pinned target reappeared
        ''' (reattach/hotplug). Source recreation on the same identity is the
        ''' authorized response, never silent retargeting.</summary>
        TopologyChanged
        ''' <summary>The pinned target is gone or no longer eligible. The
        ''' session must not silently capture a different monitor.</summary>
        TargetLost
    End Enum

    Public NotInheritable Class DisplayModeMonitor
        Private Sub New()
        End Sub

        ''' <summary>
        ''' Classify the pinned target against the current eligible output
        ''' set. Pure function — the DXGI adapter supplies real snapshots,
        ''' tests supply synthetic ones.
        ''' </summary>
        Public Shared Function Classify(pinned As CaptureTarget,
                                        currentOutputs As IReadOnlyList(Of CaptureTarget)) As DisplayModeEventKind
            If pinned Is Nothing Then Throw New ArgumentNullException(NameOf(pinned))
            If currentOutputs Is Nothing Then Return DisplayModeEventKind.TargetLost
            For Each output In currentOutputs
                If output IsNot Nothing AndAlso output.Eligible AndAlso pinned.SameOutput(output) Then
                    ' Same identity and still eligible: this poll is stable.
                    ' (Topology-wide changes are observable through the
                    ' TopologyId carried by CaptureTarget; a differing
                    ' topology on the SAME output is still the pinned target.)
                    Return DisplayModeEventKind.Stable
                End If
            Next
            Return DisplayModeEventKind.TargetLost
        End Function
    End Class
End Namespace
