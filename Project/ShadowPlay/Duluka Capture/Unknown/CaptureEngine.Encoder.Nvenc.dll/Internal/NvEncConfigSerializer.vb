Option Explicit On
Option Strict On
Option Infer On

Imports System

Namespace CaptureEngine.Encoder.Nvenc.Internal

    Friend Module NvEncConfigSerializer

        Public Function Serialize(cfg As NvEncodeAPI.NV_ENC_CONFIG) As Byte()
            Dim b(3583) As Byte

            W32(b, 0, cfg.version)
            WGuid(b, 4, cfg.profileGUID)
            W32(b, 20, cfg.gopLength)
            W32(b, 24, CUInt(cfg.frameIntervalP))
            W32(b, 28, cfg.monoChromeEncoding)
            W32(b, 32, cfg.frameFieldMode)
            W32(b, 36, cfg.mvPrecision)

            SerializeRc(b, 40, cfg.rcParams)

            ' The installed nvEncodeAPI64.dll has accepted the following
            ' empirical H.264 codec layout in prior real-hardware validation.
            ' Keep this byte-level layout independent from managed union
            ' marshalling; the SDK-11-compatible H.264 VUI placement is part
            ' of the validated ABI for this specific DLL.
            Dim h264Flags As UInteger = If(cfg.rcParams.rateControlMode = NvEncodeAPI.NV_ENC_PARAMS_RC_CBR, &H20000UI, 0UI)
            W32(b, 168, h264Flags)  ' H.264 enableFillerDataInsertion (bit 17)

            Dim gop As UInteger = cfg.gopLength
            W32(b, 172, 0UI)    ' level = AUTOSELECT
            W32(b, 176, gop)    ' idrPeriod
            W32(b, 180, 0UI)    ' separateColourPlaneFlag
            W32(b, 184, 0UI)    ' disableDeblockingFilterIDC
            W32(b, 188, 1UI)    ' numTemporalLayers
            W32(b, 192, 0UI)    ' spsId
            W32(b, 196, 0UI)    ' ppsId
            W32(b, 200, 0UI)    ' adaptiveTransformMode
            W32(b, 204, 0UI)    ' fmoMode
            W32(b, 208, 0UI)    ' bdirectMode
            W32(b, 212, 0UI)    ' entropyCodingMode
            W32(b, 216, 0UI)    ' stereoMode
            W32(b, 220, 0UI)    ' intraRefreshPeriod
            W32(b, 224, 0UI)    ' intraRefreshCnt
            W32(b, 228, 0UI)    ' maxNumRefFrames (driver default)
            W32(b, 232, 0UI)    ' sliceMode
            W32(b, 236, 0UI)    ' sliceModeData
            W32(b, 352, 0UI)    ' ltrNumFrames
            W32(b, 356, 0UI)    ' ltrTrustMode
            W32(b, 360, 1UI)    ' chromaFormatIDC
            W32(b, 364, 0UI)    ' maxTemporalLayers
            W32(b, 380, 8UI)    ' outputBitDepth
            W32(b, 384, 8UI)    ' inputBitDepth

            If cfg.reserved IsNot Nothing Then
                For i As Integer = 0 To Math.Min(cfg.reserved.Length, 278) - 1
                    W32(b, 1960 + i * 4, cfg.reserved(i))
                Next
            End If

            If cfg.reserved2 IsNot Nothing Then
                For i As Integer = 0 To Math.Min(cfg.reserved2.Length, 64) - 1
                    W64(b, 3072 + i * 8, CULng(cfg.reserved2(i).ToInt64()))
                Next
            End If

            Return b
        End Function

        Private Sub SerializeRc(b As Byte(), o As Integer, rc As NvEncodeAPI.NV_ENC_RC_PARAMS)
            W32(b, o + 0, rc.version)
            W32(b, o + 4, rc.rateControlMode)
            W32(b, o + 8, rc.qpInterP)
            W32(b, o + 12, rc.qpInterB)
            W32(b, o + 16, rc.qpIntra)
            W32(b, o + 20, rc.averageBitRate)
            W32(b, o + 24, rc.maxBitRate)
            W32(b, o + 28, rc.vbvBufferSize)
            W32(b, o + 32, rc.vbvInitialDelay)
            W32(b, o + 36, rc.bitFields)
            W32(b, o + 40, rc.minQpInterP)
            W32(b, o + 44, rc.minQpInterB)
            W32(b, o + 48, rc.minQpIntra)
            W32(b, o + 52, rc.maxQpInterP)
            W32(b, o + 56, rc.maxQpInterB)
            W32(b, o + 60, rc.maxQpIntra)
            W32(b, o + 64, rc.initQpInterP)
            W32(b, o + 68, rc.initQpInterB)
            W32(b, o + 72, rc.initQpIntra)
            W32(b, o + 76, rc.temporallayerIdxMask)
            If rc.temporalLayerQP IsNot Nothing Then
                Buffer.BlockCopy(rc.temporalLayerQP, 0, b, o + 80,
                                 Math.Min(rc.temporalLayerQP.Length, 8))
            End If

            b(o + 88) = rc.targetQuality
            b(o + 89) = rc.targetQualityLSB
            W16(b, o + 90, rc.lookaheadDepth)
            b(o + 92) = rc.lowDelayKeyFrameScale
            b(o + 93) = CByte(rc.yDcQPIndexOffset)
            b(o + 94) = CByte(rc.uDcQPIndexOffset)
            b(o + 95) = CByte(rc.vDcQPIndexOffset)
            W32(b, o + 96, rc.qpMapMode)
            W32(b, o + 100, rc.multiPass)
            W32(b, o + 104, rc.alphaLayerBitrateRatio)
            b(o + 108) = CByte(rc.cbQPIndexOffset)
            b(o + 109) = CByte(rc.crQPIndexOffset)
            W16(b, o + 110, rc.reserved2)
            W32(b, o + 112, rc.lookaheadLevel)

            If rc.viewBitrateRatios IsNot Nothing Then
                Buffer.BlockCopy(rc.viewBitrateRatios, 0, b, o + 116,
                                 Math.Min(rc.viewBitrateRatios.Length, 7))
            End If
            b(o + 123) = rc.reserved3
            W32(b, o + 124, rc.reserved1)
        End Sub

        Private Sub W16(b As Byte(), o As Integer, v As UShort)
            b(o) = CByte(v And &HFFUS)
            b(o + 1) = CByte((v >> 8) And &HFFUS)
        End Sub

        Private Sub W32(b As Byte(), o As Integer, v As UInteger)
            b(o) = CByte(v And &HFFUI)
            b(o + 1) = CByte((v >> 8) And &HFFUI)
            b(o + 2) = CByte((v >> 16) And &HFFUI)
            b(o + 3) = CByte((v >> 24) And &HFFUI)
        End Sub

        Private Sub W64(b As Byte(), o As Integer, v As ULong)
            W32(b, o, CUInt(v And &HFFFFFFFFUL))
            W32(b, o + 4, CUInt((v >> 32) And &HFFFFFFFFUL))
        End Sub

        Private Sub WGuid(b As Byte(), o As Integer, v As Guid)
            Buffer.BlockCopy(v.ToByteArray(), 0, b, o, 16)
        End Sub

    End Module

End Namespace
