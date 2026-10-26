Option Strict On
Imports System.Linq
Imports DulukaEngine.Diagnostics
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab
Imports DulukaEngine.Encoder
Imports DulukaEngine.Encoder.Nvenc

Namespace DulukaEngine.Recording
    ' Composition adapter: the NVIDIA implementation and its native ABI are untouched.
    Friend NotInheritable Class NvencRuntimeAdapter
        Implements IEncoderRuntimeBackend
        Private ReadOnly _backend As NvencEncoderBackend
        Private _actualAdapter As AdapterIdentity
        Public Sub New(logger As EngineLogger)
            _backend = New NvencEncoderBackend(logger)
        End Sub
        Public Sub Initialize(config As EncoderConfig) Implements IEncoderBackend.Initialize
            _backend.Initialize(config)
            ' NVENC reports its actual D3D11 parent LUID, verified by its own device factory.
            Dim matches = DxgiDisplayInventory.Collect().Adapters.Where(Function(a) a.Identity.HasLuid AndAlso
                a.Identity.Low = _backend.AdapterLuidLow AndAlso a.Identity.High = _backend.AdapterLuidHigh).ToArray()
            If matches.Length <> 1 OrElse Not matches(0).Identity.IsPhysical Then
                Throw New InvalidOperationException("encoder_adapter_identity_unavailable")
            End If
            _actualAdapter = matches(0).Identity
        End Sub
        Public ReadOnly Property ActualAdapter As AdapterIdentity Implements IEncoderRuntimeBackend.ActualAdapter
            Get
                Return _actualAdapter
            End Get
        End Property
        Public ReadOnly Property FrameRateFps As Integer Implements IEncoderRuntimeBackend.FrameRateFps
            Get
                Return _backend.FrameRateFps
            End Get
        End Property
        Public ReadOnly Property BitrateBpsOutput As Long Implements IEncoderRuntimeBackend.BitrateBpsOutput
            Get
                Return _backend.BitrateBpsOutput
            End Get
        End Property
        Public ReadOnly Property EncodeWidthOutput As Integer Implements IEncoderRuntimeBackend.EncodeWidthOutput
            Get
                Return _backend.EncodeWidthOutput
            End Get
        End Property
        Public ReadOnly Property EncodeHeightOutput As Integer Implements IEncoderRuntimeBackend.EncodeHeightOutput
            Get
                Return _backend.EncodeHeightOutput
            End Get
        End Property
        Public Sub Start() Implements IEncoderBackend.Start
            _backend.Start()
        End Sub
        Public Function Encode(frame As IVideoFrame, ByRef packet As EncodedPacket) As Boolean Implements IEncoderBackend.Encode
            Return _backend.Encode(frame, packet)
        End Function
        Public Function Flush(sink As Action(Of EncodedPacket)) As Integer Implements IEncoderBackend.Flush
            Return _backend.Flush(sink)
        End Function
        Public Sub [Stop]() Implements IEncoderBackend.Stop
            _backend.Stop()
        End Sub
        Public ReadOnly Property CurrentState As EncoderState Implements IEncoderBackend.CurrentState
            Get
                Return _backend.CurrentState
            End Get
        End Property
        Public ReadOnly Property Diagnostics As IEncoderDiagnostics Implements IEncoderBackend.Diagnostics
            Get
                Return _backend.Diagnostics
            End Get
        End Property
        Public Sub Dispose() Implements IDisposable.Dispose
            _backend.Dispose()
        End Sub
    End Class
End Namespace
