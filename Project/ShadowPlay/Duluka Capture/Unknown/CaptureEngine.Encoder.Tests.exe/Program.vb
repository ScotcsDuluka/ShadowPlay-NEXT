Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Encoder.Tests
    ''' <summary>
    ''' P1-F test runner. Executes every encoder contract test category.
    ''' No xUnit/NUnit — pure console runner (mirrors Foundation pattern).
    ''' </summary>
    Friend Module Program
        Private _passed As Integer = 0
        Private _failed As Integer = 0
        Private _skipped As Integer = 0
        Private ReadOnly _failures As New List(Of String)()
        Private ReadOnly _skips As New List(Of String)()

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine.Encoder Contract Tests (P1-F)")
            Console.WriteLine(" Branch: Engine-Rebuild-Stabilization")
            Console.WriteLine(" Foundation baseline: 82d792ab")
            Console.WriteLine(" Spec: P1-F v1.1")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            ' ----- Lifecycle tests -----
            Lifecycle.EncoderLifecycleTests.RunAll(AddressOf RunTest)

            ' ----- C/6 NVENC lifecycle matrix (hardware-independent) -----
            Lifecycle.NvencLifecycleTests.RunAll(AddressOf RunTest)
            Lifecycle.NvencNativeFaultTests.RunAll(AddressOf RunTest)   ' C/2: real-NVENC fault/dispose proof

            ' ----- Encode tests -----
            Encode.EncodeTests.RunAll(AddressOf RunTest)

            ' ----- Packet tests -----
            Contract.EncodedPacketTests.RunAll(AddressOf RunTest)

            ' ----- Concurrency tests -----
            Concurrency.EncoderConcurrencyTests.RunAll(AddressOf RunTest)

            ' ----- PHASE 1 VIDEO RUNTIME WIRING (V-CT5) -----
            NvEncParamBuilderTests.RunAll(AddressOf RunTest)

            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine(" Result: " & _passed & " passed, " & _failed & " failed, " &
                              _skipped & " skipped, " & (_passed + _failed + _skipped) & " total")
            Console.WriteLine("--------------------------------------------------")
            If _failed > 0 Then
                Console.WriteLine()
                Console.WriteLine("Failures:")
                For Each f As String In _failures
                    Console.WriteLine("  " & f)
                Next
            End If
            If _skipped > 0 Then
                Console.WriteLine()
                Console.WriteLine("Skips (environment not capable):")
                For Each f In _skips
                    Console.WriteLine("  " & f)
                Next
            End If
            Return If(_failed > 0, 1, 0)
        End Function

        Public Sub RunTest(name As String, test As Action)
            Console.Write("  " & name.PadRight(72))
            Try
                test()
                _passed += 1
                Console.WriteLine("PASS")
            Catch ex As SkipException
                _skipped += 1
                Dim msg As String = ex.Message
                If msg.Length > 100 Then msg = msg.Substring(0, 100) & "..."
                Console.WriteLine("SKIP")
                Console.WriteLine("    " & msg)
                _skips.Add(name & " — " & ex.Message)
            Catch ex As Exception
                _failed += 1
                Dim msg As String = ex.Message
                If msg.Length > 100 Then msg = msg.Substring(0, 100) & "..."
                Console.WriteLine("FAIL: " & msg)
                _failures.Add(name & " — " & ex.Message)
            End Try
        End Sub
    End Module
End Namespace
