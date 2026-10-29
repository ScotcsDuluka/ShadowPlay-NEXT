Option Strict On
Imports System.Linq
Imports DulukaEngine.Diagnostics
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab
Imports DulukaEngine.Encoder

Namespace DulukaEngine.Recording
    Public Interface IRecordingBackendFactory
        Function Plan(startup As EngineStartupConfig) As CaptureCompositionPlan
        Function CreateCapture(plan As CaptureCompositionPlan, cursor As Boolean, logger As EngineLogger) As IVideoCaptureRuntime
        Function CreateEncoder(plan As CaptureCompositionPlan, logger As EngineLogger) As IEncoderRuntimeBackend
        Sub ValidateTarget(plan As CaptureCompositionPlan)
    End Interface

    Public NotInheritable Class EncoderProvider
        Public ReadOnly Property Id As String
        Public ReadOnly Property Vendor As GpuVendor
        Public ReadOnly Property CodecKeys As IReadOnlyList(Of String)
        Public ReadOnly Property Implemented As Boolean
        Public ReadOnly Property RequiresFirstAdapter As Boolean
        Public Sub New(id As String, vendor As GpuVendor, implemented As Boolean, requiresFirstAdapter As Boolean, ParamArray codecs As String())
            Me.Id = id : Me.Vendor = vendor : Me.Implemented = implemented : Me.CodecKeys = Array.AsReadOnly(CType(codecs.Clone(), String()))
            Me.RequiresFirstAdapter = requiresFirstAdapter
        End Sub
    End Class

    Public NotInheritable Class CaptureCompositionPlan
        Public ReadOnly Property Target As CaptureTarget
        Public ReadOnly Property Provider As EncoderProvider
        ' ── Exclusive Capture API (SPN-ADR §C/§D): independent selection. ──
        ' "" = no explicit request (desktop route authoritative). A resolved
        ' value names the verified native source the exclusive route uses;
        ' the selection is evidence until ExclusiveVerificationGate passes.
        Public ReadOnly Property ExclusiveApi As String
        Public ReadOnly Property ExclusiveApiReason As String
        Public ReadOnly Property RuntimeWired As Boolean
            Get
                Return Provider.Implemented
            End Get
        End Property
        Public ReadOnly Property Reason As String
            Get
                Return If(RuntimeWired, "hardware_initialization_required", "intel_native_live_input_not_verified")
            End Get
        End Property
        Public Sub New(target As CaptureTarget, provider As EncoderProvider,
                       Optional exclusiveApi As String = "", Optional exclusiveApiReason As String = "")
            Me.Target = target : Me.Provider = provider
            Me.ExclusiveApi = exclusiveApi : Me.ExclusiveApiReason = exclusiveApiReason
        End Sub
        Public Sub RequireWired()
            If Not RuntimeWired Then Throw New NotSupportedException(Reason)
        End Sub
    End Class

    Public NotInheritable Class RecordingBackendFactory
        Implements IRecordingBackendFactory
        Private ReadOnly _inventory As Func(Of DxgiDisplayInventory.Snapshot)
        Public Shared ReadOnly Providers As IReadOnlyList(Of EncoderProvider) = Array.AsReadOnly({
            New EncoderProvider("nvidia:nvenc-h264", GpuVendor.Nvidia, True, True, "NVENC_H264", "h264_nvenc"),
            New EncoderProvider("intel:qsv-h264", GpuVendor.Intel, False, False, "QUICKSYNC_H264", "h264_qsv")})
        Public Sub New(Optional inventory As Func(Of DxgiDisplayInventory.Snapshot) = Nothing)
            _inventory = If(inventory, AddressOf DxgiDisplayInventory.Collect)
        End Sub
        Public Function Plan(startup As EngineStartupConfig) As CaptureCompositionPlan Implements IRecordingBackendFactory.Plan
            If startup Is Nothing Then Throw New ArgumentNullException(NameOf(startup))
            Dim method = If(startup.RequestedCaptureMethod, "").Trim().ToLowerInvariant()
            If method <> "" AndAlso method <> "ddagrab" AndAlso method <> "dxgi_desktop_duplication" Then
                Throw New NotSupportedException("unsupported_native_capture_api")
            End If
            Dim codec = If(startup.CodecKey, "").Trim()
            Dim provider = Providers.SingleOrDefault(Function(p) p.CodecKeys.Contains(codec, StringComparer.OrdinalIgnoreCase))
            If provider Is Nothing AndAlso Not String.Equals(codec, "auto", StringComparison.OrdinalIgnoreCase) Then
                Throw New NotSupportedException("unsupported_native_encoder")
            End If
            Dim snapshot = _inventory()
            Dim target As CaptureTarget
            If startup.CaptureTarget IsNot Nothing Then
                target = CaptureTarget.RequireCurrent(startup.CaptureTarget, snapshot.Outputs)
                If startup.CaptureOutputIndex >= 0 AndAlso startup.CaptureOutputIndex <> target.OutputIndex Then
                    Throw New InvalidOperationException("capture_output_index_conflict")
                End If
            ElseIf provider IsNot Nothing Then
                ' Preserve legacy first-provider-adapter behavior. Do not skip a headless
                ' first adapter and accidentally capture on a different GPU.
                Dim adapter = snapshot.Adapters.FirstOrDefault(Function(a) a.Identity.IsPhysical AndAlso
                    a.Identity.HasLuid AndAlso a.Identity.Vendor = provider.Vendor)
                If adapter Is Nothing Then Throw New InvalidOperationException("capture_adapter_unavailable")
                target = snapshot.Outputs.FirstOrDefault(Function(t) t.Eligible AndAlso t.Adapter.SameDevice(adapter.Identity) AndAlso
                    (startup.CaptureOutputIndex < 0 OrElse t.OutputIndex = startup.CaptureOutputIndex))
                If target Is Nothing Then Throw New InvalidOperationException("capture_display_unavailable")
            Else
                Dim eligible = snapshot.Outputs.Where(Function(t) t.Eligible).ToArray()
                target = eligible.SingleOrDefault(Function(t) t.Left = 0 AndAlso t.Top = 0)
                If target Is Nothing Then Throw New InvalidOperationException("capture_target_ambiguous")
            End If
            If provider Is Nothing Then provider = Providers.SingleOrDefault(Function(p) p.Vendor = target.Adapter.Vendor)
            If provider Is Nothing OrElse provider.Vendor <> target.Adapter.Vendor Then
                Throw New InvalidOperationException("encoder_capture_vendor_mismatch")
            End If
            ' ── Exclusive Capture API resolution (independent from desktop). ──
            ' Fail-closed: only the implemented verified native source is
            ' accepted; anything else throws with a precise reason — never a
            ' silent substitution. The DISPLAY boundary never changes here.
            Dim exclusive = ExclusiveCapturePolicy.Resolve(startup.RequestedExclusiveCaptureMethod)
            Return New CaptureCompositionPlan(target, provider, exclusive.Item1, exclusive.Item2)
        End Function
        Public Sub ValidateTarget(plan As CaptureCompositionPlan) Implements IRecordingBackendFactory.ValidateTarget
            If plan Is Nothing Then Throw New InvalidOperationException("capture_plan_unavailable")
            Dim snapshot = _inventory()
            CaptureTarget.RequireCurrent(plan.Target, snapshot.Outputs)
            If plan.Provider.RequiresFirstAdapter Then
                Dim first = snapshot.Adapters.FirstOrDefault(Function(a) a.Identity.Vendor = plan.Provider.Vendor)
                If first Is Nothing OrElse Not first.Identity.SameDevice(plan.Target.Adapter) Then
                    Throw New NotSupportedException("encoder_explicit_adapter_selection_not_implemented")
                End If
            End If
        End Sub
        Public Function CreateCapture(plan As CaptureCompositionPlan, cursor As Boolean, logger As EngineLogger) As IVideoCaptureRuntime Implements IRecordingBackendFactory.CreateCapture
            plan.RequireWired()
            ValidateTarget(plan)
            Return New DdagrabBackend(logger) With {.RequestedTarget = plan.Target, .CaptureCursor = cursor}
        End Function
        Public Function CreateEncoder(plan As CaptureCompositionPlan, logger As EngineLogger) As IEncoderRuntimeBackend Implements IRecordingBackendFactory.CreateEncoder
            plan.RequireWired()
            ValidateTarget(plan)
            Select Case plan.Provider.Id
                Case "nvidia:nvenc-h264"
                    Return New NvencRuntimeAdapter(logger)
                Case Else
                    Throw New NotSupportedException("native_encoder_factory_unavailable")
            End Select
        End Function
        Public Function CreateEncoderForInput(plan As CaptureCompositionPlan, format As VideoPixelFormat,
                                              logger As EngineLogger) As IEncoderRuntimeBackend
            ValidateTarget(plan)
            If plan.Provider.Id = "intel:qsv-h264" AndAlso format = VideoPixelFormat.Nv12 Then
                Return New DulukaEngine.Encoder.Qsv.QsvEncoderBackend(plan.Target.Adapter)
            End If
            If plan.Provider.Id = "nvidia:nvenc-h264" AndAlso format = VideoPixelFormat.Bgra8 Then Return CreateEncoder(plan, logger)
            Throw New NotSupportedException("native_encoder_input_transport_not_implemented")
        End Function
        Public Shared Sub RequireIdentity(plan As CaptureCompositionPlan, capture As CaptureTarget, encoder As AdapterIdentity)
            If capture Is Nothing OrElse Not plan.Target.SameOutput(capture) OrElse Not capture.Eligible Then
                Throw New InvalidOperationException("capture_target_runtime_mismatch")
            End If
            If encoder Is Nothing OrElse Not encoder.IsPhysical OrElse Not capture.Adapter.SameDevice(encoder) Then
                Throw New InvalidOperationException("encoder_capture_adapter_mismatch")
            End If
        End Sub
    End Class
End Namespace
