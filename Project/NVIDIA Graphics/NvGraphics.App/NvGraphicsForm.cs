using NvGraphics.Abstractions;
using NvGraphics.Contracts;
using NvGraphics.Models;
using NvGraphics.Services;

namespace NvGraphics;

/// <summary>
/// NvGraphics — per-game GPU preference manager and NvContainer readiness client.
/// A registry preference is never reported as proof that the game runs on that GPU;
/// runtime verification is a separate status ("Runtime not verified").
/// </summary>
public sealed class NvGraphicsForm : Form
{
    private static readonly HttpClient ReadyClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly int _controlApiPort;
    private readonly bool _startHidden;
    private System.Windows.Forms.Timer _readyTimer;
    private int _readyReportInFlight;
    private readonly GpuDiscoveryService _discovery = new();
    private readonly GpuTelemetryService _telemetry = new();
    private readonly GameProfileStore _store = new();
    private readonly WindowsGpuPreferenceService _preferences;
    private readonly RegistryBackupService _backups = new();

    private List<GpuAdapterInfo> _adapters = new();
    private IReadOnlyList<RegistryValueEntry> _windowsApps = Array.Empty<RegistryValueEntry>();
    private readonly HashSet<string> _windowsOnlyPaths = new(StringComparer.OrdinalIgnoreCase);
    private GameProfileStoreData _data = new();
    private GpuTelemetrySnapshot _lastSnapshot;
    private System.Threading.Timer _inventoryTimer;

    private ListView _adapterList;
    private ListView _gameList;
    private TextBox _nameBox;
    private TextBox _pathBox;
    private ComboBox _gpuCombo;
    private Label _statusLabel;
    private Button _browseButton;
    private Button _addRunningGameButton;
    private Button _addUpdateButton;
    private Button _removeButton;
    private Button _applyButton;
    private Button _removeEntryButton;
    private Button _restoreButton;
    private Button _saveButton;
    private Button _reloadButton;
    private Button _refreshTelemetryButton;

    public NvGraphicsForm(int controlApiPort = 59012, bool startHidden = false)
    {
        _controlApiPort = controlApiPort;
        _startHidden = startHidden;
        _preferences = new WindowsGpuPreferenceService(new HkcuRegistryApi(), _backups);
        BuildUi();
        if (_startHidden)
        {
            ShowInTaskbar = false;
            Opacity = 0;
        }
        Load += (_, _) =>
        {
            ReloadAll();
            StartInventoryPublisher();
        };
        Shown += (_, _) =>
        {
            if (_startHidden)
            {
                Hide();
                Opacity = 1;
            }
            StartReadinessHeartbeat();
        };
        FormClosed += (_, _) =>
        {
            _readyTimer?.Dispose();
            _inventoryTimer?.Dispose();
        };
    }

    private void StartReadinessHeartbeat()
    {
        if (_readyTimer != null) return;
        _readyTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _readyTimer.Tick += async (_, _) => await ReportReadyAsync();
        _readyTimer.Start();
        _ = ReportReadyAsync();
    }

    private async Task ReportReadyAsync()
    {
        if (Interlocked.Exchange(ref _readyReportInFlight, 1) != 0) return;
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                windowHandle = Handle.ToInt64()
            });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await ReadyClient.PostAsync(
                $"http://127.0.0.1:{_controlApiPort}/v1/gpu/graphics/ready", content);
            if (!response.IsSuccessStatusCode)
                SetStatus($"NvContainer readiness report failed: HTTP {(int)response.StatusCode}", error: true);
            else if (_statusLabel.Text.StartsWith("NvContainer is not ready:", StringComparison.Ordinal) ||
                     _statusLabel.Text.StartsWith("NvContainer readiness report failed:", StringComparison.Ordinal))
                SetStatus("NvContainer is ready.");
        }
        catch (Exception ex)
        {
            SetStatus($"NvContainer is not ready: {ex.Message}", error: true);
        }
        finally
        {
            Interlocked.Exchange(ref _readyReportInFlight, 0);
        }
    }

    private void BuildUi()
    {
        Text = "NvGraphics — per-game GPU preferences";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(860, 540);

        var adaptersGroup = new GroupBox { Text = "Detected adapters", Location = new Point(12, 12), Size = new Size(960, 210) };
        _adapterList = new ListView
        {
            Location = new Point(10, 20),
            Size = new Size(940, 150),
            View = View.Details,
            FullRowSelect = true
        };
        _adapterList.Columns.Add("Adapter", 250);
        _adapterList.Columns.Add("Adapter ID (PnP)", 210);
        _adapterList.Columns.Add("PCI bus", 150);
        _adapterList.Columns.Add("Driver", 100);
        _adapterList.Columns.Add("Status", 70);
        _adapterList.Columns.Add("Util %", 50);
        _adapterList.Columns.Add("VRAM (MiB)", 90);
        _adapterList.Columns.Add("DXGI LUID", 150);
        adaptersGroup.Controls.Add(_adapterList);
        _refreshTelemetryButton = new Button { Text = "Refresh telemetry", Location = new Point(10, 176), Width = 130 };
        _refreshTelemetryButton.Click += (_, _) => RefreshTelemetry();
        adaptersGroup.Controls.Add(_refreshTelemetryButton);
        var telemetryNote = new Label
        {
            Text = "Utilization/VRAM require nvidia-smi; blank when unavailable.",
            Location = new Point(150, 180),
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        adaptersGroup.Controls.Add(telemetryNote);

        var gamesGroup = new GroupBox { Text = "Game profiles", Location = new Point(12, 228), Size = new Size(960, 180) };
        _gameList = new ListView
        {
            Location = new Point(10, 20),
            Size = new Size(940, 120),
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false
        };
        _gameList.Columns.Add("Name", 160);
        _gameList.Columns.Add("Executable", 340);
        _gameList.Columns.Add("Selected GPU", 210);
        _gameList.Columns.Add("Preference status", 240);
        _gameList.SelectedIndexChanged += (_, _) => ShowSelectedProfile();
        gamesGroup.Controls.Add(_gameList);

        var detailGroup = new GroupBox { Text = "Profile detail", Location = new Point(12, 414), Size = new Size(960, 170) };
        int y = 26;
        Label L(string t) => new() { Text = t, AutoSize = true, Location = new Point(10, y + 3) };
        detailGroup.Controls.Add(L("Name:"));
        _nameBox = new TextBox { Location = new Point(80, y), Width = 300 }; detailGroup.Controls.Add(_nameBox);
        detailGroup.Controls.Add(new Label { Text = "GPU:", AutoSize = true, Location = new Point(400, y + 3) });
        _gpuCombo = new ComboBox { Location = new Point(440, y), Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        detailGroup.Controls.Add(_gpuCombo);

        y += 34;
        detailGroup.Controls.Add(L("Executable:"));
        _pathBox = new TextBox { Location = new Point(80, y), Width = 660, ReadOnly = true }; detailGroup.Controls.Add(_pathBox);
        _browseButton = new Button { Text = "Browse...", Location = new Point(750, y - 1), Width = 80 };
        _browseButton.Click += (_, _) => BrowseForExecutable();
        detailGroup.Controls.Add(_browseButton);
        _addRunningGameButton = new Button
        {
            Text = "Add running game",
            Location = new Point(836, y - 1),
            Width = 112
        };
        _addRunningGameButton.Click += (_, _) => AddRunningGameToProfiles();
        detailGroup.Controls.Add(_addRunningGameButton);

        y += 36;
        _addUpdateButton = new Button { Text = "Add / Update profile", Location = new Point(80, y), Width = 140 };
        _addUpdateButton.Click += (_, _) => AddOrUpdateProfile();
        detailGroup.Controls.Add(_addUpdateButton);
        _applyButton = new Button { Text = "Apply preference", Location = new Point(230, y), Width = 120 };
        _applyButton.Click += (_, _) => ApplyPreference();
        detailGroup.Controls.Add(_applyButton);
        _removeEntryButton = new Button { Text = "Remove Windows entry", Location = new Point(358, y), Width = 150 };
        _removeEntryButton.Click += (_, _) => RemoveWindowsEntry();
        detailGroup.Controls.Add(_removeEntryButton);
        _removeButton = new Button { Text = "Remove profile", Location = new Point(516, y), Width = 110 };
        _removeButton.Click += (_, _) => RemoveProfile();
        detailGroup.Controls.Add(_removeButton);
        _restoreButton = new Button { Text = "Restore last backup", Location = new Point(634, y), Width = 130 };
        _restoreButton.Click += (_, _) => RestoreLastBackup();
        detailGroup.Controls.Add(_restoreButton);

        y += 38;
        _saveButton = new Button { Text = "Save profiles", Location = new Point(80, y), Width = 110 };
        _saveButton.Click += (_, _) => SaveProfiles();
        detailGroup.Controls.Add(_saveButton);
        _reloadButton = new Button { Text = "Reload all", Location = new Point(198, y), Width = 90 };
        _reloadButton.Click += (_, _) => ReloadAll();
        detailGroup.Controls.Add(_reloadButton);
        _statusLabel = new Label
        {
            Text = "Ready.",
            Location = new Point(12, 592),
            Size = new Size(960, 40),
            ForeColor = Color.Black
        };

        Controls.Add(adaptersGroup);
        Controls.Add(gamesGroup);
        Controls.Add(detailGroup);
        Controls.Add(_statusLabel);
    }

    // ---------- data loading ----------

    private void ReloadAll()
    {
        try
        {
            _adapters = _discovery.DiscoverAdapters();
        }
        catch (Exception ex)
        {
            SetStatus($"Error discovering adapters: {ex.Message}", error: true);
            _adapters = new();
        }

        try
        {
            _data = _store.Load();
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading profiles: {ex.Message}", error: true);
            _data = new GameProfileStoreData();
        }

        ReloadWindowsApps();

        var comboItems = new List<string> { "(system default)"        };
        comboItems.AddRange(_adapters.Where(a => a.AdapterId != null).Select(a => a.ToString()));
        _gpuCombo.Items.Clear();
        _gpuCombo.Items.AddRange(comboItems.ToArray());
        _gpuCombo.SelectedIndex = 0;

        RefreshAdapterList();
        SyncProfileRegistryStatus();
        RefreshGameList();
        RefreshTelemetry();
    }

    private void RefreshAdapterList()
    {
        _adapterList.BeginUpdate();
        _adapterList.Items.Clear();
        foreach (var a in _adapters)
        {
            var sample = GpuTelemetryService.ForAdapter(_lastSnapshot, a);
            var item = new ListViewItem(a.DisplayName);
            item.SubItems.Add(a.AdapterId ?? "(not a PCI adapter)");
            item.SubItems.Add(a.PciLocation ?? a.PciBusId ?? "");
            item.SubItems.Add(a.DriverVersion);
            item.SubItems.Add(a.Status);
            item.SubItems.Add(sample?.UtilizationPercent?.ToString("0") ?? "");
            item.SubItems.Add(sample is { MemoryUsedMiB: not null }
                ? $"{sample.MemoryUsedMiB:0} / {sample.MemoryTotalMiB:0}" : "");
            item.SubItems.Add(a.DxgiLuid ?? "");
            item.Tag = a;
            _adapterList.Items.Add(item);
        }
        _adapterList.EndUpdate();
    }

    private void RefreshTelemetry()
    {
        _lastSnapshot = _telemetry.Sample();
        if (_lastSnapshot is null)
            SetStatus($"Telemetry unavailable: {_telemetry.LastError}", error: false);
        RefreshAdapterList();
    }

    private void ReloadWindowsApps()
    {
        try
        {
            _windowsApps = _preferences.ListPreferences()
                .Where(entry => IsWindowsGraphicsApp(entry.Name))
                .ToArray();
        }
        catch (Exception ex)
        {
            _windowsApps = Array.Empty<RegistryValueEntry>();
            SetStatus($"Could not read Windows Graphics apps: {ex.Message}", error: true);
        }
    }

    private static bool IsWindowsGraphicsApp(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        ((Path.IsPathRooted(name) &&
          string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase)) ||
         (!Path.IsPathRooted(name) && name.Contains('!')));

    private static string WindowsAppDisplayName(string appId)
    {
        if (Path.IsPathRooted(appId))
            return Path.GetFileNameWithoutExtension(appId);
        int separator = appId.LastIndexOf('!');
        return separator >= 0 ? appId[(separator + 1)..] : appId;
    }

    private string AdapterIdForWindowsPreference(string value)
    {
        var windowsId = WindowsGpuPreferenceService.ParseSpecificAdapter(value);
        return string.IsNullOrEmpty(windowsId)
            ? null
            : _adapters.FirstOrDefault(adapter =>
                !string.IsNullOrEmpty(adapter.AdapterId) &&
                string.Equals(WindowsGpuPreferenceService.ToWindowsAdapterId(adapter.AdapterId),
                    windowsId, StringComparison.OrdinalIgnoreCase))?.AdapterId;
    }

    /// <summary>Reconciles each profile's stored status with the live registry value.</summary>
    private void SyncProfileRegistryStatus()
    {
        foreach (var p in _data.Profiles)
        {
            string value;
            try { value = _preferences.FindValue(p.ExePath); }
            catch (Exception ex)
            {
                p.Status = PreferenceStatus.Error;
                p.StatusMessage = $"Could not read Windows GPU preference: {ex.Message}";
                continue;
            }
            var applied = value is null ? null : WindowsGpuPreferenceService.ParseSpecificAdapter(value);
            var expected = WindowsGpuPreferenceService.ToWindowsAdapterId(p.SelectedAdapterId);
            if (applied is null)
            {
                p.Status = PreferenceStatus.Configured;
                p.StatusMessage = "Preference configured — not present in Windows registry." +
                    (p.LastAppliedUtc is null ? "" : $" (last applied {p.LastAppliedUtc:u} to adapter {p.LastAppliedAdapterId})");
            }
            else
            {
                p.Status = PreferenceStatus.Applied;
                p.LastAppliedAdapterId = applied;
                p.LastAppliedUtc = p.LastAppliedUtc ?? DateTime.UtcNow;
                bool matches = string.Equals(applied, expected, StringComparison.OrdinalIgnoreCase);
                p.StatusMessage = $"Windows GPU preference verified " +
                    $"({(matches ? "matches selection" : $"holds {applied}, selection is {expected}")}).";
            }
        }
    }

    private void RefreshGameList()
    {
        _windowsOnlyPaths.Clear();
        _gameList.BeginUpdate();
        _gameList.Items.Clear();
        foreach (var p in _data.Profiles)
        {
            var item = new ListViewItem(p.Name);
            item.SubItems.Add(p.ExePath);
            item.SubItems.Add(p.SelectedAdapterId ?? "(system default)");
            item.SubItems.Add(StatusText(p));
            item.Tag = p;
            _gameList.Items.Add(item);
        }
        foreach (var entry in _windowsApps)
        {
            if (_data.Profiles.Any(profile =>
                    string.Equals(profile.ExePath, entry.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var value = entry.Value as string ?? "";
            var windowsAdapterId = WindowsGpuPreferenceService.ParseSpecificAdapter(value);
            var profile = new GameProfile
            {
                Name = WindowsAppDisplayName(entry.Name),
                ExePath = entry.Name,
                SelectedAdapterId = AdapterIdForWindowsPreference(value),
                LastAppliedAdapterId = windowsAdapterId,
                Status = string.IsNullOrEmpty(windowsAdapterId)
                    ? PreferenceStatus.Configured
                    : PreferenceStatus.Applied,
                StatusMessage = string.IsNullOrEmpty(windowsAdapterId)
                    ? "Windows Graphics app — no specific adapter is currently selected."
                    : ProfileAdapterMessage(windowsAdapterId)
            };
            _windowsOnlyPaths.Add(profile.ExePath);
            var item = new ListViewItem(profile.Name);
            item.SubItems.Add(profile.ExePath);
            item.SubItems.Add(profile.SelectedAdapterId is null
                ? "(system default)"
                : _adapters.FirstOrDefault(adapter => adapter.AdapterId == profile.SelectedAdapterId)?.DisplayName
                    ?? profile.SelectedAdapterId);
            item.SubItems.Add(StatusText(profile));
            item.Tag = profile;
            _gameList.Items.Add(item);
        }
        _gameList.EndUpdate();

    }

    private string ProfileAdapterMessage(string windowsAdapterId)
    {
        var detected = _adapters.FirstOrDefault(adapter =>
            string.Equals(WindowsGpuPreferenceService.ToWindowsAdapterId(adapter.AdapterId),
                windowsAdapterId, StringComparison.OrdinalIgnoreCase));
        return detected is null
            ? $"Windows Graphics preference uses adapter {windowsAdapterId}, which is not currently detected."
            : $"Windows Graphics preference uses {detected.DisplayName}.";
    }

    private static string StatusText(GameProfile p) => p.Status switch
    {
        PreferenceStatus.NotConfigured => "Detected — no preference configured",
        PreferenceStatus.Configured => "Preference configured",
        PreferenceStatus.Applied => "Windows GPU preference applied",
        PreferenceStatus.RuntimeVerified => "Runtime GPU use verified",
        PreferenceStatus.RuntimeNotVerified => "Runtime GPU use not verified",
        _ => string.IsNullOrWhiteSpace(p.StatusMessage) ? "Error" : $"Error — {p.StatusMessage}"
    };

    // ---------- profile editing ----------

    private void ShowSelectedProfile()
    {
        var p = SelectedProfile();
        _removeButton.Enabled = p is not null && !_windowsOnlyPaths.Contains(p.ExePath);
        _applyButton.Enabled = p is not null;
        _removeEntryButton.Enabled = p is not null;
        if (p is null) return;
        _nameBox.Text = p.Name;
        _pathBox.Text = p.ExePath;
        int idx = 0;
        for (int i = 1; i < _gpuCombo.Items.Count; i++)
        {
            var comboText = (string)_gpuCombo.Items[i];
            var adapter = _adapters.FirstOrDefault(a => a.ToString() == comboText);
            if (adapter?.AdapterId == p.SelectedAdapterId) { idx = i; break; }
        }
        _gpuCombo.SelectedIndex = idx;
        SetStatus(p.StatusMessage);
    }

    private void BrowseForExecutable()
    {
        using var dlg = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _pathBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
            _nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void AddRunningGameToProfiles()
    {
        var existingProfiles = new HashSet<string>(
            _data.Profiles.Select(profile => NormalizeExecutablePath(profile.ExePath)),
            StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, RunningGameCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) ||
                        !string.Equals(Path.GetExtension(executablePath), ".exe",
                            StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(executablePath))
                        continue;

                    executablePath = Path.GetFullPath(executablePath);
                    if (existingProfiles.Contains(NormalizeExecutablePath(executablePath))) continue;
                    string title = process.MainWindowTitle;
                    string name = string.IsNullOrWhiteSpace(title) ? process.ProcessName : title.Trim();
                    candidates.TryAdd(executablePath,
                        new RunningGameCandidate(name, executablePath));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or NotSupportedException or
                                           UnauthorizedAccessException)
                {
                    // Ignore protected or exiting processes; they cannot be safely added by path.
                }
            }
        }

        if (candidates.Count == 0)
        {
            SetStatus("No unprofiled, visible running games were found.");
            return;
        }

        using var picker = new Form
        {
            Text = "Add a running game",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = false,
            MinimumSize = new Size(680, 360),
            Size = new Size(820, 500)
        };
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false
        };
        list.Columns.Add("Running application", 220);
        list.Columns.Add("Executable path", 540);
        foreach (var candidate in candidates.Values.OrderBy(item => item.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new ListViewItem(candidate.Name);
            row.SubItems.Add(candidate.ExePath);
            row.Tag = candidate;
            list.Items.Add(row);
        }
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count > 0) picker.DialogResult = DialogResult.OK;
        };
        var addButton = new Button
        {
            Text = "Add selected game",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(6)
        };
        list.SelectedIndexChanged += (_, _) => addButton.Enabled = list.SelectedItems.Count == 1;
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(6)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(6)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(addButton);
        picker.Controls.Add(list);
        picker.Controls.Add(buttons);
        picker.AcceptButton = addButton;
        picker.CancelButton = cancelButton;

        if (picker.ShowDialog(this) != DialogResult.OK || list.SelectedItems.Count != 1) return;
        var selected = (RunningGameCandidate)list.SelectedItems[0].Tag;
        var profile = new GameProfile
        {
            Name = selected.Name,
            ExePath = selected.ExePath,
            SelectedAdapterId = null,
            Status = PreferenceStatus.Configured,
            StatusMessage = "Added from the running-game list. GPU preference has not been changed."
        };
        _data.Profiles.Add(profile);
        if (!SaveProfiles())
        {
            _data.Profiles.Remove(profile);
            return;
        }
        RefreshGameList();
        _gameList.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, profile))?.Selected = true;
        _nameBox.Text = profile.Name;
        _pathBox.Text = profile.ExePath;
        _gpuCombo.SelectedIndex = 0;
        SetStatus($"Added '{profile.Name}' to the profile list. Choose a GPU and apply when ready.");
    }

    private static string NormalizeExecutablePath(string path)
    {
        try { return Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return path ?? ""; }
    }

    private void AddOrUpdateProfile()
    {
        string exe = _pathBox.Text.Trim();
        if (exe.Length == 0 || !Path.IsPathRooted(exe))
        {
            SetStatus("Browse for an executable (full absolute path) first.", error: true);
            return;
        }
        string adapterId = null;
        if (_gpuCombo.SelectedIndex > 0)
        {
            var comboText = (string)_gpuCombo.SelectedItem;
            adapterId = _adapters.First(a => a.ToString() == comboText).AdapterId;
        }
        string name = string.IsNullOrWhiteSpace(_nameBox.Text) ? Path.GetFileNameWithoutExtension(exe) : _nameBox.Text.Trim();

        var existing = _data.Profiles.FirstOrDefault(
            p => string.Equals(p.ExePath, exe, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _data.Profiles.Add(new GameProfile { Name = name, ExePath = exe, SelectedAdapterId = adapterId });
        }
        else
        {
            existing.Name = name;
            existing.SelectedAdapterId = adapterId;
        }
        SaveProfiles();
        RefreshGameList();
    }

    private void RemoveProfile()
    {
        if (_gameList.SelectedItems.Count == 0) return;
        var p = (GameProfile)_gameList.SelectedItems[0].Tag;
        _data.Profiles.Remove(p);
        SaveProfiles();
        RefreshGameList();
        SetStatus($"Removed profile '{p.Name}' (Windows registry entry untouched).");
    }

    private bool SaveProfiles()
    {
        try
        {
            _store.Save(_data);
            SetStatus($"Profiles saved to {_store.StorePath}.");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save profiles: {ex.Message}", error: true);
            return false;
        }
    }

    // ---------- Windows preference operations ----------

    private GameProfile SelectedProfile() =>
        _gameList.SelectedItems.Count == 0 ? null : (GameProfile)_gameList.SelectedItems[0].Tag;

    private void ApplyPreference()
    {
        var p = SelectedProfile();
        if (p is null) { SetStatus("Select a game profile first.", error: true); return; }
        if (p.SelectedAdapterId is null)
        {
            SetStatus("Profile has no GPU selected; pick an adapter before applying.", error: true);
            return;
        }
        var result = _preferences.ApplyPreference(p.ExePath, p.SelectedAdapterId);
        if (result.Success)
        {
            p.Status = PreferenceStatus.Applied;
            p.LastAppliedAdapterId = WindowsGpuPreferenceService.ToWindowsAdapterId(p.SelectedAdapterId);
            p.LastAppliedUtc = DateTime.UtcNow;
            p.StatusMessage = result.Message + " Windows GPU preference verified.";
            PublishPreferenceApplied(p);
        }
        else
        {
            p.Status = PreferenceStatus.Error;
            p.StatusMessage = result.Message;
        }
        SaveProfiles();
        ReloadWindowsApps();
        SyncProfileRegistryStatus();
        RefreshGameList();
        SetStatus(p.StatusMessage, error: !result.Success);
    }

    private void RemoveWindowsEntry()
    {
        var p = SelectedProfile();
        if (p is null) { SetStatus("Select a game profile first.", error: true); return; }
        var result = _preferences.RemoveEntry(p.ExePath);
        if (result.Success)
        {
            SyncProfileRegistryStatus();
            ReloadWindowsApps();
            SaveProfiles();
            RefreshGameList();
            PublishSystemDefault(p);
        }
        SetStatus(result.Message, error: !result.Success);
    }

    private void RestoreLastBackup()
    {
        var latest = _backups.ListBackups().FirstOrDefault();
        if (latest is null) { SetStatus("No registry backups recorded yet.", error: true); return; }
        var result = _preferences.RestoreBackup(latest);
        SyncProfileRegistryStatus();
        RefreshGameList();
        SetStatus($"{result.Message} (backup from {latest.TimestampUtc:u}, reason: {latest.Reason})",
            error: !result.Success);
    }

    private readonly GpuStatePublisher _gpuPublisher = new();

    /// <summary>Preference-applied event (separate from runtime results): reports the
    /// verified registry write only — never claims the game runs on that GPU.</summary>
    private async void PublishPreferenceApplied(GameProfile profile)
    {
        try
        {
            var adapter = _adapters.FirstOrDefault(a =>
                a.AdapterId == profile.SelectedAdapterId);
            var payload = new GpuPreferencePayload
            {
                SchemaVersion = GpuPreferenceContract.CurrentSchemaVersion,
                EventId = Guid.NewGuid().ToString("N"),
                ExePath = profile.ExePath,
                DisplayName = profile.Name,
                AdapterId = WindowsGpuPreferenceService.ToWindowsAdapterId(profile.SelectedAdapterId),
                AdapterName = adapter?.DisplayName ?? profile.SelectedAdapterId,
                AppliedUtc = DateTimeOffset.UtcNow
            };
            var publish = await _gpuPublisher.PublishPreferenceAsync(payload).ConfigureAwait(true);
            SetStatus(publish.Message);
        }
        catch (Exception ex)
        {
            SetStatus($"Preference-event publish failed: {ex.Message}", error: true);
        }
    }

    private async void PublishSystemDefault(GameProfile profile)
    {
        try
        {
            var payload = new GpuPreferencePayload
            {
                SchemaVersion = GpuPreferenceContract.CurrentSchemaVersion,
                EventId = Guid.NewGuid().ToString("N"),
                ExePath = profile.ExePath,
                DisplayName = profile.Name,
                IsSystemDefault = true,
                AppliedUtc = DateTimeOffset.UtcNow
            };
            var publish = await _gpuPublisher.PublishPreferenceAsync(payload).ConfigureAwait(true);
            SetStatus(publish.Message);
        }
        catch (Exception ex)
        {
            SetStatus($"System-default event publish failed: {ex.Message}", error: true);
        }
    }

    private void StartInventoryPublisher()
    {
        if (_inventoryTimer != null) return;
        _inventoryTimer = new System.Threading.Timer(_ => PublishAdapterInventorySnapshot(),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void PublishAdapterInventorySnapshot()
    {
        try
        {
            var adapters = _discovery.DiscoverAdapters();
            var resolver = DxgiAdapterLuidResolver.Create(adapters, out _);
            var telemetry = _telemetry.Sample();
            var payload = new GpuAdapterInventoryPayload
            {
                SchemaVersion = GpuAdapterInventoryContract.CurrentSchemaVersion,
                EventId = Guid.NewGuid().ToString("N"),
                CapturedUtc = DateTimeOffset.UtcNow,
                Adapters = adapters.Select(adapter =>
                {
                    var sample = GpuTelemetryService.ForAdapter(telemetry, adapter);
                    return new GpuAdapterInventoryEntry
                    {
                        Name = adapter.DisplayName ?? "",
                        AdapterId = adapter.AdapterId ?? "",
                        PciBusId = adapter.PciBusId ?? "",
                        DxgiLuids = resolver.ResolvedByLuid
                            .Where(pair => ReferenceEquals(pair.Value, adapter))
                            .Select(pair => pair.Key).OrderBy(luid => luid, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        UtilizationPercent = sample?.UtilizationPercent,
                        MemoryUsedMiB = sample?.MemoryUsedMiB,
                        MemoryTotalMiB = sample?.MemoryTotalMiB
                    };
                }).ToList()
            };
            PublishAdapterInventory(payload);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(() => SetStatus($"GPU inventory publish failed: {ex.Message}", error: true));
        }
    }

    private async void PublishAdapterInventory(GpuAdapterInventoryPayload payload)
    {
        var result = await _gpuPublisher.PublishInventoryAsync(payload).ConfigureAwait(false);
        if (result.Success) return;
        if (!IsDisposed && IsHandleCreated)
            BeginInvoke(() => SetStatus(result.Message, error: true));
    }

    private void SetStatus(string text, bool error = false)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = error ? Color.Firebrick : Color.Black;
    }

    private sealed record RunningGameCandidate(string Name, string ExePath);
}
