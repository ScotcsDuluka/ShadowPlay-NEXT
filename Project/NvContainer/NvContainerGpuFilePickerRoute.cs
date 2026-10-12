using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class NvContainerGpuFilePickerRoute
{
    private static int _browseInProgress;

    internal static (int Status, object Response) BrowseExecutable(Func<string> selectFile = null)
    {
        if (Interlocked.CompareExchange(ref _browseInProgress, 1, 0) != 0)
            return (409, new { error = "An executable picker is already open." });

        try
        {
            string selectedPath = null;
            Exception selectionError = null;

            if (selectFile is not null)
            {
                try { selectedPath = selectFile(); }
                catch (Exception ex) { selectionError = ex; }
            }
            else
            {
                var foregroundWindow = GetForegroundWindow();
                IWin32Window owner = foregroundWindow != IntPtr.Zero && IsWindow(foregroundWindow)
                    ? new DialogOwner(foregroundWindow)
                    : null;
                var dialogThread = new Thread(() =>
                {
                    try
                    {
                        using var dialog = new OpenFileDialog
                        {
                            Title = "Select game executable",
                            Filter = "Executable files (*.exe)|*.exe",
                            CheckFileExists = true,
                            Multiselect = false
                        };
                        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                        if (result == DialogResult.OK)
                            selectedPath = dialog.FileName;
                    }
                    catch (Exception ex)
                    {
                        selectionError = ex;
                    }
                });
                dialogThread.SetApartmentState(ApartmentState.STA);
                dialogThread.Start();
                dialogThread.Join();
            }

            if (selectionError is not null)
                return (500, new { error = "Could not open executable picker: " + selectionError.Message });
            if (string.IsNullOrWhiteSpace(selectedPath))
                return (200, new { cancelled = true });

            try
            {
                if (!Path.IsPathRooted(selectedPath) ||
                    !string.Equals(Path.GetExtension(selectedPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(selectedPath))
                    return (400, new { error = "The selected path must be an existing executable." });
                return (200, new { cancelled = false, exePath = Path.GetFullPath(selectedPath) });
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (400, new { error = "The selected executable path is invalid." });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _browseInProgress, 0);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private sealed class DialogOwner : IWin32Window
    {
        internal DialogOwner(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}
