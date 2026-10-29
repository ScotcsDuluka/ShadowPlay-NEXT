Option Strict On
Option Explicit On
Option Infer On

' ExclusiveCapture.Tests — SPN-ADR §C/§D vertical-slice tests for the
' Exclusive Capture API block: policy resolution, the exclusive
' verification gate, the pure DisplayModeMonitor classifier, the DXGI
' bounded-poll monitor and the exclusive route truth.
'
' Synthetic CaptureTargets only. The single hardware case is a READ-ONLY
' inventory poll (no duplication, no pixels, no devices) and reports SKIP
' truthfully when no eligible output exists.

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab

Friend Module Program
    Private _passed As Integer
    Private _failed As Integer
    Private _skipped As Integer

    Function Main() As Integer
        Console.WriteLine("==================================================")
        Console.WriteLine(" Exclusive Capture API — vertical slice tests")
        Console.WriteLine("==================================================")

        ' ── Policy resolution ──────────────────────────────────────────
        RunTest("POLICY: empty request = no separate exclusive source",
                Sub()
                    Dim r = ExclusiveCapturePolicy.Resolve("")
                    Assert(r.Item1.Length = 0, "resolved api must be empty")
                    Assert(r.Item2 = "exclusive_not_requested", "reason")
                    Assert(ExclusiveCapturePolicy.IsNoRequest(r.Item1), "IsNoRequest")
                End Sub)
        RunTest("POLICY: auto resolves to the one verified native source",
                Sub()
                    Dim r = ExclusiveCapturePolicy.Resolve("auto")
                    Assert(r.Item1 = ExclusiveCapturePolicy.DxgiDesktopDuplication, "resolved api")
                    Assert(r.Item2 = "auto_resolved_desktop_duplication", "reason")
                End Sub)
        RunTest("POLICY: explicit Desktop Duplication (case-insensitive)",
                Sub()
                    Dim r = ExclusiveCapturePolicy.Resolve("DXGI_Desktop_Duplication")
                    Assert(r.Item1 = ExclusiveCapturePolicy.DxgiDesktopDuplication, "resolved api")
                    Assert(r.Item2 = "explicit_desktop_duplication_for_exclusive", "reason")
                End Sub)
        RunTest("POLICY: ddagrab alias accepted",
                Sub()
                    Dim r = ExclusiveCapturePolicy.Resolve("DDAGRAB")
                    Assert(r.Item1 = ExclusiveCapturePolicy.DxgiDesktopDuplication, "resolved api")
                End Sub)
        RunTest("POLICY: wgc_monitor fails closed (not implemented)",
                Sub()
                    Dim rejected As Boolean = False
                    Try
                        ExclusiveCapturePolicy.Resolve("wgc_monitor")
                    Catch ex As NotSupportedException
                        rejected = ex.Message = "exclusive_capture_api_not_implemented"
                    End Try
                    Assert(rejected, "must throw NotSupportedException with precise code")
                End Sub)
        RunTest("POLICY: game_hook_source fails closed",
                Sub()
                    Dim rejected As Boolean = False
                    Try
                        ExclusiveCapturePolicy.Resolve("game_hook_source")
                    Catch ex As NotSupportedException
                        rejected = ex.Message = "exclusive_capture_api_not_implemented"
                    End Try
                    Assert(rejected, "must throw NotSupportedException with precise code")
                End Sub)

        ' ── Verification gate ──────────────────────────────────────────
        RunTest("GATE: missing evidence is unverified",
                Sub()
                    Assert(ExclusiveVerificationGate.Evaluate(Nothing) = "exclusive_evidence_missing", "reason")
                End Sub)
        RunTest("GATE: target identity mismatch is unverified",
                Sub()
                    Dim e = GoodEvidence()
                    e.TargetIdentityMatched = False
                    Assert(ExclusiveVerificationGate.Evaluate(e) = "exclusive_target_identity_mismatch", "reason")
                End Sub)
        RunTest("GATE: terminal fault is unverified",
                Sub()
                    Dim e = GoodEvidence()
                    e.TerminalFault = True
                    Assert(ExclusiveVerificationGate.Evaluate(e) = "exclusive_terminal_fault", "reason")
                End Sub)
        RunTest("GATE: non-monotonic timestamps are unverified",
                Sub()
                    Dim e = GoodEvidence()
                    e.TimestampsMonotonic = False
                    Assert(ExclusiveVerificationGate.Evaluate(e) = "exclusive_timestamps_invalid", "reason")
                End Sub)
        RunTest("GATE: zero real frames is unverified (no optimistic success)",
                Sub()
                    Dim e = GoodEvidence()
                    e.RealFramesDelivered = 0
                    Assert(ExclusiveVerificationGate.Evaluate(e) = "exclusive_real_frames_insufficient", "reason")
                End Sub)
        RunTest("GATE: real timestamped frames on the pinned target verify",
                Sub()
                    Assert(ExclusiveVerificationGate.Evaluate(GoodEvidence()) = "", "verified evidence returns empty reason")
                End Sub)

        ' ── Pure mode classifier ───────────────────────────────────────
        Dim adapter As New AdapterIdentity(1UI, 2, &H10DEUI, &H1B06UI, False, "\\?\PCI#VEN_10DE&DEV_1B06#synthetic")
        Dim pinned = SyntheticTarget(adapter)
        RunTest("MONITOR: pinned target present → Stable",
                Sub()
                    Dim kind = DisplayModeMonitor.Classify(pinned, New List(Of CaptureTarget) From {SyntheticTarget(adapter)})
                    Assert(kind = DisplayModeEventKind.Stable, "kind")
                End Sub)
        RunTest("MONITOR: pinned target absent → TargetLost (never retarget)",
                Sub()
                    Dim other = SyntheticTarget(New AdapterIdentity(9UI, 8, &H10DEUI, &H1B06UI, False, "\\?\PCI#VEN_10DE&DEV_1B06#other"))
                    Dim kind = DisplayModeMonitor.Classify(pinned, New List(Of CaptureTarget) From {other})
                    Assert(kind = DisplayModeEventKind.TargetLost, "kind")
                End Sub)
        RunTest("MONITOR: empty output set → TargetLost",
                Sub()
                    Dim kind = DisplayModeMonitor.Classify(pinned, New List(Of CaptureTarget)())
                    Assert(kind = DisplayModeEventKind.TargetLost, "kind")
                End Sub)
        RunTest("MONITOR: Nothing output set → TargetLost",
                Sub()
                    Dim kind = DisplayModeMonitor.Classify(pinned, CType(Nothing, IReadOnlyList(Of CaptureTarget)))
                    Assert(kind = DisplayModeEventKind.TargetLost, "kind")
                End Sub)
        RunTest("MONITOR: Nothing pinned target rejected",
                Sub()
                    Dim rejected As Boolean = False
                    Try
                        DisplayModeMonitor.Classify(Nothing, New List(Of CaptureTarget)())
                    Catch ex As ArgumentNullException
                        rejected = True
                    End Try
                    Assert(rejected, "must reject a Nothing pin")
                End Sub)

        ' ── Exclusive route truth ──────────────────────────────────────
        RunTest("ROUTE: Duluka exclusive wgc_monitor rejected with precise code",
                Sub()
                    Dim d = CaptureBackendRoute.Decide("Duluka", "dxgi_desktop_duplication", "wgc_monitor", "NVENC_H264")
                    Assert(Not d.Allowed, "must not be allowed")
                    Assert(d.ErrorCode = "unsupported_duluka_exclusive_api", "error code")
                End Sub)
        RunTest("ROUTE: Duluka exclusive auto / dxgi / ddagrab / empty allowed",
                Sub()
                    For Each exclusive In New String() {Nothing, "", "auto", "dxgi_desktop_duplication", "ddagrab"}
                        Dim d = CaptureBackendRoute.Decide("Duluka", "dxgi_desktop_duplication", exclusive, "NVENC_H264")
                        Assert(d.Allowed, $"exclusive='{exclusive}' must be allowed")
                        Assert(d.CaptureApi = "dxgi_desktop_duplication", "desktop api unchanged")
                    Next
                End Sub)
        RunTest("ROUTE: FFmpeg engine owns its own exclusive axis (base decision unchanged)",
                Sub()
                    Dim d = CaptureBackendRoute.Decide("FFmpeg", "gdigrab", "wgc_monitor", "libx264")
                    Assert(d.Allowed, "ffmpeg base decision untouched")
                End Sub)
        RunTest("ROUTE: legacy 3-arg Decide unchanged",
                Sub()
                    Dim d = CaptureBackendRoute.Decide("Duluka", "dxgi_desktop_duplication", "NVENC_H264")
                    Assert(d.Allowed AndAlso d.Kind = "Duluka", "base behavior")
                End Sub)

        ' ── DXGI bounded-poll monitor (injected snapshots) ─────────────
        RunTest("DXGI MONITOR: stable → target-lost transition raises exactly one event",
                Sub()
                    Dim outputs As New List(Of CaptureTarget) From {SyntheticTarget(adapter)}
                    Dim snapshot As New DxgiDisplayInventory.Snapshot(
                        New List(Of DxgiDisplayInventory.AdapterEntry) From {New DxgiDisplayInventory.AdapterEntry(adapter, 0, "synthetic")},
                        outputs)
                    Dim source = snapshot
                    Dim monitor As New DxgiDisplayModeMonitor(pinned, pollIntervalMs:=250, inventory:=Function() source)
                    Dim events As New List(Of DisplayModeEventKind)
                    Dim done As New ManualResetEventSlim(False)
                    AddHandler monitor.ModeChanged,
                        Sub(kind As DisplayModeEventKind, reason As String)
                            SyncLock events
                                events.Add(kind)
                            End SyncLock
                            done.Set()
                        End Sub
                    monitor.PollOnce()
                    Assert(monitor.CurrentState = DisplayModeEventKind.Stable, "first poll stable")
                    Assert(monitor.PollCount = 1, "one poll counted")
                    source = New DxgiDisplayInventory.Snapshot(
                        New List(Of DxgiDisplayInventory.AdapterEntry)(),
                        New List(Of CaptureTarget)())
                    monitor.PollOnce()
                    Assert(monitor.CurrentState = DisplayModeEventKind.TargetLost, "second poll lost")
                    Assert(done.Wait(TimeSpan.FromSeconds(2)), "event raised")
                    SyncLock events
                        Assert(events.Count = 1 AndAlso events(0) = DisplayModeEventKind.TargetLost, "exactly one TargetLost event")
                    End SyncLock
                    monitor.Dispose()
                End Sub)
        RunTest("DXGI MONITOR: transient inventory errors hold state; budget exhausted → TargetLost",
                Sub()
                    Dim failures As Integer = 0
                    Dim monitor As New DxgiDisplayModeMonitor(pinned, pollIntervalMs:=250, maxConsecutiveErrors:=3,
                                                              inventory:=Function()
                                                                             failures += 1
                                                                             Throw New InvalidOperationException("inventory down")
                                                                         End Function)
                    Dim events As Integer = 0
                    AddHandler monitor.ModeChanged, Sub(kind As DisplayModeEventKind, reason As String) events += 1
                    monitor.PollOnce()
                    monitor.PollOnce()
                    Assert(monitor.CurrentState = DisplayModeEventKind.Stable, "state held inside the budget")
                    monitor.PollOnce()
                    Assert(monitor.CurrentState = DisplayModeEventKind.TargetLost, "budget exhausted escalates")
                    Assert(failures = 3, "three polls attempted")
                    monitor.Dispose()
                End Sub)

        ' ── Real inventory poll (read-only, skip-safe) ─────────────────
        RunTest("DXGI MONITOR: real inventory poll stays read-only",
                Sub()
                    Dim snapshot = DxgiDisplayInventory.Collect()
                    Dim eligible = snapshot.Outputs.Where(Function(t) t.Eligible).ToList()
                    If eligible.Count = 0 Then
                        Throw New SkipException("no eligible output on this machine")
                    End If
                    Dim monitor As New DxgiDisplayModeMonitor(eligible(0))
                    monitor.PollOnce()
                    Assert(monitor.CurrentState = DisplayModeEventKind.Stable, $"pinned output {eligible(0).DeviceName} must classify stable")
                    Assert(monitor.LastReason = "pinned_target_present", "reason")
                    monitor.Dispose()
                    Console.WriteLine("  (read-only: no duplication, no pixels, no device init)")
                End Sub)

        Console.WriteLine()
        Console.WriteLine("--------------------------------------------------")
        Console.WriteLine($" Result: {_passed} passed, {_failed} failed, {_skipped} skipped, {_passed + _failed + _skipped} total")
        Console.WriteLine("--------------------------------------------------")
        Return If(_failed = 0, 0, 1)
    End Function

    Private Function GoodEvidence() As ExclusiveCaptureEvidence
        Return New ExclusiveCaptureEvidence With {
            .TargetIdentityMatched = True,
            .RealFramesDelivered = 30,
            .TimestampsMonotonic = True,
            .TerminalFault = False}
    End Function

    Private Function SyntheticTarget(adapter As AdapterIdentity) As CaptureTarget
        Return New CaptureTarget(adapter, 0, "DISPLAY-TEST", New IntPtr(12345), 1920, 1080, 0, 0, 0, True, 1UI, "topo-1", "MONITOR\TEST\{e1}")
    End Function

    Private Sub RunTest(name As String, test As Action)
        Console.Write($"  {name,-72} ")
        Try
            test()
            Console.WriteLine("PASS")
            _passed += 1
        Catch ex As SkipException
            Console.WriteLine("SKIP")
            Console.WriteLine($"    skip: {ex.Message}")
            _skipped += 1
        Catch ex As Exception
            Console.WriteLine("FAIL")
            Console.WriteLine($"    {ex.Message}")
            _failed += 1
        End Try
    End Sub

    Private Sub Assert(condition As Boolean, what As String)
        If Not condition Then Throw New Exception("FAIL " & what)
    End Sub

    Friend Class SkipException
        Inherits Exception
        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class
End Module
