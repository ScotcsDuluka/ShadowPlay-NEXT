Option Strict On
Option Explicit On
Option Infer On

' HardwareGate.vb — F-01 environment preflight for CaptureEngine.Video.Tests
' (C/4 test-truthfulness pass).
'
' The DDAGRAB sections drive the REAL DdagrabBackend (DXGI adapter
' enumeration + Desktop Duplication) and cannot run on a machine without an
' NVIDIA adapter: 18 tests used to FAIL with
' "DdagrabBackend: no NVIDIA adapter found" on the Intel dev box, drowning
' real regressions.
'
' Contract:
'   - the probe Initialize→Dispose the REAL backend exactly once; its
'     exception message IS the adapter-enumeration evidence;
'   - on an NVIDIA machine every test RUNS unchanged;
'   - without an adapter, tests whose name starts with "DDAGRAB" report
'     SKIP (never PASS, never a swallowed failure) and never affect the
'     exit code.

Imports System
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video
Imports CaptureEngine.Video.Backends.Ddagrab
Imports CaptureEngine.Video.Backends.Fake

Namespace CaptureEngine.Video.Tests

    ''' <summary>Thrown by a test to report ENVIRONMENT NOT CAPABLE.</summary>
    Friend Class SkipException
        Inherits Exception

        Public Sub New(reason As String)
            MyBase.New(reason)
        End Sub
    End Class

    Friend Module HardwareGate

        Private _probed As Boolean = False
        Private _nvidiaAvailable As Boolean = False
        Private _reason As String = ""

        ''' <summary>One-time probe through the REAL production backend —
        ''' Initialize enumerates DXGI adapters and throws
        ''' VideoBackendRuntimeException when no VendorId=0x10DE adapter
        ''' exists.</summary>
        Friend Sub EnsureProbed()
            If _probed Then Return
            _probed = True
            Dim backend As DdagrabBackend = Nothing
            Try
                backend = New DdagrabBackend(New EngineLogger("nvidia-preflight", EngineLogger.LogLevel.Warning))
                backend.Initialize(New FakeVideoBackendContext(
                                       VideoBackendKind.Ddagrab,
                                       New EngineLogger("nvidia-preflight-ctx", EngineLogger.LogLevel.Warning)))
                _nvidiaAvailable = True
                _reason = ""
            Catch ex As Exception
                _nvidiaAvailable = False
                _reason = ex.Message
            Finally
                If backend IsNot Nothing Then
                    Try : backend.Dispose() : Catch : End Try
                End If
            End Try
        End Sub

        ''' <summary>True when the preflight proved the real NVIDIA capture
        ''' backend initializes on this machine.</summary>
        Friend ReadOnly Property NvidiaAvailable As Boolean
            Get
                EnsureProbed()
                Return _nvidiaAvailable
            End Get
        End Property

        ''' <summary>True when <paramref name="testName"/> needs the NVIDIA
        ''' capture backend and the probe proved this environment cannot run
        ''' it. The "DDAGRAB" prefix is the stable registration prefix of
        ''' DdagrabBackendLifecycleTests and DdagrabReplaceabilityTests.</summary>
        Friend Function ShouldSkip(testName As String) As Boolean
            EnsureProbed()
            If _nvidiaAvailable Then Return False
            Return testName.StartsWith("DDAGRAB", StringComparison.Ordinal)
        End Function

        Friend ReadOnly Property SkipReason As String
            Get
                Return _reason
            End Get
        End Property

    End Module

End Namespace
