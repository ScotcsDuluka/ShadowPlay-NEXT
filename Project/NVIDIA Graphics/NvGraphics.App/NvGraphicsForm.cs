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
public sealed partial class NvGraphicsForm : Form
{
    private static readonly HttpClient ReadyClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly int _controlApiPort;
    private readonly bool _startHidden;
    private bool _uiSmokeOnly;
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
    private System.Windows.Forms.Timer _windowsRefreshTimer;
    private int _telemetryInFlight;
    private bool _loadingGpuChoice;
    private bool _gpuChoiceDirty;
    private string _selectedWindowsBaseline;
    private string _selectedWindowsBaselinePath;
    private DateTime _profileStoreLastWriteUtc;

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
            if (_uiSmokeOnly) return;
            ReloadAll();
            StartInventoryPublisher();
            StartWindowsPreferenceRefresh();
        };
        Shown += (_, _) =>
        {
            if (_uiSmokeOnly) return;
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
            _windowsRefreshTimer?.Dispose();
        };
    }

    // Dedicated UI smoke mode: real window painting without touching GPU/Registry
    // or claiming a second NvGraphics readiness PID to the Container.
    public NvGraphicsForm(int controlApiPort, bool startHidden, bool uiSmokeOnly)
        : this(controlApiPort, startHidden) => _uiSmokeOnly = uiSmokeOnly;

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

    private void BuildUi() => BuildGraphicsSettingsUi();

    // ---------- data loading ----------

    private void ReloadAll()
    {
        // Refresh is not an implicit Cancel: retain the in-progress GPU choice.
        var editingPath = SelectedProfile()?.ExePath;
        var pendingChoice = _gpuChoiceDirty ? SelectedGraphicsChoice() : null;
        var pendingRaw = _selectedWindowsBaseline;
        var pendingPath = _selectedWindowsBaselinePath;
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
        _profileStoreLastWriteUtc = StoreLastWriteUtc();

        _gpuCombo.Items.Clear();
        _gpuCombo.Items.Add(new GraphicsChoice("Let Windows decide", WindowsPolicy: 0));
        _gpuCombo.Items.Add(new GraphicsChoice("Power saving (Windows)", WindowsPolicy: 1));
        _gpuCombo.Items.Add(new GraphicsChoice("High performance (Windows)", WindowsPolicy: 2));
        foreach (var adapter in _adapters.Where(a => !string.IsNullOrEmpty(a.AdapterId)))
            _gpuCombo.Items.Add(new GraphicsChoice(
                "Specific GPU: " + adapter.DisplayName + "  [" + (adapter.PciBusId ?? adapter.AdapterId) + "]",
                AdapterId: adapter.AdapterId));
        _loadingGpuChoice = true;
        try { _gpuCombo.SelectedIndex = 0; }
        finally { _loadingGpuChoice = false; }

        // The list rebuilds against Windows (source of truth), then restores
        // only a pending selection for the same app without altering its baseline.
        _gpuChoiceDirty = false;
        RefreshAdapterList();
        SyncProfileRegistryStatus();
        RefreshGameList();
        if (pendingChoice is not null &&
            string.Equals(editingPath, SelectedProfile()?.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            var rebound = _gpuCombo.Items.Cast<GraphicsChoice>().FirstOrDefault(item =>
                string.Equals(item.AdapterId, pendingChoice.AdapterId, StringComparison.OrdinalIgnoreCase) &&
                item.WindowsPolicy == pendingChoice.WindowsPolicy);
            if (rebound is not null)
            {
                _loadingGpuChoice = true;
                try { _gpuCombo.SelectedItem = rebound; }
                finally { _loadingGpuChoice = false; }
                _gpuChoiceDirty = true;
                _selectedWindowsBaseline = pendingRaw;
                _selectedWindowsBaselinePath = pendingPath;
                _applyButton.Enabled = true;
                SetStatus("Refresh complete; your unsaved GPU choice is preserved.");
            }
            else SetStatus("Previously chosen adapter is unavailable; review before saving.", true);
        }
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

    private async void RefreshTelemetry()
    {
        if (Interlocked.Exchange(ref _telemetryInFlight, 1) != 0) return;
        try
        {
            var snapshot = await Task.Run(() => _telemetry.Sample());
            if (IsDisposed || !IsHandleCreated) return;
            _lastSnapshot = snapshot;
            RefreshAdapterList();
            if (snapshot is null)
                SetStatus("Telemetry unavailable: " + _telemetry.LastError);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) SetStatus("Telemetry failed: " + ex.Message, error: true);
        }
        finally { Interlocked.Exchange(ref _telemetryInFlight, 0); }
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

    private DateTime StoreLastWriteUtc() =>
        File.Exists(_store.StorePath) ? File.GetLastWriteTimeUtc(_store.StorePath) : DateTime.MinValue;

    private void StartWindowsPreferenceRefresh()
    {
        if (_windowsRefreshTimer is not null) return;
        _windowsRefreshTimer = new System.Windows.Forms.Timer { Interval = 7000 };
        _windowsRefreshTimer.Tick += (_, _) =>
        {
            if (!Visible || IsDisposed || _gameList is null) return;
            try
            {
                var latest = _preferences.ListPreferences()
                    .Where(e => IsWindowsGraphicsApp(e.Name)).ToArray();
                var before = _windowsApps.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Name + "\0" + (e.Value as string ?? ""));
                var after = latest.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Name + "\0" + (e.Value as string ?? ""));
                var windowsChanged = !before.SequenceEqual(after, StringComparer.Ordinal);
                var diskStamp = StoreLastWriteUtc();
                var profilesChanged = diskStamp != _profileStoreLastWriteUtc;
                if (!windowsChanged && !profilesChanged) return;
                if (profilesChanged)
                {
                    _data = _store.Load();
                    _profileStoreLastWriteUtc = diskStamp;
                }
                _windowsApps = latest;
                SyncProfileRegistryStatus();
                RefreshGameList();
                if (_gpuChoiceDirty)
                    SetStatus("Unsaved choice preserved; external settings changed. Save will recheck Windows.");
            }
            catch (Exception ex) { SetStatus("Auto-refresh failed: " + ex.Message, true); }
        };
        _windowsRefreshTimer.Start();
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

    /// <summary>Windows Registry is authoritative; local profiles only provide extra metadata.</summary>
    private void SyncProfileRegistryStatus()
    {
        foreach (var p in _data.Profiles)
        {
            try
            {
                var raw = _preferences.FindValue(p.ExePath);
                if (raw is null)
                {
                    p.Status = PreferenceStatus.Configured;
                    p.StatusMessage = "Saved locally; Windows is using its default graphics selection.";
                    continue;
                }
                var specific = WindowsGpuPreferenceService.ParseSpecificAdapter(raw);
                var policy = WindowsGpuPreferenceService.ParseWindowsGpuPolicy(raw);
                p.SelectedAdapterId = AdapterIdForWindowsPreference(raw);
                p.LastAppliedAdapterId = specific;
                p.Status = PreferenceStatus.Applied;
                p.StatusMessage = specific is not null
                    ? "Windows Graphics uses " + ProfileAdapterMessage(specific)
                    : policy.HasValue ? "Windows Graphics policy = " +
                        (policy == 0 ? "Let Windows decide" : policy == 1 ? "Power saving" : "High performance")
                    : "Windows Graphics entry exists; GPU selection is not recognized.";
            }
            catch (Exception ex)
            {
                p.Status = PreferenceStatus.Error;
                p.StatusMessage = "Could not read Windows Graphics preference: " + ex.Message;
            }
        }
    }

    private string WindowsPreferenceLabel(string raw)
    {
        if (raw is null) return "Let Windows decide (not configured)";
        var id = WindowsGpuPreferenceService.ParseSpecificAdapter(raw);
        if (!string.IsNullOrWhiteSpace(id))
        {
            var adapter = _adapters.FirstOrDefault(a => string.Equals(
                WindowsGpuPreferenceService.ToWindowsAdapterId(a.AdapterId),
                id, StringComparison.OrdinalIgnoreCase));
            return adapter is null ? "Specific GPU (not detected): " + id
                                   : "Specific GPU: " + adapter.DisplayName +
                                     " [" + (adapter.PciBusId ?? id) + "]";
        }
        var mode = WindowsGpuPreferenceService.ParseWindowsGpuPolicy(raw);
        return mode switch
        {
            0 => "Let Windows decide",
            1 => "Power saving (Windows)",
            2 => "High performance (Windows)",
            _ => "Windows setting (unrecognized GPU policy)"
        };
    }

    private void RefreshGameList()
    {
        if (_gameList is null) return;
        var selectedPath = SelectedProfile()?.ExePath;
        var query = _searchBox?.Text?.Trim() ?? "";
        var pendingChoice = _gpuChoiceDirty ? SelectedGraphicsChoice() : null;
        var pendingBaseline = _selectedWindowsBaseline;
        var pendingBaselinePath = _selectedWindowsBaselinePath;
        var windows = _windowsApps.ToDictionary(entry => entry.Name,
            entry => entry.Value as string ?? "", StringComparer.OrdinalIgnoreCase);
        _windowsOnlyPaths.Clear();
        _gameList.BeginUpdate();
        try
        {
            _gameList.Items.Clear();
            foreach (var p in _data.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!SearchMatches(query, p.Name, p.ExePath)) continue;
                var inWindows = windows.TryGetValue(p.ExePath, out var raw);
                var item = new ListViewItem(p.Name);
                item.SubItems.Add(WindowsPreferenceLabel(inWindows ? raw : null));
                item.SubItems.Add(inWindows ? "Windows + local" : "Local (pending)");
                item.SubItems.Add(p.ExePath);
                item.ToolTipText = p.StatusMessage;
                item.Tag = p;
                _gameList.Items.Add(item);
            }
            foreach (var entry in _windowsApps.OrderBy(e => WindowsAppDisplayName(e.Name),
                         StringComparer.OrdinalIgnoreCase))
            {
                if (_data.Profiles.Any(p => string.Equals(p.ExePath, entry.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = WindowsAppDisplayName(entry.Name);
                if (!SearchMatches(query, name, entry.Name)) continue;
                var raw = entry.Value as string ?? "";
                var specific = WindowsGpuPreferenceService.ParseSpecificAdapter(raw);
                var profile = new GameProfile
                {
                    Name = name, ExePath = entry.Name,
                    SelectedAdapterId = AdapterIdForWindowsPreference(raw),
                    LastAppliedAdapterId = specific,
                    Status = PreferenceStatus.Applied,
                    StatusMessage = "Imported from Windows Graphics settings (not saved as local profile)."
                };
                _windowsOnlyPaths.Add(profile.ExePath);
                var item = new ListViewItem(name);
                item.SubItems.Add(WindowsPreferenceLabel(raw));
                item.SubItems.Add("Windows");
                item.SubItems.Add(entry.Name);
                item.Tag = profile;
                _gameList.Items.Add(item);
            }
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                foreach (ListViewItem item in _gameList.Items)
                {
                    if (item.Tag is GameProfile p && string.Equals(
                        p.ExePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                        break;
                    }
                }
            }
        }
        finally { _gameList.EndUpdate(); }
        if (pendingChoice is not null && string.Equals(
                selectedPath, SelectedProfile()?.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            _loadingGpuChoice = true;
            try { _gpuCombo.SelectedItem = pendingChoice; }
            finally { _loadingGpuChoice = false; }
            _gpuChoiceDirty = true;
            _selectedWindowsBaseline = pendingBaseline;
            _selectedWindowsBaselinePath = pendingBaselinePath;
            _applyButton.Enabled = true;
        }
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
        _removeEntryButton.Enabled = p is not null &&
            _windowsApps.Any(e => string.Equals(e.Name, p.ExePath, StringComparison.OrdinalIgnoreCase));
        _addUpdateButton.Enabled = p is not null && IsWindowsGraphicsApp(p.ExePath);
        if (p is null)
        {
            _gpuChoiceDirty = false;
            _selectedWindowsBaselinePath = null;
            _selectedWindowsBaseline = null;
            _selectionHint.Text = "Select an app above, or browse for an executable.";
            _nameBox.Text = "";
            _pathBox.Text = "";
            _gpuCombo.SelectedIndex = 0;
            return;
        }
        _selectionHint.Text = _windowsOnlyPaths.Contains(p.ExePath)
            ? "This app comes directly from Windows Graphics settings."
            : "Windows GPU settings take precedence over local profile metadata.";
        _nameBox.Text = p.Name;
        _pathBox.Text = p.ExePath;

        var hasRegistry = _windowsApps.FirstOrDefault(e =>
            string.Equals(e.Name, p.ExePath, StringComparison.OrdinalIgnoreCase));
        var raw = hasRegistry?.Value as string;
        _selectedWindowsBaseline = raw;
        _selectedWindowsBaselinePath = p.ExePath;
        _gpuChoiceDirty = false;
        var adapterId = raw is null ? p.SelectedAdapterId : AdapterIdForWindowsPreference(raw);
        var mode = raw is null ? 0 : WindowsGpuPreferenceService.ParseWindowsGpuPolicy(raw);
        var selected = 0;
        for (var i = 0; i < _gpuCombo.Items.Count; i++)
        {
            if (_gpuCombo.Items[i] is not GraphicsChoice choice) continue;
            if (adapterId is not null &&
                string.Equals(choice.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
            {
                selected = i;
                break;
            }
            if (adapterId is null && choice.AdapterId is null &&
                choice.WindowsPolicy == (mode ?? 0))
            {
                selected = i;
                break;
            }
        }
        // Never silently replace a Windows preference referencing an undetected GPU.
        var unknownSpecificAdapter = raw is not null &&
            !string.IsNullOrWhiteSpace(WindowsGpuPreferenceService.ParseSpecificAdapter(raw)) &&
            adapterId is null;
        _loadingGpuChoice = true;
        try { _gpuCombo.SelectedIndex = unknownSpecificAdapter ? -1 : selected; }
        finally { _loadingGpuChoice = false; }
        _applyButton.Enabled = !unknownSpecificAdapter;
        if (unknownSpecificAdapter)
            _selectionHint.Text = "Windows references an unavailable GPU. Select a detected GPU to change it.";
        SetStatus(p.StatusMessage);
    }

    private void BrowseForExecutable()
    {
        using var dlg = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _pathBox.Text = dlg.FileName;
        _nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        _gpuCombo.SelectedIndex = 0;
        // Staging a local entry is not a Windows preference write.
        AddOrUpdateProfile();
        _searchBox.Clear();
        RefreshGameList();
        foreach (ListViewItem item in _gameList.Items)
            if (item.Tag is GameProfile profile &&
                string.Equals(profile.ExePath, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.EnsureVisible();
                break;
            }
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
        if (!UpdateLocalProfiles(latest =>
        {
            if (latest.Profiles.Any(p => string.Equals(p.ExePath,
                profile.ExePath, StringComparison.OrdinalIgnoreCase)))
                return false;
            latest.Profiles.Add(profile);
            return true;
        }))
            return;
        _gameList.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => item.Tag is GameProfile p &&
                string.Equals(p.ExePath, profile.ExePath, StringComparison.OrdinalIgnoreCase))?.Selected = true;
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

    private bool UpdateLocalProfiles(Func<GameProfileStoreData, bool> edit)
    {
        try
        {
            _data = _store.Update(edit);
            _profileStoreLastWriteUtc = StoreLastWriteUtc();
            SyncProfileRegistryStatus();
            RefreshGameList();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Could not update local profiles: " + ex.Message, true);
            return false;
        }
    }

    private void AddOrUpdateProfile()
    {
        string exe = _pathBox.Text.Trim();
        if (exe.Length == 0 || !IsWindowsGraphicsApp(exe))
        {
            SetStatus("Browse for an executable (full absolute path) first.", error: true);
            return;
        }
        // The selector is a pending choice. Saving a local profile never writes Windows.
        var adapterId = SelectedGraphicsChoice()?.AdapterId;
        string name = string.IsNullOrWhiteSpace(_nameBox.Text) ? Path.GetFileNameWithoutExtension(exe) : _nameBox.Text.Trim();

        if (UpdateLocalProfiles(latest =>
        {
            var existing = latest.Profiles.FirstOrDefault(
                p => string.Equals(p.ExePath, exe, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                latest.Profiles.Add(new GameProfile { Name = name, ExePath = exe, SelectedAdapterId = adapterId });
            else
            {
                existing.Name = name;
                existing.SelectedAdapterId = adapterId;
            }
            return true;
        }))
            SetStatus("Local profile updated without overwriting other modules.");
    }

    private void RemoveProfile()
    {
        if (_gameList.SelectedItems.Count == 0) return;
        var p = (GameProfile)_gameList.SelectedItems[0].Tag;
        if (MessageBox.Show(this, "Forget the local profile for " + p.Name +
                "? The Windows Graphics preference will NOT be modified.",
                "Forget local profile", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (UpdateLocalProfiles(latest => latest.Profiles.RemoveAll(
                row => string.Equals(row.ExePath, p.ExePath,
                    StringComparison.OrdinalIgnoreCase)) > 0))
            SetStatus($"Removed profile '{p.Name}' (Windows registry entry untouched).");
    }

    private bool SaveProfiles()
    {
        try
        {
            _store.Save(_data);
            _profileStoreLastWriteUtc = StoreLastWriteUtc();
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
        var choice = SelectedGraphicsChoice();
        if (p is null || choice is null)
        {
            SetStatus("Select an app and a graphics preference first.", error: true);
            return;
        }
        if (choice.AdapterId is null && choice.WindowsPolicy is null)
        {
            SetStatus("Invalid GPU choice.", error: true);
            return;
        }
        // Detect external Windows Settings edits before writing a pending selection.
        try
        {
            var current = _preferences.FindValue(p.ExePath);
            if (!string.Equals(_selectedWindowsBaselinePath, p.ExePath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current, _selectedWindowsBaseline, StringComparison.Ordinal))
            {
                SetStatus("Windows Graphics changed this app externally. Refresh and review before saving.", true);
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Cannot verify current Windows setting: " + ex.Message, true);
            return;
        }
        // A single Save applies the selector straight to Windows, with a backup and read-back.
        var result = choice.AdapterId is not null
            ? _preferences.ApplyPreference(p.ExePath, choice.AdapterId)
            : _preferences.ApplyWindowsPolicy(p.ExePath, choice.WindowsPolicy.Value);
        if (!result.Success)
        {
            SetStatus(result.Message, error: true);
            return;
        }

        var local = _data.Profiles.FirstOrDefault(profile => string.Equals(
            profile.ExePath, p.ExePath, StringComparison.OrdinalIgnoreCase));
        var localSaved = local is null || UpdateLocalProfiles(latest =>
        {
            var current = latest.Profiles.FirstOrDefault(x =>
                string.Equals(x.ExePath, p.ExePath, StringComparison.OrdinalIgnoreCase));
            if (current is null) return false;
            current.SelectedAdapterId = choice.AdapterId;
            current.LastAppliedAdapterId = WindowsGpuPreferenceService.ParseSpecificAdapter(result.VerifiedValue);
            current.LastAppliedUtc = DateTime.UtcNow;
            current.Status = PreferenceStatus.Applied;
            current.StatusMessage = result.Message;
            return true;
        });
        // Always publish a successfully verified Windows write even if local profile sync failed.
        // Report the value Windows actually verified, not a previously cached profile.
        // Windows-only apps don't have an on-disk profile.
        p.SelectedAdapterId = choice.AdapterId;
        p.LastAppliedAdapterId = WindowsGpuPreferenceService.ParseSpecificAdapter(result.VerifiedValue);
        PublishPreferenceApplied(p, result.VerifiedValue);
        _gpuChoiceDirty = false;
        ReloadWindowsApps();
        SyncProfileRegistryStatus();
        RefreshGameList();
        SetStatus("Saved and verified in Windows Graphics: " + choice.Label +
            ". Restart the affected game to use the new preference." +
            (localSaved ? "" : " Local profile sync failed; refresh and retry."), !localSaved);
    }

    private void RemoveWindowsEntry()
    {
        var p = SelectedProfile();
        if (p is null) return;
        if (MessageBox.Show(this, "Reset the Windows Graphics entry for " + p.Name +
                "? Only GPU-selection overrides will be cleared; other graphics options stay intact.",
                "Reset Windows Graphics preference", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var result = _preferences.ResetGpuSelection(p.ExePath);
        if (!result.Success) { SetStatus(result.Message, true); return; }
        _gpuChoiceDirty = false;
        ReloadWindowsApps();
        SyncProfileRegistryStatus();
        RefreshGameList();
        UpdateLocalProfiles(latest =>
        {
            var local = latest.Profiles.FirstOrDefault(x => string.Equals(
                x.ExePath, p.ExePath, StringComparison.OrdinalIgnoreCase));
            if (local is null) return false;
            local.SelectedAdapterId = null;
            local.LastAppliedAdapterId = null;
            local.Status = PreferenceStatus.Configured;
            local.StatusMessage = "Windows uses its default GPU selection.";
            return true;
        });
        PublishSystemDefault(p);
        SetStatus("Windows GPU selection reset for " + p.Name + "; other graphics options preserved.");
    }

    private void RestoreLastBackup()
    {
        var selected = SelectedProfile();
        var latest = _backups.ListBackups().FirstOrDefault(record =>
            selected is null || string.Equals(record.ValueName, selected.ExePath,
                StringComparison.OrdinalIgnoreCase));
        if (latest is null)
        {
            SetStatus("No matching registry backup was found.", error: true);
            return;
        }
        if (MessageBox.Show(this, "Restore the previous Windows Graphics preference for " +
                latest.ValueName + "?", "Restore GPU preference",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var result = _preferences.RestoreBackup(latest);
        if (result.Success)
        {
            string restored;
            try { restored = _preferences.FindValue(latest.ValueName); }
            catch (Exception ex)
            {
                SetStatus("Restore succeeded but read-back failed: " + ex.Message, true);
                return;
            }
            var restoredProfile = selected ?? new GameProfile
            {
                ExePath = latest.ValueName,
                Name = WindowsAppDisplayName(latest.ValueName)
            };
            _gpuChoiceDirty = false;
            ReloadWindowsApps();
            SyncProfileRegistryStatus();
            RefreshGameList();
            // Publish only the verified restored Windows value, never a stale profile.
            if (restored is null) PublishSystemDefault(restoredProfile);
            else PublishPreferenceApplied(restoredProfile, restored);
        }
        SetStatus(result.Message, error: !result.Success);
    }

    private readonly GpuStatePublisher _gpuPublisher = new();

    /// <summary>Preference-applied event (separate from runtime results): reports the
    /// verified registry write only — never claims the game runs on that GPU.</summary>
    private async void PublishPreferenceApplied(GameProfile profile, string verifiedValue)
    {
        try
        {
            var windowsId = WindowsGpuPreferenceService.ParseSpecificAdapter(verifiedValue);
            var policy = WindowsGpuPreferenceService.ParseWindowsGpuPolicy(verifiedValue);
            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(WindowsGpuPreferenceService.ToWindowsAdapterId(a.AdapterId),
                    windowsId, StringComparison.OrdinalIgnoreCase));
            var payload = new GpuPreferencePayload
            {
                SchemaVersion = GpuPreferenceContract.CurrentSchemaVersion,
                EventId = Guid.NewGuid().ToString("N"),
                ExePath = profile.ExePath,
                DisplayName = profile.Name,
                IsSystemDefault = string.IsNullOrWhiteSpace(windowsId),
                WindowsGpuPolicy = string.IsNullOrWhiteSpace(windowsId) ? policy : null,
                AdapterId = windowsId ?? "",
                AdapterName = adapter?.DisplayName ?? "",
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
