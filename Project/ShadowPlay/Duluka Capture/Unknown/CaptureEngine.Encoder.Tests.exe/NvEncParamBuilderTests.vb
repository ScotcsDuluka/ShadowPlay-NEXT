Option Strict On
Option Explicit On
Option Infer On

' NvEncParamBuilderTests.vb — PHASE 1 VIDEO RUNTIME WIRING, V-CT5.
'
' Proves the user's video config is written into REAL NVENC init
' structures — the pre-wiring encoder sent presetGUID=DEFAULT,
' encodeConfig=IntPtr.Zero and frameRateNum=60 no matter what the
' config carried (bitrate/RC/preset/GOP/fps were aspirational).
'
' HARDWARE-FREE SEAM/CONTRACT TEST (runs on Linux):
'   NvEncParamBuilder is the exact production code path that fills the
'   native structures inside NvencEncoderBackend.Initialize — built pure
'   precisely so this contract is executable without a GPU. The native
'   side (NvEncInitializeEncoder accepting the struct, ffprobe of the
'   produced file) remains WINDOWS-ONLY VALIDATION on the owner's machine.
'
' Layout evidence: nvEncodeAPI.h SDK 13.0 (nv-codec-headers n13.0.19.0),
' mechanically derived sizes (scripts/sizeof_nvenc.py) — pinned here so a
' transcription error breaks CI instead of the driver silently reading
' garbage.

Imports System
Imports System.Runtime.InteropServices
Imports CaptureEngine.Encoder
Imports CaptureEngine.Encoder.Nvenc.Internal

Friend Class NvEncParamBuilderTests

    Public Shared Sub RunAll(runTest As Action(Of String, Action))
        runTest("V-CT5a struct sizes match the SDK 13.0 header (evidence-pinned)",
                AddressOf StructSizes_MatchSdkHeader)
        runTest("V-CT5b init params carry config fps + preset GUID (never DEFAULT/60)",
                AddressOf InitializeParams_CarryConfigValues)
        runTest("V-CT5c NV_ENC_CONFIG patched: CBR + bitrate + GOP + no B-frames",
                AddressOf EncodeConfig_CarriesRateControlBitrateGop)
        runTest("V-CT5d bitrate 10,200,000 bps lands in rcParams.averageBitRate",
                AddressOf EncodeConfig_BitrateExact)
        runTest("V-CT5e h264 idrPeriod patched in the codec union",
                AddressOf EncodeConfig_IdrPeriod_Patched)
        runTest("V-CT5f preset mapper: unknown key → p4 (never DEFAULT)",
                AddressOf PresetMapper_UnknownKey_FallsToP4)
        runTest("V-CT5g preset GUID literals match SDK 13.0 header strings (P1..P7)",
                AddressOf PresetGuids_MatchSdkLiterals)
    End Sub

    Private Shared Sub StructSizes_MatchSdkHeader()
        Assert(Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_RC_PARAMS)() = NvEncParamBuilder.SIZEOF_NV_ENC_RC_PARAMS,
               $"Marshal.SizeOf(NV_ENC_RC_PARAMS)={Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_RC_PARAMS)()} expected {NvEncParamBuilder.SIZEOF_NV_ENC_RC_PARAMS}")
        Assert(Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_CONFIG)() = NvEncParamBuilder.SIZEOF_NV_ENC_CONFIG,
               $"Marshal.SizeOf(NV_ENC_CONFIG)={Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_CONFIG)()} expected {NvEncParamBuilder.SIZEOF_NV_ENC_CONFIG}")
        Assert(Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_PRESET_CONFIG)() = NvEncParamBuilder.SIZEOF_NV_ENC_PRESET_CONFIG,
               $"Marshal.SizeOf(NV_ENC_PRESET_CONFIG)={Marshal.SizeOf(Of NvEncodeAPI.NV_ENC_PRESET_CONFIG)()} expected {NvEncParamBuilder.SIZEOF_NV_ENC_PRESET_CONFIG}")

        ' Field offsets that ApplyVideoSettings/patches rely on:
        Assert(Marshal.OffsetOf(Of NvEncodeAPI.NV_ENC_CONFIG)("gopLength").ToInt64() = NvEncParamBuilder.NV_ENC_CONFIG_OFF_GOPLENGTH,
               "NV_ENC_CONFIG.gopLength offset drifted from the header layout")
        Assert(Marshal.OffsetOf(Of NvEncodeAPI.NV_ENC_CONFIG)("frameIntervalP").ToInt64() = NvEncParamBuilder.NV_ENC_CONFIG_OFF_FRAMEINTERVALP,
               "NV_ENC_CONFIG.frameIntervalP offset drifted")
        Assert(Marshal.OffsetOf(Of NvEncodeAPI.NV_ENC_CONFIG)("encodeCodecConfig").ToInt64() = NvEncParamBuilder.NV_ENC_CONFIG_OFF_CODECCONFIG,
               "NV_ENC_CONFIG.encodeCodecConfig offset drifted")
        Assert(Marshal.OffsetOf(Of NvEncodeAPI.NV_ENC_RC_PARAMS)("averageBitRate").ToInt64() = NvEncParamBuilder.NV_ENC_RC_PARAMS_OFF_AVERAGEBITRATE,
               "NV_ENC_RC_PARAMS.averageBitRate offset drifted")
    End Sub

    Private Shared Sub InitializeParams_CarryConfigValues()
        Dim p As NvEncodeAPI.NV_ENC_INITIALIZE_PARAMS =
            NvEncParamBuilder.BuildInitializeParams(1920UI, 1080UI, 1280UI, 720UI, 75, "p4")

        Assert(p.frameRateNum = 75UI,
               $"frameRateNum expected 75 (config fps), got {p.frameRateNum} — pre-wiring code hardcoded 60")
        Assert(p.frameRateDen = 1UI, $"frameRateDen expected 1, got {p.frameRateDen}")
        Assert(p.presetGUID = NvEncodeAPI.NV_ENC_PRESET_P4_GUID,
               $"presetGUID expected NV_ENC_PRESET_P4_GUID, got {p.presetGUID} — pre-wiring code always sent NV_ENC_PRESET_DEFAULT_GUID")
        Assert(p.encodeConfig = IntPtr.Zero,
               "encodeConfig must start Zero (caller attaches the native NV_ENC_CONFIG)")
        Assert(p.encodeWidth = 1280UI AndAlso p.encodeHeight = 720UI,
               $"encodeWidth/Height expected 1280/720 (V-CT2 encode dims), got {p.encodeWidth}/{p.encodeHeight}")
        ' Validated native path leaves optional maxEncode bounds unset (0/0);
        ' the actual encode dimensions are carried by encodeWidth/encodeHeight.
        Assert(p.maxEncodeWidth = 0UI AndAlso p.maxEncodeHeight = 0UI,
               $"maxEncode expected native default 0/0, got {p.maxEncodeWidth}/{p.maxEncodeHeight}")
        Assert(p.encodeGUID = NvEncodeAPI.NV_ENC_CODEC_H264_GUID, "encodeGUID must be H264")
        Assert(p.enableEncodeAsync = 0UI, "synchronous mode contract (enableEncodeAsync=0)")
    End Sub

    Private Shared Sub EncodeConfig_CarriesRateControlBitrateGop()
        Dim cfg As NvEncodeAPI.NV_ENC_CONFIG =
            NvEncParamBuilder.BuildDefaultEncodeConfig(20_000_000L, 20_000_000L, 40_000_000L, "cbr", 60)

        Assert(cfg.version = NvEncodeAPI.NV_ENC_CONFIG_VER,
               $"NV_ENC_CONFIG.version expected NV_ENC_CONFIG_VER 0x{NvEncodeAPI.NV_ENC_CONFIG_VER:X8}, got 0x{cfg.version:X8}")
        Assert(cfg.profileGUID = NvEncodeAPI.NV_ENC_CODEC_PROFILE_AUTOSELECT_GUID,
               "profileGUID expected AUTOSELECT")
        Assert(cfg.gopLength = 60UI, $"gopLength expected 60 (config GOP), got {cfg.gopLength}")
        Assert(cfg.frameIntervalP = 1, $"frameIntervalP expected 1 (IPP — no B-frames, encode order = display order), got {cfg.frameIntervalP}")
        Assert(cfg.rcParams.rateControlMode = NvEncodeAPI.NV_ENC_PARAMS_RC_CBR,
               $"rateControlMode expected CBR(2), got {cfg.rcParams.rateControlMode} — pre-wiring code never sent any RC mode")
        Assert(cfg.rcParams.averageBitRate = 20000000UI,
               $"rcParams.averageBitRate expected 20,000,000, got {cfg.rcParams.averageBitRate}")
        Assert(cfg.rcParams.maxBitRate = 20000000UI, "CBR maxBitRate must equal the average bitrate")
        Assert(cfg.rcParams.vbvBufferSize = 40000000UI, "vbvBufferSize expected bufsize (40,000,000)")
        Assert(cfg.rcParams.version = NvEncodeAPI.NV_ENC_RC_PARAMS_VER, "rcParams.version expected NV_ENC_RC_PARAMS_VER")

        ' VBR branch: maxBitRate ≥ averageBitRate
        Dim vbr As NvEncodeAPI.NV_ENC_CONFIG =
            NvEncParamBuilder.BuildDefaultEncodeConfig(10_000_000L, 12_000_000L, 20_000_000L, "vbr", 60)
        Assert(vbr.rcParams.rateControlMode = NvEncodeAPI.NV_ENC_PARAMS_RC_VBR, "vbr key → NV_ENC_PARAMS_RC_VBR")
        Assert(vbr.rcParams.maxBitRate = 12000000UI, "VBR maxBitRate expected the configured maxrate")
    End Sub

    Private Shared Sub EncodeConfig_BitrateExact()
        ' V-CT3/V-CT5 cross-check: the owner's exact acceptance number.
        Dim cfg As NvEncodeAPI.NV_ENC_CONFIG =
            NvEncParamBuilder.BuildDefaultEncodeConfig(10_200_000L, 10_200_000L, 20_400_000L, "cbr", 60)
        Assert(cfg.rcParams.averageBitRate = 10200000UI,
               $"rcParams.averageBitRate expected 10,200,000 bps (config 10200 kbps), got {cfg.rcParams.averageBitRate}")
    End Sub

    Private Shared Sub EncodeConfig_IdrPeriod_Patched()
        Dim cfg As NvEncodeAPI.NV_ENC_CONFIG =
            NvEncParamBuilder.BuildDefaultEncodeConfig(20_000_000L, 20_000_000L, 40_000_000L, "cbr", 75)
        Dim idr As UInteger = NvEncParamBuilder.ReadCodecConfigU32(
            cfg, NvEncParamBuilder.NV_ENC_CONFIG_H264_OFF_IDRPERIOD)
        Assert(idr = 75UI,
               $"h264 idrPeriod expected 75 (GOP wiring into the codec union), got {idr}")
    End Sub

    Private Shared Sub PresetMapper_UnknownKey_FallsToP4()
        Assert(NvEncodeAPI.PresetGuidForKey("p7") = NvEncodeAPI.NV_ENC_PRESET_P7_GUID, "p7 → P7 GUID")
        Assert(NvEncodeAPI.PresetGuidForKey("P3") = NvEncodeAPI.NV_ENC_PRESET_P3_GUID, "case-insensitive p3")
        Assert(NvEncodeAPI.PresetGuidForKey("") = NvEncodeAPI.NV_ENC_PRESET_P4_GUID, "empty → p4")
        Assert(NvEncodeAPI.PresetGuidForKey("garbage") = NvEncodeAPI.NV_ENC_PRESET_P4_GUID,
               "unknown key → p4 — must NEVER fall back to NV_ENC_PRESET_DEFAULT_GUID")
        Assert(NvEncodeAPI.PresetGuidForKey("garbage") <> NvEncodeAPI.NV_ENC_PRESET_DEFAULT_GUID,
               "unknown key must not map to the DEFAULT preset")
    End Sub

    ' GLM/2 cross-review BLOCKER regression: NV_ENC_PRESET_P1_GUID was
    ' transcribed with byte 0x0E swapped to 0xE0 — header "...590EBF"
    ' became "...59E0BF". A managed-symbol ↔ managed-symbol comparison
    ' cannot catch that (both sides carry the same transcription error),
    ' so this test pins the managed fields to the VERBATIM string literals
    ' from nvEncodeAPI.h SDK 13.0 (nv-codec-headers n13.0.19.0, lines
    ' 225-252). Any byte-level drift in any preset GUID fails here.
    Private Shared Sub PresetGuids_MatchSdkLiterals()
        Dim sdkLiterals As String() = {
            "{FC0A8D3E-45F8-4CF8-80C7-298871590EBF}", ' P1 — nvEncodeAPI.h:225
            "{F581CFB8-88D6-4381-93F0-DF13F9C27DAB}", ' P2 — nvEncodeAPI.h:229
            "{36850110-3A07-441F-94D5-3670631F91F6}", ' P3 — nvEncodeAPI.h:233
            "{90A7B826-DF06-4862-B9D2-CD6D73A08681}", ' P4 — nvEncodeAPI.h:237
            "{21C6E6B4-297A-4CBA-998F-B6CBDE72ADE3}", ' P5 — nvEncodeAPI.h:241
            "{8E75C279-6299-4AB6-8302-0B215A335CF5}", ' P6 — nvEncodeAPI.h:245
            "{84848C12-6F71-4C13-931B-53E283F57974}"  ' P7 — nvEncodeAPI.h:249
        }
        Dim managed As Guid() = {
            NvEncodeAPI.NV_ENC_PRESET_P1_GUID, NvEncodeAPI.NV_ENC_PRESET_P2_GUID,
            NvEncodeAPI.NV_ENC_PRESET_P3_GUID, NvEncodeAPI.NV_ENC_PRESET_P4_GUID,
            NvEncodeAPI.NV_ENC_PRESET_P5_GUID, NvEncodeAPI.NV_ENC_PRESET_P6_GUID,
            NvEncodeAPI.NV_ENC_PRESET_P7_GUID
        }
        For i As Integer = 0 To sdkLiterals.Length - 1
            Dim actual As String = managed(i).ToString("B").ToUpperInvariant()
            Assert(actual = sdkLiterals(i),
                   $"preset P{i + 1} GUID literal drift: expected {sdkLiterals(i)} (SDK 13.0 header), got {actual}")
        Next
    End Sub

    Private Shared Sub Assert(cond As Boolean, message As String)
        If Not cond Then Throw New Exception(message)
    End Sub

End Class
