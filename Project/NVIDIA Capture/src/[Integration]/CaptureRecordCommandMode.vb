''' <summary>
''' Opt-in migration: Container sends record commands to the worker REST API.
''' Keep the legacy NodeAPI poller as the default until both sides are deployed
''' together. Instant Replay and actual-state reporting are independent.
''' </summary>
Friend NotInheritable Class CaptureRecordCommandMode
    Private Sub New()
    End Sub

    Friend Shared Function UseContainerRecordCommands(value As String) As Boolean
        Return String.Equals(value, "1", StringComparison.Ordinal)
    End Function
End Class
