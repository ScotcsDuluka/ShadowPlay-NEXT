Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

' NVIDIA Web Helper — managed host for the Project NodeAPI runtime tree.
' One job: keep its NvNode.exe + index.js backend serving 127.0.0.1:59011
' from the installed layout — never the repo working directory — with clear logs, a duplicate
' guard, a health proof and a graceful shutdown. The parity surface itself
' lives in Backend\ and is not duplicated here.
'
' Boot order (every step logged; failures are explicit exit codes):
'   log init -> config (env > config.json > defaults) -> index.js check ->
'   node runtime resolution -> duplicate guard (one host per NodeAPI dir)
'   -> port guard (healthy backend up => idempotent exit 0;
'   foreign holder => refuse) -> spawn `node index.js` (job-owned) ->
'   health proof -> supervise until child exit or shutdown signal.
'
' Launch contract (docs/PHASE-1B-LAUNCH-CONTRACT.md §5): config = JSON +
' environment only; the backend takes NO argv, so incoming argv is
' deliberately ignored.
'
' Exit codes:
'   0  ran and stopped cleanly / duplicate instance / backend already healthy
'   1  port held by a process that is not a healthy backend
'   2  node runtime not resolved
'   3  index.js missing (incomplete NvBackend tree)
'   4  node could not be started (or exited before the health proof, nonzero)
'   5  boot window elapsed without a health proof (child kept alive — logged)
'   N  backend exited on its own with code N (propagated)
Friend Module Program

    Private Const ProofTimeoutMs As Integer = 60000
    Private Const GraceMs As Integer = 8000
    Private Const WaitAfterKillMs As Integer = 5000

    Private ReadOnly StopEvent As New ManualResetEventSlim(False)
    Private ReadOnly StopLock As New Object()
    Private _child As BackendProcess
    Private _stopping As Boolean

    Function Main() As Integer
        Dim baseDir As String = AppContext.BaseDirectory
        Directory.CreateDirectory(Path.Combine(baseDir, "Logs"))
        HostLog.Init(Path.Combine(baseDir, "Logs", "NVIDIA Web Helper.log"))

        HostLog.Log("NVIDIA Web Helper host boot — pid " & Process.GetCurrentProcess().Id &
                    ", dir " & baseDir)

        Dim cfg = HostConfig.Load(baseDir)
        ApplyConsoleVisibility(baseDir)
        HostLog.Log("config: port=" & cfg.Port & " bind=" & cfg.BindHost & " health=" & cfg.HealthPath)

        Dim entry = Path.Combine(baseDir, "index.js")
        If Not File.Exists(entry) Then
            HostLog.Error("index.js not found at " & entry &
                          " — the Project NodeAPI runtime is incomplete")
            Return 3
        End If

        Dim nodeExe = NodeRuntime.Resolve(baseDir)
        If nodeExe Is Nothing Then Return 2
        Dim nodeVersion = NodeRuntime.TryGetVersion(nodeExe)
        HostLog.Log("node runtime: " & nodeExe & If(nodeVersion IsNot Nothing, " (" & nodeVersion & ")", " (version probe failed)"))

        ' duplicate-process guard — one host per installed NodeAPI directory
        Dim created As Boolean
        Dim mutexName As String = "Global\NvBackend.WebHelperHost." & DirHash(baseDir)
        Try
            _mutex = New Mutex(True, mutexName, created)
        Catch ex As Exception
            ' Global namespace refused (restricted session) — fall back per-session
            HostLog.Warn("Global mutex unavailable (" & ex.Message & ") — falling back to Local\")
            mutexName = "Local\NvBackend.WebHelperHost." & DirHash(baseDir)
            _mutex = New Mutex(True, mutexName, created)
        End Try
        If Not created Then
            HostLog.Log("duplicate guard: another NVIDIA Web Helper host from this directory is already running (" & mutexName & ") — nothing to do")
            Return 0
        End If

        ' port guard — do not spawn into a doomed EADDRINUSE
        Dim portState = PortProbe.Probe(cfg.BindHost, cfg.Port, cfg.HealthPath)
        Select Case portState
            Case BackendState.Healthy
                HostLog.Log("backend already healthy at " & cfg.BaseUrl & cfg.HealthPath & " — nothing to do (idempotent)")
                Return 0
            Case BackendState.OccupiedForeign
                HostLog.Error("port " & cfg.Port & " on " & cfg.BindHost &
                              " is held by a process that does not answer " & cfg.HealthPath &
                              " — refusing to spawn a backend that cannot bind")
                Return 1
        End Select

        ' belt for headless exits (supervisor kill without a console):
        ' stop the child on any appdomain teardown; the job object is the
        ' kernel-level guarantee beneath it
        AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender, e) GracefulStop()

        HostLog.Log("starting backend: " & nodeExe & " index.js (cwd " & baseDir & ")")
        Dim child As New BackendProcess()
        Try
            child.Start(nodeExe, entry, baseDir)
        Catch ex As Exception
            HostLog.Error("failed to start node backend: " & ex.Message)
            Return 4
        End Try
        _child = child
        HostLog.Log("backend pid " & child.Process.Id)

        ' Ctrl+C / console close (interactive path — node shares this console
        ' and its own SIGINT handler runs first during the grace window)
        AddHandler Console.CancelKeyPress,
            Sub(sender, e)
                e.Cancel = True
                StopEvent.Set()
            End Sub

        ' health proof: first 200 on /Backend/v.1.0/health inside the boot
        ' window; bail early if the child dies or a shutdown signal arrives
        Dim sw = Stopwatch.StartNew()
        Dim healthy As Boolean = False
        Dim proofOutcome As Integer = 0
        While True
            If child.HasExited Then
                proofOutcome = 4
                Exit While
            End If
            If PortProbe.IsHealthy(cfg.BindHost, cfg.Port, cfg.HealthPath) Then
                healthy = True
                Exit While
            End If
            If StopEvent.IsSet Then
                GracefulStop()
                HostLog.Log("NVIDIA Web Helper stopped cleanly (shutdown before health proof)")
                Return 0
            End If
            If sw.ElapsedMilliseconds > ProofTimeoutMs Then
                proofOutcome = 5
                Exit While
            End If
            Thread.Sleep(500)
        End While

        If healthy Then
            HostLog.Log("PROOF: backend healthy at " & cfg.BaseUrl & cfg.HealthPath &
                        " (node pid " & child.Process.Id &
                        ", boot " & (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", Globalization.CultureInfo.InvariantCulture) & "s)")
        ElseIf proofOutcome = 4 Then
            Dim code = child.SafeExitCode()
            HostLog.Error("backend exited before the health proof (exit code " & code & ") — check [NODE!] lines above and data\logs\backend.log")
            If code = 0 Then Return 4
            Return If(code > 0 AndAlso code <= 255, code, 4)
        Else
            HostLog.Warn("boot window elapsed without a health proof — child alive, continuing to supervise (cold hardware probe can be slow)")
        End If

        ' supervise: run until child exit or shutdown signal
        Dim exitTask = child.WaitForExitTask()
        Dim stopTask = Task.Run(Sub() StopEvent.Wait())
        Dim done = Task.WhenAny(exitTask, stopTask).GetAwaiter().GetResult()

        If done IsNot exitTask Then
            GracefulStop()
            HostLog.Log("NVIDIA Web Helper stopped cleanly")
            Return 0
        End If

        Dim exitCode = child.SafeExitCode()
        If exitCode = 0 Then
            HostLog.Log("backend exited (code 0)")
            Return 0
        End If
        HostLog.Error("backend exited unexpectedly (code " & exitCode & ") — check [NODE!] lines above and data\logs\backend.log")
        GracefulStop()
        Return If(exitCode > 0 AndAlso exitCode <= 255, exitCode, 1)
    End Function

    ' Idempotent stop — safe from both the Main flow and ProcessExit.
    Private Sub GracefulStop()
        SyncLock StopLock
            If _stopping Then Return
            _stopping = True
        End SyncLock
        Dim child = _child
        If child Is Nothing Then Return
        If child.HasExited Then Return

        HostLog.Log("shutdown signal — stopping backend (node pid " & child.Process.Id & ")")
        If child.TryStop(GraceMs, WaitAfterKillMs) Then
            HostLog.Log("backend stopped")
        Else
            HostLog.Error("backend did not stop within the kill window")
        End If
    End Sub

    ' Stable FNV-1a of the normalized base directory — mutex name can only
    ' contain word characters, so the dir is hashed, not embedded.
    Private Function DirHash(path As String) As String
        Dim hash As ULong = 2166136261UL
        For Each b As Byte In Encoding.UTF8.GetBytes(path.ToLowerInvariant())
            hash = ((hash Xor b) * 16777619UL) And &HFFFFFFFFUL
        Next
        Return hash.ToString("x8")
    End Function

    Private _mutex As Mutex

    ' =========== Console visibility ("showConsole" in config.json) ===========
    ' WinExe builds never create a console. "showConsole": true
    ' allocates one for debugging; file logs remain the evidence.

    <System.Runtime.InteropServices.DllImport("kernel32.dll")>
    Private Function AllocConsole() As Boolean
    End Function

    Private Sub ApplyConsoleVisibility(configPath As String)
        Try
            Dim show As Boolean = False
            If File.Exists(configPath) Then
                Using doc As System.Text.Json.JsonDocument =
                        System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath))
                    Dim el As System.Text.Json.JsonElement
                    If doc.RootElement.TryGetProperty("showConsole", el) Then
                        If el.ValueKind = System.Text.Json.JsonValueKind.True Then
                            show = True
                        ElseIf el.ValueKind = System.Text.Json.JsonValueKind.String Then
                            show = String.Equals(el.GetString(), "true", StringComparison.OrdinalIgnoreCase)
                        End If
                    End If
                End Using
            End If
            If show Then
                If AllocConsole() Then
                    Try
                        Console.SetOut(New IO.StreamWriter(Console.OpenStandardOutput()) With {.AutoFlush = True})
                        Console.SetError(New IO.StreamWriter(Console.OpenStandardError()) With {.AutoFlush = True})
                    Catch
                    End Try
                End If
            End If
        Catch
        End Try
    End Sub

End Module
