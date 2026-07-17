using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Clouder.Service;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _paused;

    public event Action? ExitRequested;
    public event Action? SyncRequested;

    public TrayIconManager()
    {
        _pauseItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => TogglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Clouder", null, (_, _) => OpenApp());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sync Now", null, (_, _) => SyncRequested?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Clouder",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenApp();
    }

    public void UpdateStatus(int poolCount, bool syncing)
    {
        var status = syncing ? "Syncing..." : $"{poolCount} pool(s) active";
        _icon.Text = $"Clouder - {status}";
    }

    public bool IsPaused => _paused;

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Text = _paused ? "Resume Sync" : "Pause Sync";
        _icon.Text = _paused ? "Clouder - Paused" : "Clouder";
    }

    private static void OpenApp()
    {
        try
        {
            var appPath = Path.Combine(AppContext.BaseDirectory, "Clouder.App.exe");
            if (File.Exists(appPath))
                Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true });
        }
        catch { /* App might not be available */ }
    }

    private static Icon CreateIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(0, 120, 215));
        g.FillEllipse(brush, 1, 5, 14, 9);
        g.FillEllipse(brush, 4, 2, 10, 8);
        g.FillEllipse(brush, 8, 3, 6, 6);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
