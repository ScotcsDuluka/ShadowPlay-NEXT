<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class AudioSettingsForm
    Inherits System.Windows.Forms.Form

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New ComponentModel.Container()
        grpTrackMode = New GroupBox()
        radSingle = New RadioButton()
        radSeparate = New RadioButton()
        lblModeHint = New Label()
        grpSystem = New GroupBox()
        chkSystem = New CheckBox()
        trkSystemVol = New TrackBar()
        lblSystemVol = New Label()
        grpMic = New GroupBox()
        chkMic = New CheckBox()
        cboMic = New ComboBox()
        lblMicDevice = New Label()
        btnRefresh = New Button()
        trkMicVol = New TrackBar()
        lblMicVol = New Label()
        btnApply = New Button()
        btnCancel = New Button()
        btnTest = New Button()
        lblStatus = New Label()
        DIMBOX_2 = New PictureBox()
        BT_Back = New Label()
        settings_top = New PictureBox()
        settings_menu = New Panel()
        quality_main = New Label()
        ICO_MENU1 = New Label()
        PictureBox5 = New PictureBox()
        PictureBox4 = New PictureBox()
        PictureBox3 = New PictureBox()
        PictureBox16 = New PictureBox()
        hg2 = New PictureBox()
        Audio_Capture_Menutext = New Label()
        OPEN_UI = New System.Windows.Forms.Timer(components)
        grpTrackMode.SuspendLayout()
        grpSystem.SuspendLayout()
        CType(trkSystemVol, ComponentModel.ISupportInitialize).BeginInit()
        grpMic.SuspendLayout()
        CType(trkMicVol, ComponentModel.ISupportInitialize).BeginInit()
        CType(DIMBOX_2, ComponentModel.ISupportInitialize).BeginInit()
        CType(settings_top, ComponentModel.ISupportInitialize).BeginInit()
        settings_menu.SuspendLayout()
        CType(PictureBox5, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox4, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox3, ComponentModel.ISupportInitialize).BeginInit()
        CType(PictureBox16, ComponentModel.ISupportInitialize).BeginInit()
        CType(hg2, ComponentModel.ISupportInitialize).BeginInit()
        SuspendLayout()
        ' 
        ' grpTrackMode
        ' 
        grpTrackMode.Controls.Add(radSingle)
        grpTrackMode.Controls.Add(radSeparate)
        grpTrackMode.Controls.Add(lblModeHint)
        grpTrackMode.ForeColor = Color.White
        grpTrackMode.Location = New Point(129, 190)
        grpTrackMode.Name = "grpTrackMode"
        grpTrackMode.Size = New Size(488, 95)
        grpTrackMode.TabIndex = 1
        grpTrackMode.TabStop = False
        grpTrackMode.Text = "Audio Track Mode"
        ' 
        ' radSingle
        ' 
        radSingle.AutoSize = True
        radSingle.Checked = True
        radSingle.Location = New Point(15, 22)
        radSingle.Name = "radSingle"
        radSingle.Size = New Size(203, 19)
        radSingle.TabIndex = 0
        radSingle.TabStop = True
        radSingle.Text = "Single track (system + mic mixed)"
        ' 
        ' radSeparate
        ' 
        radSeparate.AutoSize = True
        radSeparate.Location = New Point(15, 45)
        radSeparate.Name = "radSeparate"
        radSeparate.Size = New Size(211, 19)
        radSeparate.TabIndex = 1
        radSeparate.Text = "Separate tracks (system + mic split)"
        ' 
        ' lblModeHint
        ' 
        lblModeHint.AutoSize = True
        lblModeHint.ForeColor = Color.Gray
        lblModeHint.Location = New Point(15, 68)
        lblModeHint.Name = "lblModeHint"
        lblModeHint.Size = New Size(413, 15)
        lblModeHint.TabIndex = 2
        lblModeHint.Text = "Single: 1 AAC track, mix of both sources.   Separate: 2 AAC tracks in same file."
        ' 
        ' grpSystem
        ' 
        grpSystem.Controls.Add(chkSystem)
        grpSystem.Controls.Add(trkSystemVol)
        grpSystem.Controls.Add(lblSystemVol)
        grpSystem.ForeColor = Color.White
        grpSystem.Location = New Point(129, 295)
        grpSystem.Name = "grpSystem"
        grpSystem.Size = New Size(488, 90)
        grpSystem.TabIndex = 2
        grpSystem.TabStop = False
        grpSystem.Text = "System Audio (loopback)"
        ' 
        ' chkSystem
        ' 
        chkSystem.AutoSize = True
        chkSystem.Checked = True
        chkSystem.CheckState = CheckState.Checked
        chkSystem.Location = New Point(15, 22)
        chkSystem.Name = "chkSystem"
        chkSystem.Size = New Size(141, 19)
        chkSystem.TabIndex = 0
        chkSystem.Text = "Capture system audio"
        ' 
        ' trkSystemVol
        ' 
        trkSystemVol.Location = New Point(15, 45)
        trkSystemVol.Maximum = 100
        trkSystemVol.Name = "trkSystemVol"
        trkSystemVol.Size = New Size(360, 45)
        trkSystemVol.TabIndex = 1
        trkSystemVol.Value = 100
        ' 
        ' lblSystemVol
        ' 
        lblSystemVol.AutoSize = True
        lblSystemVol.Location = New Point(385, 50)
        lblSystemVol.Name = "lblSystemVol"
        lblSystemVol.Size = New Size(35, 15)
        lblSystemVol.TabIndex = 2
        lblSystemVol.Text = "100%"
        ' 
        ' grpMic
        ' 
        grpMic.Controls.Add(chkMic)
        grpMic.Controls.Add(cboMic)
        grpMic.Controls.Add(lblMicDevice)
        grpMic.Controls.Add(btnRefresh)
        grpMic.Controls.Add(trkMicVol)
        grpMic.Controls.Add(lblMicVol)
        grpMic.ForeColor = Color.White
        grpMic.Location = New Point(129, 395)
        grpMic.Name = "grpMic"
        grpMic.Size = New Size(488, 130)
        grpMic.TabIndex = 3
        grpMic.TabStop = False
        grpMic.Text = "Microphone"
        ' 
        ' chkMic
        ' 
        chkMic.AutoSize = True
        chkMic.Location = New Point(15, 22)
        chkMic.Name = "chkMic"
        chkMic.Size = New Size(136, 19)
        chkMic.TabIndex = 0
        chkMic.Text = "Capture microphone"
        ' 
        ' cboMic
        ' 
        cboMic.DropDownStyle = ComboBoxStyle.DropDownList
        cboMic.Location = New Point(70, 45)
        cboMic.Name = "cboMic"
        cboMic.Size = New Size(330, 23)
        cboMic.TabIndex = 1
        ' 
        ' lblMicDevice
        ' 
        lblMicDevice.AutoSize = True
        lblMicDevice.Location = New Point(15, 48)
        lblMicDevice.Name = "lblMicDevice"
        lblMicDevice.Size = New Size(45, 15)
        lblMicDevice.TabIndex = 2
        lblMicDevice.Text = "Device:"
        ' 
        ' btnRefresh
        ' 
        btnRefresh.Location = New Point(408, 44)
        btnRefresh.Name = "btnRefresh"
        btnRefresh.Size = New Size(70, 25)
        btnRefresh.TabIndex = 3
        btnRefresh.Text = "Refresh"
        ' 
        ' trkMicVol
        ' 
        trkMicVol.Location = New Point(15, 80)
        trkMicVol.Maximum = 100
        trkMicVol.Name = "trkMicVol"
        trkMicVol.Size = New Size(360, 45)
        trkMicVol.TabIndex = 4
        trkMicVol.Value = 100
        ' 
        ' lblMicVol
        ' 
        lblMicVol.AutoSize = True
        lblMicVol.Location = New Point(385, 85)
        lblMicVol.Name = "lblMicVol"
        lblMicVol.Size = New Size(35, 15)
        lblMicVol.TabIndex = 5
        lblMicVol.Text = "100%"
        ' 
        ' btnApply
        ' 
        btnApply.ForeColor = Color.Black
        btnApply.Location = New Point(351, 540)
        btnApply.Name = "btnApply"
        btnApply.Size = New Size(85, 30)
        btnApply.TabIndex = 4
        btnApply.Text = "Apply"
        btnApply.UseVisualStyleBackColor = True
        ' 
        ' btnCancel
        ' 
        btnCancel.ForeColor = Color.Black
        btnCancel.Location = New Point(442, 540)
        btnCancel.Name = "btnCancel"
        btnCancel.Size = New Size(85, 30)
        btnCancel.TabIndex = 5
        btnCancel.Text = "Cancel"
        btnCancel.UseVisualStyleBackColor = True
        ' 
        ' btnTest
        ' 
        btnTest.ForeColor = Color.Black
        btnTest.Location = New Point(129, 540)
        btnTest.Name = "btnTest"
        btnTest.Size = New Size(85, 30)
        btnTest.TabIndex = 6
        btnTest.Text = "Test"
        btnTest.UseVisualStyleBackColor = True
        ' 
        ' lblStatus
        ' 
        lblStatus.AutoSize = True
        lblStatus.ForeColor = Color.Gray
        lblStatus.Location = New Point(222, 547)
        lblStatus.Name = "lblStatus"
        lblStatus.Size = New Size(0, 15)
        lblStatus.TabIndex = 7
        ' 
        ' DIMBOX_2
        ' 
        DIMBOX_2.BackColor = Color.Blue
        DIMBOX_2.BackgroundImageLayout = ImageLayout.None
        DIMBOX_2.Location = New Point(0, 160)
        DIMBOX_2.Name = "DIMBOX_2"
        DIMBOX_2.Size = New Size(80, 80)
        DIMBOX_2.TabIndex = 94
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
        BT_Back.TabIndex = 99
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
        settings_top.TabIndex = 97
        settings_top.TabStop = False
        ' 
        ' settings_menu
        ' 
        settings_menu.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        settings_menu.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        settings_menu.Controls.Add(quality_main)
        settings_menu.Controls.Add(ICO_MENU1)
        settings_menu.Controls.Add(PictureBox5)
        settings_menu.Controls.Add(PictureBox4)
        settings_menu.Controls.Add(PictureBox3)
        settings_menu.Controls.Add(PictureBox16)
        settings_menu.Controls.Add(grpTrackMode)
        settings_menu.Controls.Add(grpSystem)
        settings_menu.Controls.Add(hg2)
        settings_menu.Controls.Add(grpMic)
        settings_menu.Controls.Add(btnApply)
        settings_menu.Controls.Add(btnCancel)
        settings_menu.Controls.Add(btnTest)
        settings_menu.Controls.Add(lblStatus)
        settings_menu.Controls.Add(Audio_Capture_Menutext)
        settings_menu.ForeColor = Color.White
        settings_menu.Location = New Point(80, 160)
        settings_menu.Name = "settings_menu"
        settings_menu.Size = New Size(1760, 840)
        settings_menu.TabIndex = 98
        ' 
        ' quality_main
        ' 
        quality_main.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        quality_main.Font = New Font("Segoe UI Semibold", 18F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        quality_main.ForeColor = Color.White
        quality_main.Location = New Point(129, 85)
        quality_main.Name = "quality_main"
        quality_main.Size = New Size(515, 67)
        quality_main.TabIndex = 73
        quality_main.Text = "Quality:"
        quality_main.TextAlign = ContentAlignment.MiddleLeft
        ' 
        ' ICO_MENU1
        ' 
        ICO_MENU1.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        ICO_MENU1.Font = New Font("nvgcshare", 50F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        ICO_MENU1.ForeColor = Color.White
        ICO_MENU1.Location = New Point(62, 85)
        ICO_MENU1.Name = "ICO_MENU1"
        ICO_MENU1.Size = New Size(61, 67)
        ICO_MENU1.TabIndex = 111
        ICO_MENU1.Text = ""
        ICO_MENU1.TextAlign = ContentAlignment.MiddleLeft
        ' 
        ' PictureBox5
        ' 
        PictureBox5.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        PictureBox5.BackColor = Color.FromArgb(CByte(38), CByte(43), CByte(47))
        PictureBox5.Location = New Point(269, 2190)
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
        PictureBox4.Location = New Point(12, 2190)
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
        PictureBox3.Location = New Point(269, 2069)
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
        PictureBox16.Location = New Point(12, 2069)
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
        hg2.Location = New Point(11, 2068)
        hg2.Name = "hg2"
        hg2.Size = New Size(513, 239)
        hg2.TabIndex = 87
        hg2.TabStop = False
        hg2.Visible = False
        ' 
        ' Audio_Capture_Menutext
        ' 
        Audio_Capture_Menutext.AutoSize = True
        Audio_Capture_Menutext.Font = New Font("GeForce", 24F, FontStyle.Bold)
        Audio_Capture_Menutext.Location = New Point(62, 43)
        Audio_Capture_Menutext.Name = "Audio_Capture_Menutext"
        Audio_Capture_Menutext.Size = New Size(210, 42)
        Audio_Capture_Menutext.TabIndex = 0
        Audio_Capture_Menutext.Text = "Audio Capture"
        ' 
        ' OPEN_UI
        ' 
        OPEN_UI.Enabled = True
        OPEN_UI.Interval = 1
        ' 
        ' AudioSettingsForm
        ' 
        AutoScaleDimensions = New SizeF(7F, 15F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = Color.Coral
        ClientSize = New Size(1920, 1080)
        Controls.Add(BT_Back)
        Controls.Add(settings_top)
        Controls.Add(settings_menu)
        Controls.Add(DIMBOX_2)
        FormBorderStyle = FormBorderStyle.None
        MaximizeBox = False
        MinimizeBox = False
        Name = "AudioSettingsForm"
        Opacity = 0R
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterScreen
        Text = "Audio Settings — ShadowPlay Engine"
        TopMost = True
        TransparencyKey = Color.Coral
        WindowState = FormWindowState.Maximized
        grpTrackMode.ResumeLayout(False)
        grpTrackMode.PerformLayout()
        grpSystem.ResumeLayout(False)
        grpSystem.PerformLayout()
        CType(trkSystemVol, ComponentModel.ISupportInitialize).EndInit()
        grpMic.ResumeLayout(False)
        grpMic.PerformLayout()
        CType(trkMicVol, ComponentModel.ISupportInitialize).EndInit()
        CType(DIMBOX_2, ComponentModel.ISupportInitialize).EndInit()
        CType(settings_top, ComponentModel.ISupportInitialize).EndInit()
        settings_menu.ResumeLayout(False)
        settings_menu.PerformLayout()
        CType(PictureBox5, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox4, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox3, ComponentModel.ISupportInitialize).EndInit()
        CType(PictureBox16, ComponentModel.ISupportInitialize).EndInit()
        CType(hg2, ComponentModel.ISupportInitialize).EndInit()
        ResumeLayout(False)
    End Sub
    Friend WithEvents grpTrackMode As System.Windows.Forms.GroupBox
    Friend WithEvents radSingle As System.Windows.Forms.RadioButton
    Friend WithEvents radSeparate As System.Windows.Forms.RadioButton
    Friend WithEvents lblModeHint As System.Windows.Forms.Label
    Friend WithEvents grpSystem As System.Windows.Forms.GroupBox
    Friend WithEvents chkSystem As System.Windows.Forms.CheckBox
    Friend WithEvents trkSystemVol As System.Windows.Forms.TrackBar
    Friend WithEvents lblSystemVol As System.Windows.Forms.Label
    Friend WithEvents grpMic As System.Windows.Forms.GroupBox
    Friend WithEvents chkMic As System.Windows.Forms.CheckBox
    Friend WithEvents cboMic As System.Windows.Forms.ComboBox
    Friend WithEvents lblMicDevice As System.Windows.Forms.Label
    Friend WithEvents btnRefresh As System.Windows.Forms.Button
    Friend WithEvents trkMicVol As System.Windows.Forms.TrackBar
    Friend WithEvents lblMicVol As System.Windows.Forms.Label
    Friend WithEvents btnApply As System.Windows.Forms.Button
    Friend WithEvents btnCancel As System.Windows.Forms.Button
    Friend WithEvents btnTest As System.Windows.Forms.Button
    Friend WithEvents lblStatus As System.Windows.Forms.Label
    Friend WithEvents DIMBOX_2 As PictureBox
    Friend WithEvents BT_Back As Label
    Friend WithEvents settings_top As PictureBox
    Friend WithEvents settings_menu As Panel
    Friend WithEvents PictureBox5 As PictureBox
    Friend WithEvents PictureBox4 As PictureBox
    Friend WithEvents PictureBox3 As PictureBox
    Friend WithEvents PictureBox16 As PictureBox
    Friend WithEvents hg2 As PictureBox
    Friend WithEvents Audio_Capture_Menutext As Label
    Friend WithEvents OPEN_UI As System.Windows.Forms.Timer
    Friend WithEvents ICO_MENU1 As Label
    Friend WithEvents quality_main As Label
End Class
