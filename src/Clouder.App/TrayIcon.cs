using System.Drawing;
using System.Windows.Forms;

namespace Clouder_App;

/// <summary>
/// System tray icon running on a dedicated STA thread.
/// WinUI 3 uses its own thread; WinForms needs a separate message pump.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _pauseItem;
    private Thread? _thread;
    private bool _paused;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;
    public event Action? SyncRequested;
    public event Action<bool>? PauseChanged;

    public bool IsPaused => _paused;

    public void Start()
    {
        _thread = new Thread(RunTrayThread);
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Name = "ClouderTray";
        _thread.Start();
    }

    public void UpdateStatus(int poolCount, bool syncing)
    {
        if (_icon == null) return;
        var text = syncing ? "Clouder - Syncing..." : $"Clouder - {poolCount} pool(s) active";
        if (text.Length > 63) text = text[..63]; // NotifyIcon.Text max 63 chars
        try { _icon.Text = text; } catch { }
    }

    public void Dispose()
    {
        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        System.Windows.Forms.Application.ExitThread();
    }

    private void RunTrayThread()
    {
        System.Windows.Forms.Application.EnableVisualStyles();

        _pauseItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => TogglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Clouder", null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sync Now", null, (_, _) => SyncRequested?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = CreateCloudIcon(),
            Text = "Clouder",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        System.Windows.Forms.Application.Run();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_pauseItem != null)
            _pauseItem.Text = _paused ? "Resume Sync" : "Pause Sync";
        if (_icon != null)
            _icon.Text = _paused ? "Clouder - Paused" : "Clouder";
        PauseChanged?.Invoke(_paused);
    }

    private static Icon CreateCloudIcon()
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
}
