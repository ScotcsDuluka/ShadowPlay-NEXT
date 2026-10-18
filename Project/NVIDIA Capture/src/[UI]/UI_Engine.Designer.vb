' UI_Engine.Designer.vb
' ShadowPlay Engine - Settings Form Layout
' Dark NVIDIA-style theme

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class UI_Engine
    Inherits System.Windows.Forms.Form

    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    Private components As System.ComponentModel.IContainer

    ' ── Color Scheme ──

    Private _bgDark As Drawing.Color = Drawing.Color.FromArgb(32, 32, 36)
    Private _bgPanel As Drawing.Color = Drawing.Color.FromArgb(44, 44, 48)
    Private _bgInput As Drawing.Color = Drawing.Color.FromArgb(55, 55, 60)
    Private _bgButton As Drawing.Color = Drawing.Color.FromArgb(118, 185, 0)
    Private _bgButtonStop As Drawing.Color = Drawing.Color.FromArgb(200, 50, 50)
    Private _fgText As Drawing.Color = Drawing.Color.FromArgb(230, 230, 230)
    Private _fgDim As Drawing.Color = Drawing.Color.FromArgb(160, 160, 160)
    Private _accentGreen As Drawing.Color = Drawing.Color.FromArgb(118, 185, 0)
    Private _accentRed As Drawing.Color = Drawing.Color.FromArgb(200, 50, 50)

    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New ComponentModel.Container()
        lblTitle = New Label()
        lblStatus = New Label()
        lblTimer = New Label()
        pnlCapture = New Panel()
        lblCapTitle = New Label()
        cboCaptureMethod = New ComboBox()
        pnlEncoder = New Panel()
        lblEncTitle = New Label()
        cboEncoder = New ComboBox()
        btnDetect = New Button()
        pnlRes = New Panel()
        chkNativeRes = New CheckBox()
        cboResolution = New ComboBox()
        pnlPerf = New Panel()
        lblFPSTitle = New Label()
        nudFPS = New NumericUpDown()
        lblBitTitle = New Label()
        nudBitrate = New NumericUpDown()
        lblBitrateHint = New Label()
        pnlPreset = New Panel()
        lblPresetTitle = New Label()
        lblPresetValue = New Label()
        lblNvencPreset = New Label()
        lblReplayTitle = New Label()
        nudReplayDuration = New NumericUpDown()
        pnlAudio = New Panel()
        lblAudioTitle = New Label()
        chkSysAudio = New CheckBox()
        chkMic = New CheckBox()
        lblSysVol = New Label()
        trkSysVol = New TrackBar()
        lblMicVol = New Label()
        trkMicVol = New TrackBar()
        lblMicDevice = New Label()
        btnOpenAudioSettings = New Button()
        pnlOutput = New Panel()
        lblOutTitle = New Label()
        txtOutputDir = New TextBox()
        btnBrowse = New Button()
        lblFfmpegTitle = New Label()
        txtFFmpegPath = New TextBox()
        btnFFmpegBrowse = New Button()
        pnlGitHub = New Panel()
        lblGitHubTitle = New Label()
        lblGitHubUser = New Label()
        lblGitHubStatus = New Label()
        pnlHub = New Panel()
        lblHubTitle = New Label()
        lblHubStatus = New Label()
        lblHubClients = New Label()
        lblConfigSource = New Label()
        btnRecord = New Button()
        btnStop = New Button()
        tmrRecording = New System.Windows.Forms.Timer(components)
        tmrRefresh = New System.Windows.Forms.Timer(components)
        DIMBOX_2 = New PictureBox()
        BT_Back = New Label()
        settings_top = New PictureBox()
        settings_menu = New Panel()
        btnStressTest = New Button()
        PictureBox5 = New PictureBox()
        PictureBox4 = New PictureBox()
        PictureBox3 = New PictureBox()
        PictureBox16 = New PictureBox()
        hg2 = New PictureBox()
        pnlDiag = New Panel()
        lblDiagTitle = New Label()
        txtDiagnostics = New TextBox()
        lblMirrorNote = New Label()
        pnlStatus = New Panel()
        lblRecState = New Label()
        lblRecSize = New Label()
        lblRecFrames = New Label()
        lblRecBitrate = New Label()
        OPEN_UI = New System.Windows.Forms.Timer(components)
        pnlCapture.SuspendLayout()
        pnlEncoder.SuspendLayout()
        pnlRes.SuspendLayout()
        pnlPerf.SuspendLayout()
        CType(nudFPS, ComponentModel.ISupportInitialize).BeginInit()
        CType(nudBitrate, ComponentModel.ISupportInitialize).BeginInit()
        pnlPreset.SuspendLayout()
        CType(nudReplayDuration, ComponentModel.ISupportInitialize).BeginInit()
        pnlAudio.SuspendLayout()
        CType(trkSysVol, ComponentModel.ISupportInitialize).BeginInit()
        CType(trkMicVol, ComponentModel.ISupportInitialize).BeginInit()
        pnlOutput.SuspendLayout()
        pnlGitHub.SuspendLayout()
        pnlHub.SuspendLayout()
        CType(DIMBOX_2, ComponentModel.ISupportInitialize).BeginInit()
        CType(settings_top, ComponentModel.ISupportInitialize).BeginInit()
        settings_menu.SuspendLayout()
        CType(PictureBox5, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox4, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox3, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox16, ComponentModel.ISupportInitialize).BeginInit()
        CType(hg2, ComponentModel.ISupportInitialize).BeginInit()
        pnlDiag.SuspendLayout()
        pnlStatus.SuspendLayout()
        SuspendLayout()
        ' 
        ' lblTitle
        ' 
        lblTitle.AutoSize = True
        lblTitle.Font = New Font("GeForce", 24F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        lblTitle.Location = New Point(62, 43)
        lblTitle.Name = "lblTitle"
        lblTitle.Size = New Size(234, 42)
        lblTitle.TabIndex = 0
        lblTitle.Text = "nvsphelper64"
        ' 
        ' lblStatus
        ' 
        lblStatus.AutoSize = True
        lblStatus.Font = New Font("Segoe UI", 10F, FontStyle.Bold)
        lblStatus.Location = New Point(47, 95)
        lblStatus.Name = "lblStatus"
        lblStatus.Size = New Size(34, 19)
        lblStatus.TabIndex = 1
        lblStatus.Text = "Idle"
        ' 
        ' lblTimer
        ' 
        lblTimer.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        lblTimer.Font = New Font("GeForce", 20.25F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        lblTimer.Location = New Point(325, 15)
        lblTimer.Name = "lblTimer"
        lblTimer.Size = New Size(123, 36)
        lblTimer.TabIndex = 2
        lblTimer.Text = "00:00:00"
        ' 
        ' pnlCapture
        ' 
        pnlCapture.Controls.Add(lblCapTitle)
        pnlCapture.Controls.Add(cboCaptureMethod)
        pnlCapture.Location = New Point(47, 164)
        pnlCapture.Name = "pnlCapture"
        pnlCapture.Size = New Size(490, 70)
        pnlCapture.TabIndex = 3
        ' 
        ' lblCapTitle
        ' 
        lblCapTitle.AutoSize = True
        lblCapTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblCapTitle.Location = New Point(12, 8)
        lblCapTitle.Name = "lblCapTitle"
        lblCapTitle.Size = New Size(98, 15)
        lblCapTitle.TabIndex = 0
        lblCapTitle.Text = "Capture Method"
        ' 
        ' cboCaptureMethod
        ' 
        cboCaptureMethod.DropDownStyle = ComboBoxStyle.DropDownList
        cboCaptureMethod.Enabled = False
        cboCaptureMethod.Font = New Font("Segoe UI", 9F)
        cboCaptureMethod.Items.AddRange(New Object() {"ddagrab - Desktop Duplication (DXGI)", "gdigrab - GDI Screen Capture", "gfxcapture - DXGI Desktop Dup (NV)"})
        cboCaptureMethod.Location = New Point(12, 30)
        cboCaptureMethod.Name = "cboCaptureMethod"
        cboCaptureMethod.Size = New Size(466, 23)
        cboCaptureMethod.TabIndex = 1
        ' 
        ' pnlEncoder
        ' 
        pnlEncoder.Controls.Add(lblEncTitle)
        pnlEncoder.Controls.Add(cboEncoder)
        pnlEncoder.Controls.Add(btnDetect)
        pnlEncoder.Location = New Point(47, 244)
        pnlEncoder.Name = "pnlEncoder"
        pnlEncoder.Size = New Size(490, 70)
        pnlEncoder.TabIndex = 4
        ' 
        ' lblEncTitle
        ' 
        lblEncTitle.AutoSize = True
        lblEncTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblEncTitle.Location = New Point(12, 8)
        lblEncTitle.Name = "lblEncTitle"
        lblEncTitle.Size = New Size(52, 15)
        lblEncTitle.TabIndex = 0
        lblEncTitle.Text = "Encoder"
        ' 
        ' cboEncoder
        ' 
        cboEncoder.DropDownStyle = ComboBoxStyle.DropDownList
        cboEncoder.Enabled = False
        cboEncoder.Font = New Font("Segoe UI", 9F)
        cboEncoder.Items.AddRange(New Object() {"Auto (Recommended)", "h264_nvenc - NVIDIA H.264", "hevc_nvenc - NVIDIA H.265", "h264_qsv - Intel QuickSync", "hevc_qsv - Intel QuickSync H.265", "h264_amf - AMD AMF H.264", "libx264 - CPU H.264", "libx265 - CPU H.265", "libsvtav1 - CPU SVT-AV1"})
        cboEncoder.Location = New Point(12, 30)
        cboEncoder.Name = "cboEncoder"
        cboEncoder.Size = New Size(325, 23)
        cboEncoder.TabIndex = 1
        ' 
        ' btnDetect
        ' 
        btnDetect.Cursor = Cursors.Hand
        btnDetect.Font = New Font("Segoe UI", 8F)
        btnDetect.Location = New Point(345, 30)
        btnDetect.Name = "btnDetect"
        btnDetect.Size = New Size(133, 23)
        btnDetect.TabIndex = 2
        btnDetect.Text = "Detect Encoders"
        ' 
        ' pnlRes
        ' 
        pnlRes.Controls.Add(chkNativeRes)
        pnlRes.Controls.Add(cboResolution)
        pnlRes.Location = New Point(47, 324)
        pnlRes.Name = "pnlRes"
        pnlRes.Size = New Size(490, 65)
        pnlRes.TabIndex = 5
        ' 
        ' chkNativeRes
        ' 
        chkNativeRes.AutoSize = True
        chkNativeRes.BackColor = Color.Transparent
        chkNativeRes.Checked = True
        chkNativeRes.CheckState = CheckState.Checked
        chkNativeRes.Enabled = False
        chkNativeRes.Font = New Font("Segoe UI", 9F)
        chkNativeRes.Location = New Point(12, 8)
        chkNativeRes.Name = "chkNativeRes"
        chkNativeRes.Size = New Size(141, 19)
        chkNativeRes.TabIndex = 0
        chkNativeRes.Text = "Use Native Resolution"
        chkNativeRes.UseVisualStyleBackColor = False
        ' 
        ' cboResolution
        ' 
        cboResolution.DropDownStyle = ComboBoxStyle.DropDownList
        cboResolution.Enabled = False
        cboResolution.Font = New Font("Segoe UI", 9F)
        cboResolution.Items.AddRange(New Object() {"1920x1080 (1080p)", "2560x1440 (1440p)", "3840x2160 (4K)", "1280x720 (720p)"})
        cboResolution.Location = New Point(12, 32)
        cboResolution.Name = "cboResolution"
        cboResolution.Size = New Size(466, 23)
        cboResolution.TabIndex = 1
        ' 
        ' pnlPerf
        ' 
        pnlPerf.Controls.Add(lblFPSTitle)
        pnlPerf.Controls.Add(nudFPS)
        pnlPerf.Controls.Add(lblBitTitle)
        pnlPerf.Controls.Add(nudBitrate)
        pnlPerf.Controls.Add(lblBitrateHint)
        pnlPerf.Location = New Point(47, 399)
        pnlPerf.Name = "pnlPerf"
        pnlPerf.Size = New Size(490, 70)
        pnlPerf.TabIndex = 6
        ' 
        ' lblFPSTitle
        ' 
        lblFPSTitle.AutoSize = True
        lblFPSTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblFPSTitle.Location = New Point(12, 8)
        lblFPSTitle.Name = "lblFPSTitle"
        lblFPSTitle.Size = New Size(27, 15)
        lblFPSTitle.TabIndex = 0
        lblFPSTitle.Text = "FPS"
        ' 
        ' nudFPS
        ' 
        nudFPS.BorderStyle = BorderStyle.FixedSingle
        nudFPS.Enabled = False
        nudFPS.Location = New Point(12, 30)
        nudFPS.Maximum = New Decimal(New Integer() {240, 0, 0, 0})
        nudFPS.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        nudFPS.Name = "nudFPS"
        nudFPS.Size = New Size(100, 23)
        nudFPS.TabIndex = 1
        nudFPS.Value = New Decimal(New Integer() {60, 0, 0, 0})
        ' 
        ' lblBitTitle
        ' 
        lblBitTitle.AutoSize = True
        lblBitTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblBitTitle.Location = New Point(150, 8)
        lblBitTitle.Name = "lblBitTitle"
        lblBitTitle.Size = New Size(87, 15)
        lblBitTitle.TabIndex = 2
        lblBitTitle.Text = "Bitrate (Mbps)"
        ' 
        ' nudBitrate
        ' 
        nudBitrate.BorderStyle = BorderStyle.FixedSingle
        nudBitrate.Enabled = False
        nudBitrate.Increment = New Decimal(New Integer() {5, 0, 0, 0})
        nudBitrate.Location = New Point(150, 30)
        nudBitrate.Maximum = New Decimal(New Integer() {200, 0, 0, 0})
        nudBitrate.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        nudBitrate.Name = "nudBitrate"
        nudBitrate.Size = New Size(120, 23)
        nudBitrate.TabIndex = 3
        nudBitrate.Value = New Decimal(New Integer() {50, 0, 0, 0})
        ' 
        ' lblBitrateHint
        ' 
        lblBitrateHint.AutoSize = True
        lblBitrateHint.Font = New Font("Segoe UI", 8F)
        lblBitrateHint.Location = New Point(278, 34)
        lblBitrateHint.Name = "lblBitrateHint"
        lblBitrateHint.Size = New Size(83, 13)
        lblBitrateHint.TabIndex = 4
        lblBitrateHint.Text = "(50 = 50 Mbps)"
        ' 
        ' pnlPreset
        ' 
        pnlPreset.Controls.Add(lblPresetTitle)
        pnlPreset.Controls.Add(lblPresetValue)
        pnlPreset.Controls.Add(lblNvencPreset)
        pnlPreset.Controls.Add(lblReplayTitle)
        pnlPreset.Controls.Add(nudReplayDuration)
        pnlPreset.Location = New Point(47, 479)
        pnlPreset.Name = "pnlPreset"
        pnlPreset.Size = New Size(490, 70)
        pnlPreset.TabIndex = 11
        ' 
        ' lblPresetTitle
        ' 
        lblPresetTitle.AutoSize = True
        lblPresetTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblPresetTitle.Location = New Point(12, 8)
        lblPresetTitle.Name = "lblPresetTitle"
        lblPresetTitle.Size = New Size(43, 15)
        lblPresetTitle.TabIndex = 0
        lblPresetTitle.Text = "Preset"
        ' 
        ' lblPresetValue
        ' 
        lblPresetValue.AutoSize = True
        lblPresetValue.Font = New Font("Segoe UI", 9F)
        lblPresetValue.Location = New Point(60, 8)
        lblPresetValue.Name = "lblPresetValue"
        lblPresetValue.Size = New Size(61, 15)
        lblPresetValue.TabIndex = 1
        lblPresetValue.Text = "Maximum"
        ' 
        ' lblNvencPreset
        ' 
        lblNvencPreset.AutoSize = True
        lblNvencPreset.Font = New Font("Segoe UI", 8F)
        lblNvencPreset.Location = New Point(200, 9)
        lblNvencPreset.Name = "lblNvencPreset"
        lblNvencPreset.Size = New Size(62, 13)
        lblNvencPreset.TabIndex = 2
        lblNvencPreset.Text = "NVENC: p7"
        ' 
        ' lblReplayTitle
        ' 
        lblReplayTitle.AutoSize = True
        lblReplayTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblReplayTitle.Location = New Point(12, 32)
        lblReplayTitle.Name = "lblReplayTitle"
        lblReplayTitle.Size = New Size(112, 15)
        lblReplayTitle.TabIndex = 3
        lblReplayTitle.Text = "Replay Duration (s)"
        ' 
        ' nudReplayDuration
        ' 
        nudReplayDuration.BorderStyle = BorderStyle.FixedSingle
        nudReplayDuration.Enabled = False
        nudReplayDuration.Location = New Point(140, 30)
        nudReplayDuration.Maximum = New Decimal(New Integer() {1200, 0, 0, 0})
        nudReplayDuration.Minimum = New Decimal(New Integer() {15, 0, 0, 0})
        nudReplayDuration.Name = "nudReplayDuration"
        nudReplayDuration.Size = New Size(75, 23)
        nudReplayDuration.TabIndex = 4
        nudReplayDuration.Value = New Decimal(New Integer() {60, 0, 0, 0})
        ' 
        ' pnlAudio
        ' 
        pnlAudio.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        pnlAudio.Controls.Add(lblAudioTitle)
        pnlAudio.Controls.Add(chkSysAudio)
        pnlAudio.Controls.Add(chkMic)
        pnlAudio.Controls.Add(lblSysVol)
        pnlAudio.Controls.Add(trkSysVol)
        pnlAudio.Controls.Add(lblMicVol)
        pnlAudio.Controls.Add(trkMicVol)
        pnlAudio.Controls.Add(lblMicDevice)
        pnlAudio.Controls.Add(btnOpenAudioSettings)
        pnlAudio.Location = New Point(1012, 164)
        pnlAudio.Name = "pnlAudio"
        pnlAudio.Size = New Size(736, 118)
        pnlAudio.TabIndex = 12
        ' 
        ' lblAudioTitle
        ' 
        lblAudioTitle.AutoSize = True
        lblAudioTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblAudioTitle.Location = New Point(12, 8)
        lblAudioTitle.Name = "lblAudioTitle"
        lblAudioTitle.Size = New Size(39, 15)
        lblAudioTitle.TabIndex = 0
        lblAudioTitle.Text = "Audio"
        ' 
        ' chkSysAudio
        ' 
        chkSysAudio.AutoSize = True
        chkSysAudio.Font = New Font("Segoe UI", 9F)
        chkSysAudio.Location = New Point(12, 32)
        chkSysAudio.Name = "chkSysAudio"
        chkSysAudio.Size = New Size(99, 19)
        chkSysAudio.TabIndex = 1
        chkSysAudio.Text = "System Audio"
        ' 
        ' chkMic
        ' 
        chkMic.AutoSize = True
        chkMic.Font = New Font("Segoe UI", 9F)
        chkMic.Location = New Point(140, 32)
        chkMic.Name = "chkMic"
        chkMic.Size = New Size(91, 19)
        chkMic.TabIndex = 2
        chkMic.Text = "Microphone"
        ' 
        ' lblSysVol
        ' 
        lblSysVol.AutoSize = True
        lblSysVol.Font = New Font("Segoe UI", 8F)
        lblSysVol.Location = New Point(12, 58)
        lblSysVol.Name = "lblSysVol"
        lblSysVol.Size = New Size(75, 13)
        lblSysVol.TabIndex = 3
        lblSysVol.Text = "Sys Vol: 100%"
        ' 
        ' trkSysVol
        ' 
        trkSysVol.Location = New Point(95, 55)
        trkSysVol.Maximum = 100
        trkSysVol.Name = "trkSysVol"
        trkSysVol.Size = New Size(120, 45)
        trkSysVol.TabIndex = 4
        trkSysVol.Value = 100
        ' 
        ' lblMicVol
        ' 
        lblMicVol.AutoSize = True
        lblMicVol.Font = New Font("Segoe UI", 8F)
        lblMicVol.Location = New Point(240, 58)
        lblMicVol.Name = "lblMicVol"
        lblMicVol.Size = New Size(77, 13)
        lblMicVol.TabIndex = 5
        lblMicVol.Text = "Mic Vol: 100%"
        ' 
        ' trkMicVol
        ' 
        trkMicVol.Location = New Point(325, 55)
        trkMicVol.Maximum = 100
        trkMicVol.Name = "trkMicVol"
        trkMicVol.Size = New Size(120, 45)
        trkMicVol.TabIndex = 6
        trkMicVol.Value = 100
        ' 
        ' lblMicDevice
        ' 
        lblMicDevice.AutoSize = True
        lblMicDevice.Font = New Font("Segoe UI", 8F)
        lblMicDevice.Location = New Point(12, 85)
        lblMicDevice.Name = "lblMicDevice"
        lblMicDevice.Size = New Size(74, 13)
        lblMicDevice.TabIndex = 7
        lblMicDevice.Text = "Mic: (default)"
        ' 
        ' btnOpenAudioSettings
        ' 
        btnOpenAudioSettings.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        btnOpenAudioSettings.Font = New Font("Segoe UI", 9F)
        btnOpenAudioSettings.Location = New Point(605, 84)
        btnOpenAudioSettings.Name = "btnOpenAudioSettings"
        btnOpenAudioSettings.Size = New Size(120, 22)
        btnOpenAudioSettings.TabIndex = 8
        btnOpenAudioSettings.Text = "Audio Settings..."
        ' 
        ' pnlOutput
        ' 
        pnlOutput.Controls.Add(lblOutTitle)
        pnlOutput.Controls.Add(txtOutputDir)
        pnlOutput.Controls.Add(btnBrowse)
        pnlOutput.Location = New Point(47, 611)
        pnlOutput.Name = "pnlOutput"
        pnlOutput.Size = New Size(490, 65)
        pnlOutput.TabIndex = 7
        ' 
        ' lblOutTitle
        ' 
        lblOutTitle.AutoSize = True
        lblOutTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblOutTitle.Location = New Point(12, 8)
        lblOutTitle.Name = "lblOutTitle"
        lblOutTitle.Size = New Size(85, 15)
        lblOutTitle.TabIndex = 0
        lblOutTitle.Text = "Output Folder"
        ' 
        ' txtOutputDir
        ' 
        txtOutputDir.BorderStyle = BorderStyle.FixedSingle
        txtOutputDir.Location = New Point(12, 30)
        txtOutputDir.Name = "txtOutputDir"
        txtOutputDir.ReadOnly = True
        txtOutputDir.Size = New Size(420, 23)
        txtOutputDir.TabIndex = 1
        ' 
        ' btnBrowse
        ' 
        btnBrowse.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        btnBrowse.Location = New Point(438, 30)
        btnBrowse.Name = "btnBrowse"
        btnBrowse.Size = New Size(40, 23)
        btnBrowse.TabIndex = 2
        btnBrowse.Text = "..."
        ' 
        ' lblFfmpegTitle
        ' 
        lblFfmpegTitle.AutoSize = True
        lblFfmpegTitle.Font = New Font("Segoe UI", 10F, FontStyle.Bold)
        lblFfmpegTitle.Location = New Point(15, 130)
        lblFfmpegTitle.Name = "lblFfmpegTitle"
        lblFfmpegTitle.Size = New Size(96, 19)
        lblFfmpegTitle.TabIndex = 0
        lblFfmpegTitle.Text = "FFmpeg Path"
        ' 
        ' txtFFmpegPath
        ' 
        txtFFmpegPath.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        txtFFmpegPath.BorderStyle = BorderStyle.FixedSingle
        txtFFmpegPath.Location = New Point(15, 156)
        txtFFmpegPath.Name = "txtFFmpegPath"
        txtFFmpegPath.ReadOnly = True
        txtFFmpegPath.Size = New Size(383, 23)
        txtFFmpegPath.TabIndex = 1
        ' 
        ' btnFFmpegBrowse
        ' 
        btnFFmpegBrowse.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        btnFFmpegBrowse.BackColor = Color.DimGray
        btnFFmpegBrowse.FlatStyle = FlatStyle.Flat
        btnFFmpegBrowse.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        btnFFmpegBrowse.Location = New Point(404, 156)
        btnFFmpegBrowse.Name = "btnFFmpegBrowse"
        btnFFmpegBrowse.Size = New Size(40, 23)
        btnFFmpegBrowse.TabIndex = 2
        btnFFmpegBrowse.Text = "..."
        btnFFmpegBrowse.UseVisualStyleBackColor = False
        ' 
        ' pnlGitHub
        ' 
        pnlGitHub.Controls.Add(lblGitHubTitle)
        pnlGitHub.Controls.Add(lblGitHubUser)
        pnlGitHub.Controls.Add(lblGitHubStatus)
        pnlGitHub.Location = New Point(47, 555)
        pnlGitHub.Name = "pnlGitHub"
        pnlGitHub.Size = New Size(490, 50)
        pnlGitHub.TabIndex = 13
        ' 
        ' lblGitHubTitle
        ' 
        lblGitHubTitle.AutoSize = True
        lblGitHubTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblGitHubTitle.Location = New Point(12, 8)
        lblGitHubTitle.Name = "lblGitHubTitle"
        lblGitHubTitle.Size = New Size(50, 15)
        lblGitHubTitle.TabIndex = 0
        lblGitHubTitle.Text = "GitHub:"
        ' 
        ' lblGitHubUser
        ' 
        lblGitHubUser.AutoSize = True
        lblGitHubUser.Font = New Font("Segoe UI", 9F)
        lblGitHubUser.Location = New Point(70, 8)
        lblGitHubUser.Name = "lblGitHubUser"
        lblGitHubUser.Size = New Size(72, 15)
        lblGitHubUser.TabIndex = 1
        lblGitHubUser.Text = "(not loaded)"
        ' 
        ' lblGitHubStatus
        ' 
        lblGitHubStatus.AutoSize = True
        lblGitHubStatus.Font = New Font("Segoe UI", 8F)
        lblGitHubStatus.Location = New Point(12, 28)
        lblGitHubStatus.Name = "lblGitHubStatus"
        lblGitHubStatus.Size = New Size(116, 13)
        lblGitHubStatus.TabIndex = 2
        lblGitHubStatus.Text = "Status: not logged in"
        ' 
        ' pnlHub
        ' 
        pnlHub.Controls.Add(lblHubTitle)
        pnlHub.Controls.Add(lblHubStatus)
        pnlHub.Controls.Add(lblHubClients)
        pnlHub.Location = New Point(543, 370)
        pnlHub.Name = "pnlHub"
        pnlHub.Size = New Size(463, 52)
        pnlHub.TabIndex = 14
        ' 
        ' lblHubTitle
        ' 
        lblHubTitle.AutoSize = True
        lblHubTitle.Font = New Font("Segoe UI", 9F, FontStyle.Bold)
        lblHubTitle.Location = New Point(12, 8)
        lblHubTitle.Name = "lblHubTitle"
        lblHubTitle.Size = New Size(33, 15)
        lblHubTitle.TabIndex = 0
        lblHubTitle.Text = "Hub:"
        ' 
        ' lblHubStatus
        ' 
        lblHubStatus.AutoSize = True
        lblHubStatus.Font = New Font("Segoe UI", 9F)
        lblHubStatus.Location = New Point(50, 8)
        lblHubStatus.Name = "lblHubStatus"
        lblHubStatus.Size = New Size(78, 15)
        lblHubStatus.TabIndex = 1
        lblHubStatus.Text = "disconnected"
        ' 
        ' lblHubClients
        ' 
        lblHubClients.AutoSize = True
        lblHubClients.Font = New Font("Segoe UI", 8F)
        lblHubClients.Location = New Point(12, 28)
        lblHubClients.Name = "lblHubClients"
        lblHubClients.Size = New Size(54, 13)
        lblHubClients.TabIndex = 2
        lblHubClients.Text = "Clients: 0"
        ' 
        ' lblConfigSource
        ' 
        lblConfigSource.AutoSize = True
        lblConfigSource.Font = New Font("Segoe UI", 7F)
        lblConfigSource.Location = New Point(47, 679)
        lblConfigSource.Name = "lblConfigSource"
        lblConfigSource.Size = New Size(93, 12)
        lblConfigSource.TabIndex = 15
        lblConfigSource.Text = "Config: (searching...)"
        ' 
        ' btnRecord
        ' 
        btnRecord.Cursor = Cursors.Hand
        btnRecord.Font = New Font("Segoe UI", 11F, FontStyle.Bold)
        btnRecord.ForeColor = Color.White
        btnRecord.Location = New Point(47, 117)
        btnRecord.Name = "btnRecord"
        btnRecord.Size = New Size(240, 32)
        btnRecord.TabIndex = 9
        btnRecord.Text = "Record"
        ' 
        ' btnStop
        ' 
        btnStop.Cursor = Cursors.Hand
        btnStop.Enabled = False
        btnStop.Font = New Font("Segoe UI", 11F, FontStyle.Bold)
        btnStop.ForeColor = Color.White
        btnStop.Location = New Point(297, 117)
        btnStop.Name = "btnStop"
        btnStop.Size = New Size(240, 32)
        btnStop.TabIndex = 10
        btnStop.Text = "Stop"
        ' 
        ' tmrRecording
        ' 
        tmrRecording.Interval = 1000
        ' 
        ' tmrRefresh
        ' 
        tmrRefresh.Interval = 2000
        ' 
        ' DIMBOX_2
        ' 
        DIMBOX_2.BackColor = Color.Blue
        DIMBOX_2.BackgroundImageLayout = ImageLayout.None
        DIMBOX_2.Location = New Point(0, 160)
        DIMBOX_2.Name = "DIMBOX_2"
        DIMBOX_2.Size = New Size(80, 80)
        DIMBOX_2.TabIndex = 93
        DIMBOX_2.TabStop = False
        DIMBOX_2.Visible = False
        ' 
        ' BT_Back
        ' 
        BT_Back.BackColor = Color.FromArgb(CByte(118), CByte(185), CByte(0))
        BT_Back.Cursor = Cursors.Hand
        BT_Back.Font = New Font("Segoe UI", 12F, FontStyle.Bold)
        BT_Back.ForeColor = Color.White
        BT_Back.Location = New Point(80, 110)
        BT_Back.Name = "BT_Back"
        BT_Back.Size = New Size(200, 50)
        BT_Back.TabIndex = 96
        BT_Back.Text = "Back"
        BT_Back.TextAlign = ContentAlignment.MiddleCenter
        ' 
        ' settings_top
        ' 
        settings_top.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        settings_top.BackColor = Color.FromArgb(CByte(118), CByte(185), CByte(0))
        settings_top.Location = New Point(80, 160)
        settings_top.Name = "settings_top"
        settings_top.Size = New Size(1760, 5)
        settings_top.TabIndex = 94
        settings_top.TabStop = False
        ' 
        ' settings_menu
        ' 
        settings_menu.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        settings_menu.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        settings_menu.Controls.Add(btnStressTest)
        settings_menu.Controls.Add(PictureBox5)
        settings_menu.Controls.Add(PictureBox4)
        settings_menu.Controls.Add(lblConfigSource)
        settings_menu.Controls.Add(lblMirrorNote)
        settings_menu.Controls.Add(PictureBox3)
        settings_menu.Controls.Add(PictureBox16)
        settings_menu.Controls.Add(lblStatus)
        settings_menu.Controls.Add(hg2)
        settings_menu.Controls.Add(pnlCapture)
        settings_menu.Controls.Add(pnlEncoder)
        settings_menu.Controls.Add(btnStop)
        settings_menu.Controls.Add(pnlRes)
        settings_menu.Controls.Add(btnRecord)
        settings_menu.Controls.Add(pnlPerf)
        settings_menu.Controls.Add(pnlPreset)
        settings_menu.Controls.Add(pnlHub)
        settings_menu.Controls.Add(pnlAudio)
        settings_menu.Controls.Add(pnlGitHub)
        settings_menu.Controls.Add(pnlOutput)
        settings_menu.Controls.Add(pnlDiag)
        settings_menu.Controls.Add(lblTitle)
        settings_menu.Controls.Add(pnlStatus)
        settings_menu.ForeColor = Color.White
        settings_menu.Location = New Point(80, 160)
        settings_menu.Name = "settings_menu"
        settings_menu.Size = New Size(1760, 840)
        settings_menu.TabIndex = 95
        ' 
        ' btnStressTest
        ' 
        btnStressTest.Location = New Point(543, 117)
        btnStressTest.Name = "btnStressTest"
        btnStressTest.Size = New Size(463, 32)
        btnStressTest.TabIndex = 92
        btnStressTest.Text = "Button1"
        btnStressTest.UseVisualStyleBackColor = True
        ' 
        ' PictureBox5
        ' 
        PictureBox5.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        PictureBox5.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        PictureBox5.Location = New Point(269, 1450)
        PictureBox5.Name = "PictureBox5"
        PictureBox5.Size = New Size(254, 116)
        PictureBox5.TabIndex = 91
        PictureBox5.TabStop = False
        PictureBox5.Visible = False
        ' 
        ' PictureBox4
        ' 
        PictureBox4.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        PictureBox4.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        PictureBox4.Location = New Point(12, 1450)
        PictureBox4.Name = "PictureBox4"
        PictureBox4.Size = New Size(256, 116)
        PictureBox4.TabIndex = 90
        PictureBox4.TabStop = False
        PictureBox4.Visible = False
        ' 
        ' PictureBox3
        ' 
        PictureBox3.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        PictureBox3.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        PictureBox3.Location = New Point(269, 1329)
        PictureBox3.Name = "PictureBox3"
        PictureBox3.Size = New Size(254, 120)
        PictureBox3.TabIndex = 89
        PictureBox3.TabStop = False
        PictureBox3.Visible = False
        ' 
        ' PictureBox16
        ' 
        PictureBox16.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        PictureBox16.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        PictureBox16.Location = New Point(12, 1329)
        PictureBox16.Name = "PictureBox16"
        PictureBox16.Size = New Size(256, 120)
        PictureBox16.TabIndex = 88
        PictureBox16.TabStop = False
        PictureBox16.Visible = False
        ' 
        ' hg2
        ' 
        hg2.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        hg2.BackColor = Color.DimGray
        hg2.Location = New Point(11, 1328)
        hg2.Name = "hg2"
        hg2.Size = New Size(513, 239)
        hg2.TabIndex = 87
        hg2.TabStop = False
        hg2.Visible = False
        ' 
        ' pnlDiag
        ' 
        pnlDiag.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        pnlDiag.BackColor = Color.FromArgb(CByte(20), CByte(20), CByte(24))
        pnlDiag.BorderStyle = BorderStyle.FixedSingle
        pnlDiag.Controls.Add(lblDiagTitle)
        pnlDiag.Controls.Add(txtDiagnostics)
        pnlDiag.Location = New Point(1012, 288)
        pnlDiag.Name = "pnlDiag"
        pnlDiag.Size = New Size(736, 537)
        pnlDiag.TabIndex = 91
        ' 
        ' lblDiagTitle
        ' 
        lblDiagTitle.AutoSize = True
        lblDiagTitle.ForeColor = Color.FromArgb(CByte(118), CByte(185), CByte(0))
        lblDiagTitle.Location = New Point(8, 6)
        lblDiagTitle.Name = "lblDiagTitle"
        lblDiagTitle.Size = New Size(357, 15)
        lblDiagTitle.TabIndex = 0
        lblDiagTitle.Text = "EFFECTIVE RUNTIME (read-only) — requested → effective → actual"
        ' 
        ' txtDiagnostics
        ' 
        txtDiagnostics.BackColor = Color.FromArgb(CByte(24), CByte(24), CByte(28))
        txtDiagnostics.BorderStyle = BorderStyle.None
        txtDiagnostics.Font = New Font("Consolas", 8.25F)
        txtDiagnostics.ForeColor = Color.FromArgb(CByte(200), CByte(200), CByte(200))
        txtDiagnostics.Location = New Point(8, 26)
        txtDiagnostics.Multiline = True
        txtDiagnostics.Name = "txtDiagnostics"
        txtDiagnostics.ReadOnly = True
        txtDiagnostics.ScrollBars = ScrollBars.Vertical
        txtDiagnostics.Size = New Size(440, 452)
        txtDiagnostics.TabIndex = 1
        txtDiagnostics.Text = "(initializing...)"
        ' 
        ' lblMirrorNote
        ' 
        lblMirrorNote.AutoSize = True
        lblMirrorNote.ForeColor = Color.FromArgb(CByte(160), CByte(160), CByte(160))
        lblMirrorNote.Location = New Point(47, 694)
        lblMirrorNote.Name = "lblMirrorNote"
        lblMirrorNote.Size = New Size(376, 15)
        lblMirrorNote.TabIndex = 92
        lblMirrorNote.Text = "Mirrors are read-only — user settings: Overlay → Settings (config.json)"
        ' 
        ' pnlStatus
        ' 
        pnlStatus.BackColor = Color.FromArgb(CByte(20), CByte(20), CByte(24))
        pnlStatus.BorderStyle = BorderStyle.FixedSingle
        pnlStatus.Controls.Add(lblFfmpegTitle)
        pnlStatus.Controls.Add(txtFFmpegPath)
        pnlStatus.Controls.Add(lblTimer)
        pnlStatus.Controls.Add(btnFFmpegBrowse)
        pnlStatus.Controls.Add(lblRecState)
        pnlStatus.Controls.Add(lblRecSize)
        pnlStatus.Controls.Add(lblRecFrames)
        pnlStatus.Controls.Add(lblRecBitrate)
        pnlStatus.Location = New Point(543, 164)
        pnlStatus.Name = "pnlStatus"
        pnlStatus.Size = New Size(463, 200)
        pnlStatus.TabIndex = 90
        ' 
        ' lblRecState
        ' 
        lblRecState.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        lblRecState.BackColor = Color.FromArgb(CByte(20), CByte(20), CByte(24))
        lblRecState.Font = New Font("GeForce", 14F, FontStyle.Bold)
        lblRecState.ForeColor = Color.FromArgb(CByte(160), CByte(160), CByte(160))
        lblRecState.Location = New Point(15, 15)
        lblRecState.Name = "lblRecState"
        lblRecState.Size = New Size(433, 32)
        lblRecState.TabIndex = 0
        lblRecState.Text = "● Idle"
        lblRecState.TextAlign = ContentAlignment.MiddleLeft
        ' 
        ' lblRecSize
        ' 
        lblRecSize.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        lblRecSize.Font = New Font("Consolas", 14F, FontStyle.Bold)
        lblRecSize.ForeColor = Color.FromArgb(CByte(230), CByte(230), CByte(230))
        lblRecSize.Location = New Point(15, 87)
        lblRecSize.Name = "lblRecSize"
        lblRecSize.Size = New Size(433, 30)
        lblRecSize.TabIndex = 2
        lblRecSize.Text = "0.0 MB"
        lblRecSize.TextAlign = ContentAlignment.MiddleLeft
        ' 
        ' lblRecFrames
        ' 
        lblRecFrames.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        lblRecFrames.Font = New Font("Consolas", 10F)
        lblRecFrames.ForeColor = Color.FromArgb(CByte(160), CByte(160), CByte(160))
        lblRecFrames.Location = New Point(307, 51)
        lblRecFrames.Name = "lblRecFrames"
        lblRecFrames.Size = New Size(141, 20)
        lblRecFrames.TabIndex = 3
        lblRecFrames.Text = "0 frames"
        lblRecFrames.TextAlign = ContentAlignment.MiddleRight
        ' 
        ' lblRecBitrate
        ' 
        lblRecBitrate.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        lblRecBitrate.Font = New Font("Consolas", 10F)
        lblRecBitrate.ForeColor = Color.FromArgb(CByte(160), CByte(160), CByte(160))
        lblRecBitrate.Location = New Point(15, 51)
        lblRecBitrate.Name = "lblRecBitrate"
        lblRecBitrate.Size = New Size(433, 20)
        lblRecBitrate.TabIndex = 4
        lblRecBitrate.Text = "Target: -- Mbps"
        lblRecBitrate.TextAlign = ContentAlignment.MiddleLeft
        ' 
        ' OPEN_UI
        ' 
        OPEN_UI.Enabled = True
        OPEN_UI.Interval = 1
        ' 
        ' UI_Engine
        ' 
        AutoScaleDimensions = New SizeF(7F, 15F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = Color.Coral
        ClientSize = New Size(1920, 1080)
        Controls.Add(BT_Back)
        Controls.Add(settings_top)
        Controls.Add(DIMBOX_2)
        Controls.Add(settings_menu)
        Font = New Font("Segoe UI", 9F)
        FormBorderStyle = FormBorderStyle.None
        MaximizeBox = False
        Name = "UI_Engine"
        Opacity = 0R
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterScreen
        Text = "nvsphelper64"
        TopMost = True
        TransparencyKey = Color.Coral
        WindowState = FormWindowState.Maximized
        pnlCapture.ResumeLayout(False)
        pnlCapture.PerformLayout()
        pnlEncoder.ResumeLayout(False)
        pnlEncoder.PerformLayout()
        pnlRes.ResumeLayout(False)
        pnlRes.PerformLayout()
        pnlPerf.ResumeLayout(False)
        pnlPerf.PerformLayout()
        CType(nudFPS, ComponentModel.ISupportInitialize).EndInit()
        CType(nudBitrate, ComponentModel.ISupportInitialize).EndInit()
        pnlPreset.ResumeLayout(False)
        pnlPreset.PerformLayout()
        CType(nudReplayDuration, ComponentModel.ISupportInitialize).EndInit()
        pnlAudio.ResumeLayout(False)
        pnlAudio.PerformLayout()
        CType(trkSysVol, ComponentModel.ISupportInitialize).EndInit()
        CType(trkMicVol, ComponentModel.ISupportInitialize).EndInit()
        pnlOutput.ResumeLayout(False)
        pnlOutput.PerformLayout()
        pnlGitHub.ResumeLayout(False)
        pnlGitHub.PerformLayout()
        pnlHub.ResumeLayout(False)
        pnlHub.PerformLayout()
        CType(DIMBOX_2, ComponentModel.ISupportInitialize).EndInit()
        CType(settings_top, ComponentModel.ISupportInitialize).EndInit()
        settings_menu.ResumeLayout(False)
        settings_menu.PerformLayout()
        CType(PictureBox5, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox4, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox3, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox16, ComponentModel.ISupportInitialize).EndInit()
        CType(hg2, ComponentModel.ISupportInitialize).EndInit()
        pnlDiag.ResumeLayout(False)
        pnlDiag.PerformLayout()
        pnlStatus.ResumeLayout(False)
        pnlStatus.PerformLayout()
        ResumeLayout(False)
    End Sub

    ' ── Control Declarations ──

    Friend WithEvents lblStatus As System.Windows.Forms.Label
    Friend WithEvents lblTimer As System.Windows.Forms.Label
    Friend WithEvents cboCaptureMethod As System.Windows.Forms.ComboBox
    Friend WithEvents cboEncoder As System.Windows.Forms.ComboBox
    Friend WithEvents chkNativeRes As System.Windows.Forms.CheckBox
    Friend WithEvents cboResolution As System.Windows.Forms.ComboBox
    Friend WithEvents nudFPS As System.Windows.Forms.NumericUpDown
    Friend WithEvents nudBitrate As System.Windows.Forms.NumericUpDown
    Friend WithEvents lblBitrateHint As System.Windows.Forms.Label
    Friend WithEvents txtOutputDir As System.Windows.Forms.TextBox
    Friend WithEvents btnBrowse As System.Windows.Forms.Button
    Friend WithEvents txtFFmpegPath As System.Windows.Forms.TextBox
    Friend WithEvents btnFFmpegBrowse As System.Windows.Forms.Button
    Friend WithEvents btnRecord As System.Windows.Forms.Button
    Friend WithEvents btnStop As System.Windows.Forms.Button
    Friend WithEvents btnDetect As System.Windows.Forms.Button
    Friend WithEvents tmrRecording As System.Windows.Forms.Timer
    Friend WithEvents lblTitle As Label
    Friend WithEvents pnlCapture As Panel
    Friend WithEvents lblCapTitle As Label
    Friend WithEvents pnlEncoder As Panel
    Friend WithEvents lblEncTitle As Label
    Friend WithEvents pnlRes As Panel
    Friend WithEvents pnlPerf As Panel
    Friend WithEvents lblFPSTitle As Label
    Friend WithEvents lblBitTitle As Label
    Friend WithEvents pnlOutput As Panel
    Friend WithEvents lblOutTitle As Label
    Friend WithEvents lblFfmpegTitle As Label

    ' ✅ P2: new controls for full Overlay config display
    Friend WithEvents pnlPreset As Panel
    Friend WithEvents lblPresetTitle As Label
    Friend WithEvents lblPresetValue As Label
    Friend WithEvents lblNvencPreset As Label
    Friend WithEvents lblReplayTitle As Label
    Friend WithEvents nudReplayDuration As NumericUpDown

    Friend WithEvents pnlAudio As Panel
    Friend WithEvents lblAudioTitle As Label
    Friend WithEvents chkSysAudio As CheckBox
    Friend WithEvents chkMic As CheckBox
    Friend WithEvents lblSysVol As Label
    Friend WithEvents trkSysVol As TrackBar
    Friend WithEvents lblMicVol As Label
    Friend WithEvents trkMicVol As TrackBar
    Friend WithEvents lblMicDevice As Label
    Friend WithEvents btnOpenAudioSettings As Button

    Friend WithEvents pnlGitHub As Panel
    Friend WithEvents lblGitHubTitle As Label
    Friend WithEvents lblGitHubUser As Label
    Friend WithEvents lblGitHubStatus As Label

    Friend WithEvents pnlHub As Panel
    Friend WithEvents lblHubTitle As Label
    Friend WithEvents lblHubStatus As Label
    Friend WithEvents lblHubClients As Label

    Friend WithEvents lblConfigSource As Label
    Friend WithEvents tmrRefresh As System.Windows.Forms.Timer
    Friend WithEvents DIMBOX_2 As PictureBox
    Friend WithEvents BT_Back As Label
    Friend WithEvents settings_top As PictureBox
    Friend WithEvents settings_menu As Panel
    Friend WithEvents PictureBox5 As PictureBox
    Friend WithEvents PictureBox4 As PictureBox
    Friend WithEvents PictureBox3 As PictureBox
    Friend WithEvents PictureBox16 As PictureBox
    Friend WithEvents hg2 As PictureBox
    Friend WithEvents OPEN_UI As System.Windows.Forms.Timer

    ' ✅ P2.10: real-time status panel controls
    Friend WithEvents pnlStatus As Panel
    Friend WithEvents lblRecState As Label
    Friend WithEvents lblRecSize As Label
    Friend WithEvents lblRecFrames As Label
    Friend WithEvents lblRecBitrate As Label
    Friend WithEvents btnStressTest As Button

    ' ✅ PHASE 3: Effective Runtime diagnostics panel + mirror note
    Friend WithEvents pnlDiag As Panel
    Friend WithEvents lblDiagTitle As Label
    Friend WithEvents txtDiagnostics As TextBox
    Friend WithEvents lblMirrorNote As Label

End Class
