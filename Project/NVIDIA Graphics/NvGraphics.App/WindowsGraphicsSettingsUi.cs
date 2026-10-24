using System.Drawing;
using System.Windows.Forms;

namespace NvGraphics;

// Windows Settings-inspired shell. The data and writes still come from
// WindowsGpuPreferenceService; this UI must never simulate a GPU assignment.
public sealed partial class NvGraphicsForm
{
    private TextBox _searchBox;
    private Label _selectionHint;

    private sealed record GraphicsChoice(string Label, string AdapterId = null, int? WindowsPolicy = null)
    {
        public override string ToString() => Label;
    }

    private static readonly Color PageColor = Color.FromArgb(247, 247, 249);
    private static readonly Color CardColor = Color.White;
    private static readonly Color AccentColor = Color.FromArgb(0, 98, 190);
    private static readonly Color SecondaryText = Color.FromArgb(92, 92, 99);

    private static Label TextLabel(string caption, int left, int top, int width, bool strong = false)
        => new()
        {
            Text = caption, Location = new Point(left, top), Size = new Size(width, 28),
            Font = new Font("Segoe UI", strong ? 10.5f : 9.5f,
                strong ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = strong ? Color.FromArgb(32, 32, 36) : SecondaryText
        };

    private static Button ActionButton(string label, int left, int top, int width, bool primary = false)
        => new()
        {
            Text = label, Location = new Point(left, top), Size = new Size(width, 34),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? AccentColor : Color.FromArgb(242, 243, 246),
            ForeColor = primary ? Color.White : Color.FromArgb(32, 32, 36),
            UseVisualStyleBackColor = false
        };

    private static Panel Card() => new()
    {
        Dock = DockStyle.Fill, BackColor = CardColor,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 0, 6)
    };

    private void BuildGraphicsSettingsUi()
    {
        SuspendLayout();
        Text = "NVIDIA Graphics — Windows Graphics preferences";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = PageColor;
        ClientSize = new Size(1180, 930);
        MinimumSize = new Size(1020, 810);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(26, 20, 26, 14),
            BackColor = PageColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 216));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 147));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));

        var heading = new Panel { Dock = DockStyle.Fill, BackColor = PageColor };
        heading.Controls.Add(new Label
        {
            Text = "Graphics", Location = new Point(0, 4), Size = new Size(800, 46),
            Font = new Font("Segoe UI", 23, FontStyle.Bold),
            ForeColor = Color.FromArgb(25, 25, 28)
        });
        heading.Controls.Add(TextLabel("System  >  Display  >  Graphics    ·    Live Windows graphics preferences", 2, 57, 950));
        layout.Controls.Add(heading, 0, 0);

        var add = Card();
        add.Controls.Add(TextLabel("Custom options for apps", 18, 12, 570, true));
        add.Controls.Add(TextLabel("Add a desktop app or select an existing Windows Graphics entry.", 18, 43, 660));
        _browseButton = ActionButton("Browse .exe", 0, 27, 130, true);
        _browseButton.Click += (_, _) => BrowseForExecutable();
        _addRunningGameButton = ActionButton("Add running app", 0, 27, 150);
        _addRunningGameButton.Click += (_, _) => AddRunningGameToProfiles();
        var openNative = ActionButton("Open Windows Settings", 0, 27, 195);
        openNative.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:display-advancedgraphics") { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus("Could not open Windows Settings: " + ex.Message, true); }
        };
        var storeButton = ActionButton("Microsoft Store apps", 0, 3, 174);
        storeButton.Click += async (_, _) => await AddStoreAppAsync();
        var addActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 0, 8, 0),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        _browseButton.Top = 3;
        _addRunningGameButton.Top = 3;
        openNative.Top = 3;
        addActions.Controls.Add(openNative);
        addActions.Controls.Add(storeButton);
        addActions.Controls.Add(_addRunningGameButton);
        addActions.Controls.Add(_browseButton);
        add.Controls.Add(addActions);
        layout.Controls.Add(add, 0, 1);

        var search = new Panel { Dock = DockStyle.Fill };
        search.Controls.Add(TextLabel("Find an app", 2, 18, 112, true));
        _searchBox = new TextBox
        {
            Location = new Point(120, 15), Size = new Size(430, 32),
            PlaceholderText = "Search by app name or executable path",
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        _searchBox.TextChanged += (_, _) => RefreshGameList();
        search.Controls.Add(_searchBox);
        _reloadButton = ActionButton("Refresh from Windows", 0, 12, 190);
        _reloadButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        search.Resize += (_, _) => _reloadButton.Left = search.ClientSize.Width - _reloadButton.Width;
        _reloadButton.Click += (_, _) => ReloadAll();
        search.Controls.Add(_reloadButton);
        layout.Controls.Add(search, 0, 2);

        var apps = Card();
        var appHeader = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = CardColor };
        appHeader.Controls.Add(TextLabel("Applications", 16, 8, 650, true));
        _gameList = new ListView
        {
            Location = new Point(16, 44), Dock = DockStyle.Fill,
            View = View.Details, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.None, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9.5f)
        };
        _gameList.Columns.Add("App", 185);
        _gameList.Columns.Add("Windows GPU preference", 228);
        _gameList.Columns.Add("Source", 130);
        _gameList.Columns.Add("Executable / App ID", 315);
        _gameList.SelectedIndexChanged += (_, _) => ShowSelectedProfile();
        // Docking inside a content panel keeps header visible during resize.
        var appContent = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 41, 16, 10) };
        appContent.Controls.Add(_gameList);
        var appCaption = TextLabel("Applications", 16, 6, 650, true);
        appContent.Controls.Add(appCaption);
        appCaption.BringToFront();
        apps.Controls.Add(appContent);
        apps.Controls.Add(appHeader);
        appHeader.BringToFront();
        layout.Controls.Add(apps, 0, 3);

        var detail = Card();
        detail.Controls.Add(TextLabel("Graphics preference", 18, 9, 500, true));
        _selectionHint = TextLabel("Select an app above, or browse for an executable.", 18, 38, 850);
        detail.Controls.Add(_selectionHint);
        detail.Controls.Add(TextLabel("Name", 20, 72, 90));
        _nameBox = new TextBox { Location = new Point(100, 69), Width = 272 };
        detail.Controls.Add(_nameBox);
        detail.Controls.Add(TextLabel("Graphics", 396, 72, 90));
        _gpuCombo = new ComboBox
        {
            Location = new Point(490, 69), Width = 465,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        detail.Controls.Add(_gpuCombo);
        _gpuCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_applyButton is not null && _gameList?.SelectedItems.Count > 0)
            {
                _applyButton.Enabled = _gpuCombo.SelectedIndex >= 0;
                if (!_loadingGpuChoice) _gpuChoiceDirty = true;
            }
        };
        detail.Controls.Add(TextLabel("App path", 20, 110, 95));
        _pathBox = new TextBox
        {
            Location = new Point(100, 107), Width = 854, ReadOnly = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        detail.Controls.Add(_pathBox);
        detail.Resize += (_, _) =>
        {
            _gpuCombo.Width = Math.Max(220, detail.ClientSize.Width - _gpuCombo.Left - 18);
            _pathBox.Width = Math.Max(220, detail.ClientSize.Width - _pathBox.Left - 18);
            _selectionHint.Width = Math.Max(200, detail.ClientSize.Width - _selectionHint.Left - 18);
        };
        _applyButton = ActionButton("Save GPU preference", 19, 151, 186, true);
        _applyButton.Click += (_, _) => ApplyPreference();
        detail.Controls.Add(_applyButton);
        _removeEntryButton = ActionButton("Reset Windows setting", 215, 151, 188);
        _removeEntryButton.Click += (_, _) => RemoveWindowsEntry();
        detail.Controls.Add(_removeEntryButton);
        _addUpdateButton = ActionButton("Save local profile", 413, 151, 154);
        _addUpdateButton.Click += (_, _) => AddOrUpdateProfile();
        detail.Controls.Add(_addUpdateButton);
        _removeButton = ActionButton("Forget local profile", 577, 151, 168);
        _removeButton.Click += (_, _) => RemoveProfile();
        detail.Controls.Add(_removeButton);
        _restoreButton = ActionButton("Restore last backup", 755, 151, 167);
        _restoreButton.Click += (_, _) => RestoreLastBackup();
        detail.Controls.Add(_restoreButton);
        _saveButton = ActionButton("Save profiles", 930, 151, 135);
        _saveButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _saveButton.Click += (_, _) => SaveProfiles();
        detail.Controls.Add(_saveButton);
        _saveButton.Visible = false; // profile saves already happen in their actions
        layout.Controls.Add(detail, 0, 4);

        var inventory = Card();
        var inventoryHeader = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = CardColor };
        inventoryHeader.Controls.Add(TextLabel("Detected graphics adapters", 16, 5, 580, true));
        _refreshTelemetryButton = ActionButton("Refresh telemetry", 0, 1, 165);
        _refreshTelemetryButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        inventoryHeader.Resize += (_, _) => _refreshTelemetryButton.Left =
            inventoryHeader.ClientSize.Width - _refreshTelemetryButton.Width - 12;
        _refreshTelemetryButton.Click += (_, _) => RefreshTelemetry();
        inventoryHeader.Controls.Add(_refreshTelemetryButton);
        _adapterList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _adapterList.Columns.Add("Adapter", 184);
        _adapterList.Columns.Add("Adapter ID", 168);
        _adapterList.Columns.Add("PCI bus", 112);
        _adapterList.Columns.Add("Driver", 85);
        _adapterList.Columns.Add("Status", 70);
        _adapterList.Columns.Add("Util %", 60);
        _adapterList.Columns.Add("VRAM (MiB)", 102);
        _adapterList.Columns.Add("DXGI LUID", 112);
        var adapterContent = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 37, 16, 9) };
        adapterContent.Controls.Add(_adapterList);
        var adapterCaption = TextLabel("Detected graphics adapters", 16, 5, 560, true);
        adapterContent.Controls.Add(adapterCaption);
        adapterCaption.BringToFront();
        adapterContent.Controls.Add(_refreshTelemetryButton);
        adapterContent.Resize += (_, _) =>
            _refreshTelemetryButton.Left = adapterContent.ClientSize.Width - _refreshTelemetryButton.Width - 14;
        _refreshTelemetryButton.BringToFront();
        inventory.Controls.Add(adapterContent);
        inventory.Controls.Add(inventoryHeader);
        inventoryHeader.BringToFront();
        layout.Controls.Add(inventory, 0, 5);

        var footer = new Panel { Dock = DockStyle.Fill };
        _statusLabel = new Label
        {
            Text = "Preferences are read from Windows; saving modifies Windows for that app only.",
            Dock = DockStyle.Fill, Padding = new Padding(4, 9, 0, 0),
            ForeColor = SecondaryText, AutoEllipsis = true
        };
        footer.Controls.Add(_statusLabel);
        layout.Controls.Add(footer, 0, 6);

        Controls.Add(layout);
        _gpuCombo.Items.Add(new GraphicsChoice("Let Windows decide", WindowsPolicy: 0));
        _gpuCombo.Items.Add(new GraphicsChoice("Power saving (Windows)", WindowsPolicy: 1));
        _gpuCombo.Items.Add(new GraphicsChoice("High performance (Windows)", WindowsPolicy: 2));
        _gpuCombo.SelectedIndex = 0;
        _applyButton.Enabled = false;
        _removeEntryButton.Enabled = false;
        _removeButton.Enabled = false;
        _addUpdateButton.Enabled = false;
        _saveButton.Enabled = true;
        ResumeLayout();
    }

    private GraphicsChoice SelectedGraphicsChoice() => _gpuCombo.SelectedItem as GraphicsChoice;
    private static bool SearchMatches(string query, string name, string path)
        => string.IsNullOrWhiteSpace(query) ||
           (name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
           (path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}
