Imports System
Imports System.Diagnostics
Imports System.IO

' node.exe resolution for the INSTALLED layout — never the repo working
' directory (goal: ทำงานจาก installed owner-layout). Precedence:
'   1. NVBACKEND_NODE_EXE env   — explicit deployment override
'   2. <NodeAPI>\NvNode.exe     — portable Node runtime beside the host
'      (preferred project runtime); then <NodeAPI>\node.exe
'                                 deploy/install.ps1 fallback hint)
'   3. PATH                     — system node
'   4. %ProgramFiles%\nodejs\node.exe and
'      %LocalAppData%\Programs\nodejs\node.exe — deploy/install.ps1's
'      candidate list
' The backend itself resolves its dependencies from NodeAPI\node_modules
' by standard node resolution (cwd + entry beside it) — the host must not
' set NODE_PATH (launch contract: NvNode's own module.paths come from the
' entry location, not from injected env).
Friend Class NodeRuntime

    Public Shared Function Resolve(baseDir As String) As String
        Dim nodeOverride = Environment.GetEnvironmentVariable("NVBACKEND_NODE_EXE")
        If Not String.IsNullOrWhiteSpace(nodeOverride) Then
            If File.Exists(nodeOverride) Then
                HostLog.Log("node runtime: NVBACKEND_NODE_EXE override -> " & nodeOverride)
                Return nodeOverride
            End If
            HostLog.Error("NVBACKEND_NODE_EXE='" & nodeOverride & "' does not exist")
            Return Nothing
        End If

        ' ★ identity: runtime ของเราชื่อ NvNode.exe (ไม่โชว์ node.exe ใน Task Manager)
        Dim nvnode = Path.Combine(baseDir, "NvNode.exe")
        If File.Exists(nvnode) Then
            HostLog.Log("node runtime: portable slot (NvNode) -> " & nvnode)
            Return nvnode
        End If

        Dim portable = Path.Combine(baseDir, "node.exe")
        If File.Exists(portable) Then
            HostLog.Log("node runtime: portable slot -> " & portable)
            Return portable
        End If

        Dim pathEnv = Environment.GetEnvironmentVariable("PATH")
        If pathEnv IsNot Nothing Then
            For Each pathDir In pathEnv.Split(";"c)
                If String.IsNullOrWhiteSpace(pathDir) Then Continue For
                Try
                    Dim candidate = Path.Combine(pathDir.Trim().Trim(""""c), "node.exe")
                    If File.Exists(candidate) Then
                        HostLog.Log("node runtime: PATH -> " & candidate)
                        Return candidate
                    End If
                Catch
                End Try
            Next
        End If

        Dim knownCandidates As String() = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe")}
        For Each candidate In knownCandidates
            If File.Exists(candidate) Then
                HostLog.Log("node runtime: known install location -> " & candidate)
                Return candidate
            End If
        Next

        HostLog.Error("node.exe not found (searched: NVBACKEND_NODE_EXE, " & portable & ", PATH, %ProgramFiles%\nodejs, %LocalAppData%\Programs\nodejs)")
        HostLog.Error("the Project NodeAPI backend needs its NvNode.exe or Node.js on PATH")
        Return Nothing
    End Function

    ' `node --version` probe — boot-log proof of the resolved runtime.
    Public Shared Function TryGetVersion(nodeExe As String) As String
        Try
            Dim psi As New ProcessStartInfo With {
                .FileName = nodeExe,
                .Arguments = "--version",
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True}
            Using p = Process.Start(psi)
                Dim stdout As String = p.StandardOutput.ReadToEnd()
                p.WaitForExit(5000)
                Return stdout.Trim()
            End Using
        Catch
            Return Nothing
        End Try
    End Function

End Class
