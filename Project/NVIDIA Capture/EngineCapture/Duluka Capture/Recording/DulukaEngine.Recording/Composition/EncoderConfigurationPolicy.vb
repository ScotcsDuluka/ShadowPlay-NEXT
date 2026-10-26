Option Strict On
Imports DulukaEngine.Encoder
Imports DulukaEngine.Encoder.Qsv
Imports DulukaEngine.Video
Namespace DulukaEngine.Recording
    Public NotInheritable Class EncoderConfigurationPolicy
        Public Shared Sub Apply(config As EncoderConfig, provider As String, startup As EngineStartupConfig)
            If provider <> "intel:qsv-h264" Then
                If startup.Color IsNot Nothing Then Throw New NotSupportedException("color_descriptor_not_verified_for_provider")
                Return
            End If
            config.Color = startup.Color
            config.RateControl = If(startup.RateControl, "").Trim().ToLowerInvariant()
            config.Preset = startup.Preset
            config.ExpectedInputFormat = VideoPixelFormat.Nv12
            config.MinrateBps = 0 : config.BufsizeBps = 0
            config.Cq = startup.Cq
            If config.RateControl = "cqp" Then
                config.BitrateBps = 0 : config.MaxrateBps = 0
            Else
                config.MaxrateBps = config.BitrateBps * 2
            End If
            QsvEncoderBackend.ValidateConfiguration(config)
        End Sub
    End Class
End Namespace
