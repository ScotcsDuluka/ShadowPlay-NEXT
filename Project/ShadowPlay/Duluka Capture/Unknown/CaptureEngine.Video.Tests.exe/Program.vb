Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports CaptureEngine.Video.Tests.Delivery
Imports CaptureEngine.Video.Tests.FrameContract
Imports CaptureEngine.Video.Tests.Lifecycle
Imports CaptureEngine.Video.Tests.Replaceability

Namespace CaptureEngine.Video.Tests
    ''' <summary>
    ''' P1-B.1 test runner. Executes every contract test category from
    ''' P1-A v1.3.1 §8.2 against the FakeVideoCaptureBackend and the real
    ''' BoundedVideoFrameSink. No xUnit/NUnit — pure console runner.
    ''' </summary>
    Friend Module Program
        Private _passed As Integer = 0
        Private _failed As Integer = 0
        Private _skipped As Integer = 0
        Private ReadOnly _failures As New List(Of String)()
        Private ReadOnly _skips As New List(Of String)()

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine.Video Contract Tests (P1-B.1)")
            Console.WriteLine(" Branch: Engine-Rebuild")
            Console.WriteLine(" Foundation baseline: 82d792ab")
            Console.WriteLine(" Spec: P1-A v1.3.1 (APPROVED / FROZEN)")
            Console.WriteLine("==================================================")
            Console.WriteLine()
            Console.WriteLine()

            ' ─── F-01 preflight: probe the REAL DdagrabBackend once (adapter
            ' enumeration). Evidence printed; DDAGRAB tests SKIP when the
            ' environment has no NVIDIA adapter, RUN unchanged when it does.
            HardwareGate.EnsureProbed()
            Console.WriteLine(" NVIDIA backend probe = " &
                              If(HardwareGate.NvidiaAvailable,
                                 "AVAILABLE (DDAGRAB tests will run)",
                                 "NOT AVAILABLE (DDAGRAB tests will SKIP): " & HardwareGate.SkipReason))
            Console.WriteLine()

            ' ----- Lifecycle tests (§4.3, 11 cases) -----
            BackendLifecycleTests.RunAll(AddressOf RunTest)

            ' ----- P1-B.1 FIX regression tests -----
            DeadlockRegressionTests.RunAll(AddressOf RunTest)

            ' ----- GLM-1: DdagrabBackend skeleton tests -----
            DdagrabBackendLifecycleTests.RunAll(AddressOf RunTest)

            ' ----- C-1/C-2: Ddagrab ownership & concurrency (real DXGI) -----
            DdagrabOwnershipTests.RunAll(AddressOf RunTest)

            ' ----- Frame contract tests (FrameAvailable / NoFrame / Error / BGRA8 / diagnostics) -----
            FrameAvailabilityTests.RunAll(AddressOf RunTest)

            ' ----- Ownership / lifetime tests -----
            FrameOwnershipTests.RunAll(AddressOf RunTest)

            ' ----- Bounded handoff / backpressure tests -----
            BoundedHandoffTests.RunAll(AddressOf RunTest)

            ' ----- Replaceability tests -----
            ReplaceabilityTests.RunAll(AddressOf RunTest)

            ' ----- GLM-1: DdagrabBackend replaceability tests -----
            DdagrabReplaceabilityTests.RunAll(AddressOf RunTest)

            Console.WriteLine()
            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine(" Result: " & _passed & " passed, " & _failed & " failed, " &
                              _skipped & " skipped, " & (_passed + _failed + _skipped) & " total")
            Console.WriteLine("--------------------------------------------------")
            If _skipped > 0 Then
                Console.WriteLine()
                Console.WriteLine("Skips (ENVIRONMENT NOT CAPABLE — F-01):")
                For Each s As String In _skips
                    Console.WriteLine("  - " & s)
                Next
            End If
            If _failed > 0 Then
                Console.WriteLine()
                Console.WriteLine("Failures:")
                For Each f As String In _failures
                    Console.WriteLine("  - " & f)
                Next
            End If
            Return If(_failed > 0, 1, 0)
        End Function

        Private Sub RunTest(name As String, test As Action)
            ' F-01 gate: DDAGRAB tests drive the real NVIDIA capture backend —
            ' when the preflight proved this environment cannot run them,
            ' report SKIP (never PASS, never a swallowed FAIL).
            If HardwareGate.ShouldSkip(name) Then
                Dim gateReason As String = "NVIDIA Ddagrab backend unavailable: " & HardwareGate.SkipReason
                _skips.Add(name & " -> " & gateReason)
                _skipped += 1
                Console.Write("[" & name & "] ")
                Console.WriteLine("SKIP")
                Console.WriteLine("    " & gateReason)
                Return
            End If

            ' Pad name for alignment.
            Dim paddedName = name
            If paddedName.Length < 70 Then
                paddedName = paddedName & New String(" "c, 70 - paddedName.Length)
            End If

            Console.Write("[" & paddedName & "] ")
            Try
                test()
                _passed += 1
                Console.WriteLine("PASS")
            Catch ex As SkipException
                _skips.Add(name & " -> " & ex.Message)
                _skipped += 1
                Console.WriteLine("SKIP")
                Console.WriteLine("    " & ex.Message)
            Catch ex As Exception
                _failed += 1
                Dim innerMsg As String = ex.Message
                If ex.InnerException IsNot Nothing Then
                    innerMsg &= " :: " & ex.InnerException.GetType().Name & ": " & ex.InnerException.Message
                End If
                _failures.Add(name & " -> " & ex.GetType().Name & ": " & innerMsg)
                Console.WriteLine("FAIL")
                Console.WriteLine("    " & ex.GetType().Name & ": " & ex.Message)
                If ex.InnerException IsNot Nothing Then
                    Console.WriteLine("    Inner: " & ex.InnerException.GetType().Name & ": " & ex.InnerException.Message)
                End If
            End Try
        End Sub
    End Module
End Namespace
