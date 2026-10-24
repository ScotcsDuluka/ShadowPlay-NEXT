using System.Diagnostics;
using System.Text.Json;
using NvGraphics.Models;

namespace NvGraphics;

public sealed partial class NvGraphicsForm
{
    private sealed record InstalledStoreApp(string Name, string AppId);

    // Get-StartApps returns installed Start-menu applications, including packaged
    // Microsoft Store apps with AUMIDs. Let users enter an AUMID manually if absent.
    // Nothing is installed or launched by this discovery.
    private static async Task<IReadOnlyList<InstalledStoreApp>> FindStoreAppsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "Get-StartApps | Where-Object { $_.AppID -like '*!*' } | " +
            "Select-Object Name,AppID | ConvertTo-Json -Compress -Depth 3");
        try
        {
            if (!process.Start()) throw new IOException("Could not start Windows app discovery.");
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(error)
                    ? "Windows app discovery failed." : error.Trim());
            if (string.IsNullOrWhiteSpace(output)) return Array.Empty<InstalledStoreApp>();
            using var doc = JsonDocument.Parse(output);
            var entries = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };
            return entries
                .Where(e => e.ValueKind == JsonValueKind.Object &&
                            e.TryGetProperty("AppID", out var a) &&
                            a.ValueKind == JsonValueKind.String &&
                            a.GetString() is { Length: > 2 } id && id.Contains('!'))
                .Select(e => new InstalledStoreApp(
                    e.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                    e.GetProperty("AppID").GetString() ?? ""))
                .DistinctBy(e => e.AppId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new TimeoutException("Windows app discovery timed out.");
        }
    }

    private async Task AddStoreAppAsync()
    {
        IReadOnlyList<InstalledStoreApp> installed = Array.Empty<InstalledStoreApp>();
        string discoveryError = "";
        try { installed = await FindStoreAppsAsync(); }
        catch (Exception ex) { discoveryError = ex.Message; }
        if (IsDisposed) return;
        using var picker = new Form
        {
            Text = "Microsoft Store / packaged applications",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(840, 570), MinimumSize = new Size(700, 420),
            ShowInTaskbar = false
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        var headline = new Label
        {
            Dock = DockStyle.Fill,
            Text = string.IsNullOrWhiteSpace(discoveryError)
                ? "Choose an installed packaged app or enter its AppUserModelID (AUMID)."
                : "Windows app discovery unavailable: " + discoveryError +
                  "\nYou can still paste an AppUserModelID below.",
            AutoEllipsis = true
        };
        layout.Controls.Add(headline, 0, 0);
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search installed apps" };
        layout.Controls.Add(search, 0, 1);
        var list = new ListView
        {
            Dock = DockStyle.Fill, FullRowSelect = true, MultiSelect = false,
            View = View.Details, HideSelection = false
        };
        list.Columns.Add("Application", 265);
        list.Columns.Add("AppUserModelID", 500);
        void Filter()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var app in installed)
            {
                if (!string.IsNullOrWhiteSpace(search.Text) &&
                    !app.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) &&
                    !app.AppId.Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                    continue;
                var item = new ListViewItem(app.Name);
                item.SubItems.Add(app.AppId);
                item.Tag = app;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
        search.TextChanged += (_, _) => Filter();
        Filter();
        layout.Controls.Add(list, 0, 2);
        var manualPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        manualPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        manualPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        manualPanel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = "Selected or manually entered AppUserModelID:"
        }, 0, 0);
        var manual = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "PackageFamilyName!AppId" };
        manualPanel.Controls.Add(manual, 0, 1);
        layout.Controls.Add(manualPanel, 0, 3);
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count == 1)
                manual.Text = ((InstalledStoreApp)list.SelectedItems[0].Tag).AppId;
        };
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count == 1) picker.DialogResult = DialogResult.OK;
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        var add = new Button { Text = "Add application", Width = 155, DialogResult = DialogResult.OK };
        actions.Controls.Add(cancel);
        actions.Controls.Add(add);
        layout.Controls.Add(actions, 0, 4);
        picker.Controls.Add(layout);
        picker.AcceptButton = add;
        picker.CancelButton = cancel;
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        var appId = manual.Text.Trim();
        if (appId.Length is < 3 or > 512 || Path.IsPathRooted(appId) ||
            !appId.Contains('!') || appId.Any(char.IsWhiteSpace))
        {
            SetStatus("Invalid AppUserModelID. Expected PackageFamilyName!AppId.", true);
            return;
        }
        var match = installed.FirstOrDefault(e =>
            string.Equals(e.AppId, appId, StringComparison.OrdinalIgnoreCase));
        var local = _data.Profiles.FirstOrDefault(p =>
            string.Equals(p.ExePath, appId, StringComparison.OrdinalIgnoreCase));
        if (local is null)
        {
            local = new GameProfile
            {
                Name = match?.Name ?? WindowsAppDisplayName(appId), ExePath = appId,
                Status = PreferenceStatus.Configured,
                StatusMessage = "Packaged app added. Choose a GPU and save to Windows."
            };
            if (!UpdateLocalProfiles(latest =>
            {
                if (latest.Profiles.Any(p => string.Equals(p.ExePath,
                        appId, StringComparison.OrdinalIgnoreCase)))
                    return false;
                latest.Profiles.Add(local);
                return true;
            })) return;
        }
        _searchBox.Clear();
        RefreshGameList();
        foreach (ListViewItem item in _gameList.Items)
            if (item.Tag is GameProfile p &&
                string.Equals(p.ExePath, appId, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.EnsureVisible();
                break;
            }
        SetStatus("App added. Select a Windows GPU preference and Save.");
    }
}
