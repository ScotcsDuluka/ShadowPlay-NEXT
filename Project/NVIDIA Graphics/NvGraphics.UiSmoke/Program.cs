using System.Drawing;
using System.Windows.Forms;
using NvGraphics;

[STAThread]
static int RunUiSmoke(string[] args)
{
    Application.EnableVisualStyles();
    var checks = 0;
    var errors = new List<string>();
    void Check(bool condition, string message)
    {
        checks++;
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) errors.Add(message);
    }

    // Show the real WinForms controls in dedicated smoke mode. The Load/Shown
    // runtime wiring is disabled so no Registry/GPU request or heartbeat occurs.
    using var form = new NvGraphicsForm(59012, false, true);
    form.Show();
    Application.DoEvents();
    var sizes = new[] { new Size(1180, 930), new Size(1020, 810),
                        new Size(1400, 1000), new Size(1100, 850) };
    foreach (var size in sizes)
    {
        form.ClientSize = size;
        form.PerformLayout();
        var layout = form.Controls.OfType<TableLayoutPanel>().Single();
        layout.PerformLayout();
        var apps = layout.GetControlFromPosition(0, 3);
        var detail = layout.GetControlFromPosition(0, 4);
        var inventory = layout.GetControlFromPosition(0, 5);
        Check(apps.Width > 0 && apps.Height > 0 &&
            detail.Width > 0 && inventory.Height > 0,
            $"{size.Width}x{size.Height}: all content sections have space");
        var primaryButtons = detail.Controls.OfType<Button>()
            .Where(c => c.Visible).ToArray();
        Check(primaryButtons.All(c => c.Bounds.Left >= 0 &&
            c.Bounds.Right <= detail.ClientSize.Width + 2 &&
            c.Bounds.Top >= 0 && c.Bounds.Bottom <= detail.ClientSize.Height + 2),
            $"{size.Width}x{size.Height}: all visible action buttons fit");
        Check(primaryButtons.Where(c => c.Enabled).All(c => c.Height >= 25),
            $"{size.Width}x{size.Height}: interactive buttons have usable height");
        var search = layout.GetControlFromPosition(0, 2);
        search.PerformLayout();
        var reload = search.Controls.OfType<Button>().Single();
        Check(reload.Right <= search.ClientSize.Width + 2 && reload.Left >= 0,
            $"{size.Width}x{size.Height}: Refresh button fits");
    }
    // Draw the real WinForms control tree without showing it or changing GPU settings.
    form.ClientSize = new Size(1180, 930);
    form.PerformLayout();
    form.Controls.OfType<TableLayoutPanel>().Single().PerformLayout();
    Application.DoEvents();
    using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
    {
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
        var screenshot = Path.Combine(AppContext.BaseDirectory, "NvGraphics-UI-1180x930.png");
        bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("UI_CAPTURE=" + screenshot);
    }
    Console.WriteLine($"UI layout checks: {checks-errors.Count}/{checks}");
    return errors.Count == 0 ? 0 : 1;
}

return RunUiSmoke(args);
