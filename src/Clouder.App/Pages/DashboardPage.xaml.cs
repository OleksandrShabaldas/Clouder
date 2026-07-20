using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder_App.Pages;

public sealed partial class DashboardPage : Page
{
    /// <summary>
    /// Categorical palette: one fixed hue per account, assigned by position and never
    /// cycled, so an account keeps its colour as others come and go. Values are the
    /// validated reference palette — light and dark are the same eight hues stepped for
    /// their surface, not an automatic flip.
    /// </summary>
    private static readonly string[] SeriesLight =
        ["#2a78d6", "#1baf7a", "#eda100", "#008300", "#4a3aa7", "#e34948", "#e87ba4", "#eb6834"];

    private static readonly string[] SeriesDark =
        ["#3987e5", "#199e70", "#c98500", "#008300", "#9085e9", "#e66767", "#d55181", "#d95926"];

    // Status colours are reserved and never reused as a series hue.
    private const string StatusGood = "#0ca30c";
    private const string StatusCritical = "#d03b3b";

    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    private SolidColorBrush SeriesBrush(int index)
    {
        var palette = IsDark ? SeriesDark : SeriesLight;
        // Fixed order, never cycled: a ninth account folds onto the last slot rather
        // than generating a new hue, and is still distinguished by its direct label.
        var hex = palette[Math.Min(index, palette.Length - 1)];
        return new SolidColorBrush(ParseHex(hex));
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private async Task RefreshAsync()
    {
        try
        {
            var accounts = await App.Store.GetAllAccountsAsync();
            var pools = await App.Store.GetAllPoolsAsync();

            BuildHeadline(accounts, pools);
            BuildComposition(accounts);
            BuildAccounts(accounts);
            BuildPools(pools, accounts);
            await BuildActivityAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to refresh the dashboard", ex);
        }
    }

    // ── Headline figures ────────────────────────────────────────────────

    private void BuildHeadline(IReadOnlyList<ProviderAccount> accounts, IReadOnlyList<StoragePool> pools)
    {
        long total = 0, used = 0;
        foreach (var a in accounts)
        {
            if (a.Quota == null) continue;
            total += a.Quota.TotalBytes;
            used += a.Quota.UsedBytes;
        }

        var pct = total > 0 ? (double)used / total * 100.0 : 0;

        StatTotal.Text = total > 0 ? FormatBytes(total) : "—";
        StatTotalSub.Text = accounts.Count == 1 ? "across 1 account" : $"across {accounts.Count} accounts";

        StatUsed.Text = total > 0 ? FormatBytes(used) : "—";
        StatUsedSub.Text = total > 0 ? $"{pct:F1}% of pool" : "no quota data yet";

        StatFree.Text = total > 0 ? FormatBytes(total - used) : "—";
        StatFreeSub.Text = total > 0 ? $"{100 - pct:F1}% available" : "";

        StatFiles.Text = "—";
        StatFilesSub.Text = pools.Count == 1 ? "in 1 pool" : $"in {pools.Count} pools";
        _ = UpdateFileCountAsync(pools);
    }

    private async Task UpdateFileCountAsync(IReadOnlyList<StoragePool> pools)
    {
        try
        {
            int files = 0;
            foreach (var pool in pools)
            {
                var items = await App.Store.GetItemsByIdPrefixAsync(pool.PoolId + "|");
                files += items.Count(i => i.Type == CloudItemType.File);
            }
            StatFiles.Text = files.ToString("N0");
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not count synced files: {ex.Message}");
        }
    }

    // ── Composition bar: capacity contributed by each account ───────────

    private void BuildComposition(IReadOnlyList<ProviderAccount> accounts)
    {
        CompositionBar.Children.Clear();
        CompositionBar.ColumnDefinitions.Clear();

        var withQuota = accounts.Where(a => a.Quota is { TotalBytes: > 0 }).ToList();
        bool hasData = withQuota.Count > 0;

        CompositionCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        NoAccountsText.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!hasData)
        {
            CompositionLegend.ItemsSource = null;
            return;
        }

        var legend = new List<object>();

        for (int i = 0; i < withQuota.Count; i++)
        {
            var account = withQuota[i];
            long capacity = account.Quota!.TotalBytes;
            long consumed = Math.Min(account.Quota.UsedBytes, capacity);

            // Segment width is proportional to the account's share of total capacity.
            CompositionBar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(capacity, GridUnitType.Star)
            });

            var brush = SeriesBrush(i);

            // Track (free space) with the used portion filled — the fill is anchored to
            // the segment's left edge, so the bar reads as "how full is each drive".
            var track = new Grid
            {
                Background = (Brush)Application.Current.Resources["ControlAltFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(4)
            };
            track.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(consumed, 1), GridUnitType.Star)
            });
            track.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(capacity - consumed, 1), GridUnitType.Star)
            });

            var fill = new Rectangle
            {
                Fill = brush,
                RadiusX = 4,
                RadiusY = 4
            };
            Grid.SetColumn(fill, 0);
            track.Children.Add(fill);

            ToolTipService.SetToolTip(track,
                $"{account.DisplayName}\n{FormatBytes(consumed)} used of {FormatBytes(capacity)}");

            Grid.SetColumn(track, i);
            CompositionBar.Children.Add(track);

            var pct = capacity > 0 ? (double)consumed / capacity * 100.0 : 0;
            legend.Add(new
            {
                Swatch = brush,
                Label = account.DisplayName,
                Value = $"{FormatBytes(consumed)} / {FormatBytes(capacity)}  ({pct:F0}%)"
            });
        }

        CompositionLegend.ItemsSource = legend;
    }

    // ── Per-account rows ────────────────────────────────────────────────

    private void BuildAccounts(IReadOnlyList<ProviderAccount> accounts)
    {
        const double BarMaxWidth = 260;

        AccountsList.ItemsSource = accounts.Select((a, i) =>
        {
            bool hasQuota = a.Quota is { TotalBytes: > 0 };
            double pct = hasQuota
                ? Math.Min(100.0, (double)a.Quota!.UsedBytes / a.Quota.TotalBytes * 100.0)
                : 0;

            var state = App.Connection?.GetState(a.AccountId) ?? ConnectionState.Unknown;
            var (statusText, statusHex) = state switch
            {
                ConnectionState.Connected => ("Connected", StatusGood),
                ConnectionState.Reconnecting => ("Reconnecting…", "#fab219"),
                ConnectionState.Failed => ("Connection failed", StatusCritical),
                ConnectionState.NeedsCredentials => ("Reconnect needed", StatusCritical),
                _ => ("", "#898781")
            };

            return new
            {
                Swatch = SeriesBrush(i),
                a.DisplayName,
                Email = a.Email ?? a.ProviderId,
                StatusText = statusText,
                StatusBrush = new SolidColorBrush(ParseHex(statusHex)),
                BarWidth = hasQuota ? BarMaxWidth * pct / 100.0 : 0,
                UsageText = hasQuota
                    ? $"{FormatBytes(a.Quota!.UsedBytes)} / {FormatBytes(a.Quota.TotalBytes)}"
                    : "No quota data",
                FreeText = hasQuota ? $"{FormatBytes(a.Quota!.FreeBytes)} free" : ""
            };
        }).ToList();
    }

    // ── Pools ───────────────────────────────────────────────────────────

    private void BuildPools(IReadOnlyList<StoragePool> pools, IReadOnlyList<ProviderAccount> accounts)
    {
        const double BarMaxWidth = 420;
        var accountMap = accounts.ToDictionary(a => a.AccountId);

        NoPoolsText.Visibility = pools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PoolsList.ItemsSource = pools.Select(p =>
        {
            long total = 0, used = 0;
            foreach (var m in p.Members)
            {
                if (accountMap.TryGetValue(m.AccountId, out var acc) && acc.Quota != null)
                {
                    total += acc.Quota.TotalBytes;
                    used += acc.Quota.UsedBytes;
                }
            }

            double pct = total > 0 ? (double)used / total * 100.0 : 0;

            return new
            {
                p.Name,
                p.LocalPath,
                StrategyLabel = p.DefaultStrategy.ToString(),
                MemberCountLabel = p.Members.Count == 1 ? "1 account" : $"{p.Members.Count} accounts",
                BarWidth = total > 0 ? BarMaxWidth * pct / 100.0 : 0,
                SizeText = total > 0
                    ? $"{FormatBytes(used)} of {FormatBytes(total)} used ({pct:F1}%)"
                    : "No quota data — refresh quotas on the Accounts page"
            };
        }).ToList();
    }

    // ── Recent activity ─────────────────────────────────────────────────

    private async Task BuildActivityAsync()
    {
        var recent = await App.Store.GetRecentTransfersAsync(limit: 12);
        var stats = await App.Store.GetTransferStatsAsync(DateTime.UtcNow.AddDays(-7));

        NoActivityText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ActivitySummary.Text = recent.Count == 0
            ? ""
            : $"last 7 days: {stats.Uploads} up ({FormatBytes(stats.BytesUploaded)}) · "
              + $"{stats.Downloads} down ({FormatBytes(stats.BytesDownloaded)})"
              + (stats.Failures > 0 ? $" · {stats.Failures} failed" : "");

        var accounts = await App.Store.GetAllAccountsAsync();
        var accountMap = accounts.ToDictionary(a => a.AccountId, a => a.DisplayName);

        ActivityList.ItemsSource = recent.Select(t =>
        {
            bool failed = t.Outcome == TransferOutcome.Failed;

            // Glyph and label carry the meaning; colour only reinforces it.
            var glyph = t.Kind switch
            {
                TransferKind.Upload => "",      // upload
                TransferKind.Download => "",    // download
                TransferKind.Delete => "",      // delete
                TransferKind.Placeholder => "", // cloud
                TransferKind.Move => "",        // move
                _ => ""
            };

            var verb = t.Kind switch
            {
                TransferKind.Upload => failed ? "Upload failed" : "Uploaded to",
                TransferKind.Download => failed ? "Download failed" : "Downloaded from",
                TransferKind.Delete => failed ? "Delete failed" : "Removed from",
                TransferKind.Placeholder => "Available on demand from",
                TransferKind.Move => "Moved to",
                _ => "Synced"
            };

            var accountName = t.AccountId != null && accountMap.TryGetValue(t.AccountId, out var n) ? n : null;
            var detail = accountName != null ? $"{verb} {accountName}" : verb;
            if (failed && !string.IsNullOrEmpty(t.Error))
                detail += $" — {t.Error}";

            return new
            {
                Glyph = glyph,
                Tint = new SolidColorBrush(ParseHex(failed ? StatusCritical : StatusGood)),
                t.FileName,
                Detail = detail,
                SizeText = t.Bytes > 0 ? FormatBytes(t.Bytes) : "",
                TimeText = FormatTimeAgo(t.TimestampUtc)
            };
        }).ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
