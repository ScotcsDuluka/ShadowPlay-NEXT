Imports System
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Threading.Tasks

' Backend reachability probe for the duplicate/health guards.
'   Free            — nothing accepts on (bindHost, port): we spawn.
'   Healthy         — a healthy backend already serves the port (idempotent
'                     no-op; whoever it is). Two liveness shapes are real:
'                     /Backend/v.1.0/health (our Backend\) and /health (the
'                     real Web Helper's duluka contract — live-probed 200,
'                     while its /Backend/v.1.0/health sits behind auth 401).
'   OccupiedForeign — the port is held but answers neither shape: a doomed
'                     spawn would only EADDRINUSE-crash, so refuse.
Friend Enum BackendState
    Free
    Healthy
    OccupiedForeign
End Enum

Friend Class PortProbe

    ' Both real liveness shapes, guard order: ours first, then the real host.
    Public Shared ReadOnly LivenessPaths As String() = {"/Backend/v.1.0/health", "/health"}

    Public Shared Function Probe(bindHost As String, port As Integer, healthPath As String) As BackendState
        Dim tcp As TcpClient = Nothing
        Try
            tcp = New TcpClient()
            Dim connectTask = tcp.ConnectAsync(bindHost, port)
            If Not connectTask.Wait(300) Then
                Return BackendState.Free
            End If
        Catch
            Return BackendState.Free
        Finally
            If tcp IsNot Nothing Then tcp.Dispose()
        End Try

        Return If(IsBackendAlive(bindHost, port), BackendState.Healthy, BackendState.OccupiedForeign)
    End Function

    ' Port-guard question: does ANY real backend liveness shape answer?
    Public Shared Function IsBackendAlive(bindHost As String, port As Integer) As Boolean
        For Each path In LivenessPaths
            If TryUrl(bindHost, port, path) Then Return True
        Next
        Return False
    End Function

    ' Post-spawn proof: the exact contract path of OUR backend must answer.
    Public Shared Function IsHealthy(bindHost As String, port As Integer, healthPath As String) As Boolean
        Return TryUrl(bindHost, port, healthPath)
    End Function

    Private Shared Function TryUrl(bindHost As String, port As Integer, path As String) As Boolean
        Try
            Dim handler As New HttpClientHandler With {.UseProxy = False}
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromMilliseconds(2000)
                Dim url As String = "http://" & bindHost & ":" &
                    port.ToString(Globalization.CultureInfo.InvariantCulture) & path
                Using response As HttpResponseMessage =
                        client.GetAsync(url).GetAwaiter().GetResult()
                    Return response.IsSuccessStatusCode
                End Using
            End Using
        Catch
            Return False
        End Try
    End Function

End Class
