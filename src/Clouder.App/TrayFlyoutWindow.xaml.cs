using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder_App;

/// <summary>
/// The panel that drops out of the tray icon: pooled storage at a glance, how much is
/// in sync, what moved recently, and the handful of actions worth reaching without
/// opening the full window.
///
/// Kept alive for the life of the app and shown/hidden rather than recreated, so a
/// click feels instant and there's no window churn.
/// </summary>
public sealed partial class TrayFlyoutWindow : Window
{
    private const int WidthDip = 380;
    private const int HeightDip = 470;
    private const int EdgeMarginDip = 12;

    private const string StatusGood = "#0ca30c";
    private const string StatusCritical = "#d03b3b";
    private const string DirectionUp = "#2a78d6";
    private const string DirectionDown = "#1baf7a";

    private readonly IntPtr _hwnd;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);

        // A flyout shouldn't appear in the taskbar or alt-tab.
        AppWindow.IsShownInSwitchers = false;

        // Clicking anywhere else dismisses it, the way a real flyout behaves.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                AppWindow.Hide();
                LastHiddenUtc = DateTime.UtcNow;
            }
        };
    }

    /// <summary>
    /// When the panel last dismissed itself. Clicking the tray icon while the panel is
    /// open deactivates it *before* the click arrives, so without this the panel would
    /// close and immediately reopen instead of toggling shut.
    /// </summary>
    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Positions the panel near the tray and shows it, refreshing its contents first.
    /// </summary>
    public async Task ShowNearAsync(int cursorX, int cursorY)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to refresh the tray panel", ex);
        }

        PositionNear(cursorX, cursorY);
        AppWindow.Show();

        // Activating is what lets the deactivation handler dismiss it later.
        Activate();
    }

    public void Dismiss()
    {
        AppWindow.Hide();
        LastHiddenUtc = DateTime.UtcNow;
    }

    public bool IsOpen => AppWindow.IsVisible;

    // ── Placement ───────────────────────────────────────────────────────

    private void PositionNear(int cursorX, int cursorY)
    {
        // Everything below is in physical pixels; the sizes above are in DIPs.
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)(WidthDip * scale);
        int height = (int)(HeightDip * scale);
        int margin = (int)(EdgeMarginDip * scale);

        // Anchor to the work area of whichever display the tray is on, so the panel
        // lands beside the taskbar rather than under it — and works on a second
        // monitor or a taskbar that isn't at the bottom.
        var area = DisplayArea.GetFromPoint(new PointInt32(cursorX, cursorY), DisplayAreaFallback.Nearest)
                   ?? DisplayArea.Primary;
        var work = area.WorkArea;

        int x = Math.Clamp(cursorX - width / 2, work.X + margin, work.X + work.Width - width - margin);

        // Above the cursor when the taskbar is at the bottom, below it when at the top.
        bool trayAtBottom = cursorY > work.Y + work.Height / 2;
        int y = trayAtBottom
            ? work.Y + work.Height - height - margin
            : work.Y + margin;

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── Contents ────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var accounts = await App.Store.GetAllAccountsAsync();
        var pools = await App.Store.GetAllPoolsAsync();

        UpdateStorageRing(accounts);
        await UpdateSyncRingAsync(pools);
        await UpdateRecentAsync();
        UpdateStatus(accounts);
        UpdatePauseButton();
    }

    private void UpdateStorageRing(IReadOnlyList<ProviderAccount> accounts)
    {
        long total = 0, used = 0;
        foreach (var a in accounts)
        {
            if (a.Quota == null) continue;
            total += a.Quota.TotalBytes;
            used += a.Quota.UsedBytes;
        }

        if (total > 0)
        {
            double pct = (double)used / total * 100.0;
            StorageRing.Value = pct;
            StoragePct.Text = $"{pct:F0}%";
            StorageText.Text = $"{FormatBytes(used)} / {FormatBytes(total)}";
        }
        else
        {
            StorageRing.Value = 0;
            StoragePct.Text = "—";
            StorageText.Text = accounts.Count == 0 ? "No accounts" : "No quota data";
        }
    }

    private async Task UpdateSyncRingAsync(IReadOnlyList<StoragePool> pools)
    {
        int total = 0, synced = 0;

        foreach (var pool in pools)
        {
            var items = await App.Store.GetItemsByIdPrefixAsync(pool.PoolId + "|");
            foreach (var item in items.Where(i => i.Type == CloudItemType.File))
            {
                total++;
                if (item.SyncState == Clouder.Core.Models.SyncState.Synced) synced++;
            }
        }

        if (total > 0)
        {
            double pct = (double)synced / total * 100.0;
            SyncRing.Value = pct;
            SyncPct.Text = $"{pct:F0}%";
            SyncText.Text = synced == total
                ? $"{total:N0} file(s)"
                : $"{synced:N0} of {total:N0} files";
        }
        else
        {
            SyncRing.Value = 100;
            SyncPct.Text = "—";
            SyncText.Text = "Nothing synced yet";
        }
    }

    private async Task UpdateRecentAsync()
    {
        var recent = await App.Store.GetRecentTransfersAsync(limit: 20);
        var accounts = await App.Store.GetAllAccountsAsync();
        var names = accounts.ToDictionary(a => a.AccountId, a => a.DisplayName);

        EmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RecentList.ItemsSource = recent.Select(t =>
        {
            bool failed = t.Outcome == TransferOutcome.Failed;
            bool isDownload = t.Kind is TransferKind.Download or TransferKind.Placeholder;

            var verb = t.Kind switch
            {
                TransferKind.Upload => failed ? "Upload failed" : "Uploaded",
                TransferKind.Download => failed ? "Download failed" : "Downloaded",
                TransferKind.Delete => "Removed",
                TransferKind.Placeholder => "Available on demand",
                TransferKind.Move => "Moved",
                _ => "Synced"
            };

            var where = t.IsStriped
                ? $"{t.ChunkCount} chunks"
                : t.AccountId != null ? names.GetValueOrDefault(t.AccountId, "") : "";

            var detail = string.IsNullOrEmpty(where)
                ? $"{verb} {FormatTimeAgo(t.TimestampUtc)}"
                : $"{verb} · {where} · {FormatTimeAgo(t.TimestampUtc)}";

            return new
            {
                t.FileName,
                Detail = detail,
                MediaGlyph = MediaKindClassifier.Glyph(MediaKindClassifier.Classify(t.FileName)),
                DirectionGlyph = isDownload ? "" : t.Kind == TransferKind.Delete ? "" : "",
                DirectionBrush = Brush(isDownload ? DirectionDown : DirectionUp),
                StatusGlyph = failed ? "" : "",
                StatusBrush = Brush(failed ? StatusCritical : StatusGood)
            };
        }).ToList();
    }

    private void UpdateStatus(IReadOnlyList<ProviderAccount> accounts)
    {
        bool paused = App.SyncService?.Paused ?? false;

        int failedAccounts = accounts.Count(a =>
            App.Connection?.GetState(a.AccountId) is ConnectionState.Failed or ConnectionState.NeedsCredentials);

        if (paused)
        {
            FooterIcon.Glyph = "";                       // pause
            FooterIcon.Foreground = Brush(StatusCritical);
            FooterText.Text = "Sync paused";
            StatusLine.Text = "Paused";
        }
        else if (failedAccounts > 0)
        {
            FooterIcon.Glyph = "";                       // warning
            FooterIcon.Foreground = Brush(StatusCritical);
            FooterText.Text = failedAccounts == 1
                ? "1 account needs attention"
                : $"{failedAccounts} accounts need attention";
            StatusLine.Text = "Action needed";
        }
        else
        {
            FooterIcon.Glyph = "";                       // check
            FooterIcon.Foreground = Brush(StatusGood);
            FooterText.Text = "Up to date";
            StatusLine.Text = accounts.Count == 1 ? "1 account" : $"{accounts.Count} accounts";
        }
    }

    private void UpdatePauseButton()
    {
        bool paused = App.SyncService?.Paused ?? false;
        PauseIcon.Glyph = paused ? "" : "";   // play : pause
        ToolTipService.SetToolTip(PauseBtn, paused ? "Resume syncing" : "Pause syncing");
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private async void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        if (App.SyncService == null) return;

        bool paused = !App.SyncService.Paused;
        App.SyncService.Paused = paused;
        if (App.RemoteSync != null) App.RemoteSync.Paused = paused;
        ClouderLog.Info(paused ? "Sync paused from the tray panel" : "Sync resumed from the tray panel");

        var accounts = await App.Store.GetAllAccountsAsync();
        UpdateStatus(accounts);
        UpdatePauseButton();
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        App.RequestSyncNow();
        FooterIcon.Glyph = "";
        FooterText.Text = "Syncing…";
        StatusLine.Text = "Syncing";
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.ShowMainWindow();
    }

    private void OpenTransfers_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.ShowMainWindow("transfers");
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.ShowMainWindow("settings");
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.RequestExit();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16)));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:F0} {units[i]}" : $"{size:F1} {units[i]}";
    }

    private static string FormatTimeAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }
}
