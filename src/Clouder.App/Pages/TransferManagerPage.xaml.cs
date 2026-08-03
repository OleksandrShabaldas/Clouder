using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder_App.Pages;

/// <summary>
/// Full history of everything the pool has moved: what was uploaded or downloaded,
/// when, to which account, and whether it was striped across several accounts.
/// </summary>
public sealed partial class TransferManagerPage : Page
{
    private enum Category { All, Uploads, Downloads, Completed, Failed }

    private const string StatusGood = "#0ca30c";
    private const string StatusCritical = "#d03b3b";
    private const string DirectionUp = "#2a78d6";
    private const string DirectionDown = "#1baf7a";

    private List<TransferRecord> _all = [];
    private Dictionary<string, string> _accountNames = new();
    private Dictionary<string, string> _poolNames = new();

    private Category _category = Category.All;
    private MediaKind? _mediaFilter;
    private string? _poolFilter;
    private string _search = "";
    private bool _loading = true;

    public TransferManagerPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    // ── Data ────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            // History is pruned to a couple of thousand rows, so pulling the lot and
            // filtering in memory keeps counts exact without a query per category.
            _all = (await App.Store.GetRecentTransfersAsync(limit: 2000)).ToList();

            var accounts = await App.Store.GetAllAccountsAsync();
            _accountNames = accounts.ToDictionary(a => a.AccountId, a => a.DisplayName);

            var pools = await App.Store.GetAllPoolsAsync();
            _poolNames = pools.ToDictionary(p => p.PoolId, p => p.Name);

            BuildPoolFilter(pools);
            UpdateQuotaBar(accounts);
            UpdatePauseButton();
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to load transfers", ex);
        }
        finally
        {
            _loading = false;
        }

        BuildSidebar();
        ApplyFilters();
    }

    private void BuildPoolFilter(IReadOnlyList<StoragePool> pools)
    {
        var items = new List<string> { "All pools" };
        items.AddRange(pools.Select(p => p.Name));
        PoolFilter.ItemsSource = items;
        PoolFilter.SelectedIndex = 0;
    }

    private void UpdateQuotaBar(IReadOnlyList<ProviderAccount> accounts)
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
            var pct = (double)used / total * 100.0;
            QuotaBar.Value = pct;
            QuotaText.Text = $"{FormatBytes(used)} of {FormatBytes(total)} ({pct:F0}%)";
        }
        else
        {
            QuotaBar.Value = 0;
            QuotaText.Text = "No quota data";
        }
    }

    private void UpdatePauseButton()
    {
        bool paused = App.SyncService?.Paused ?? false;
        PauseIcon.Glyph = paused ? "" : "";   // play : pause
        ToolTipService.SetToolTip(PauseBtn, paused ? "Resume syncing" : "Pause syncing");
    }

    // ── Sidebar ─────────────────────────────────────────────────────────

    private void BuildSidebar()
    {
        // Counts describe the whole history, not the current filter, so the sidebar
        // stays a stable map of what exists.
        var scoped = _all.Where(MatchesPool).ToList();

        int Count(Category c) => scoped.Count(t => MatchesCategory(t, c));

        var categories = new[]
        {
            new SidebarItem(Category.All, null, "", "All transfers", Count(Category.All)),
            new SidebarItem(Category.Uploads, null, "", "Uploads", Count(Category.Uploads)),
            new SidebarItem(Category.Downloads, null, "", "Downloads", Count(Category.Downloads)),
            new SidebarItem(Category.Completed, null, "", "Completed", Count(Category.Completed)),
            new SidebarItem(Category.Failed, null, "", "Failed", Count(Category.Failed))
        };

        var selectedCategory = _category;
        CategoryList.ItemsSource = categories;
        CategoryList.SelectedIndex = Array.FindIndex(categories, c => c.Category == selectedCategory);

        var media = new List<SidebarItem>
        {
            new(null, null, "", "All types", scoped.Count)
        };

        foreach (var kind in Enum.GetValues<MediaKind>())
        {
            int count = scoped.Count(t => MediaKindClassifier.Classify(t.FileName) == kind);
            if (count == 0 && kind != MediaKind.Other) continue;   // hide empty categories
            media.Add(new SidebarItem(null, kind, MediaKindClassifier.Glyph(kind),
                MediaKindClassifier.DisplayName(kind), count));
        }

        var selectedMedia = _mediaFilter;
        MediaList.ItemsSource = media;
        MediaList.SelectedIndex = media.FindIndex(m => m.Media == selectedMedia);
    }

    // ── Filtering ───────────────────────────────────────────────────────

    private bool MatchesPool(TransferRecord t) =>
        _poolFilter == null || t.PoolId == _poolFilter;

    private static bool MatchesCategory(TransferRecord t, Category category) => category switch
    {
        Category.Uploads => t.Kind == TransferKind.Upload,
        Category.Downloads => t.Kind is TransferKind.Download or TransferKind.Placeholder,
        Category.Completed => t.Outcome == TransferOutcome.Success,
        Category.Failed => t.Outcome == TransferOutcome.Failed,
        _ => true
    };

    private void ApplyFilters()
    {
        var filtered = _all
            .Where(MatchesPool)
            .Where(t => MatchesCategory(t, _category))
            .Where(t => _mediaFilter == null || MediaKindClassifier.Classify(t.FileName) == _mediaFilter)
            .Where(t => _search.Length == 0
                        || t.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                        || (t.RelativePath?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        TransferList.ItemsSource = filtered.Select(ToViewModel).ToList();

        bool any = filtered.Count > 0;
        TransferList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        EmptyText.Text = _all.Count == 0
            ? "No transfers yet. Drop a file into a pool folder to get started."
            : "No transfers match these filters.";

        HeadingText.Text = _category switch
        {
            Category.Uploads => "Uploads",
            Category.Downloads => "Downloads",
            Category.Completed => "Completed",
            Category.Failed => "Failed",
            _ => "All transfers"
        };

        var parts = new List<string> { $"{filtered.Count} of {_all.Count} transfer(s)" };
        if (_mediaFilter != null) parts.Add(MediaKindClassifier.DisplayName(_mediaFilter.Value).ToLowerInvariant());
        if (_poolFilter != null && _poolNames.TryGetValue(_poolFilter, out var pn)) parts.Add($"in {pn}");
        SubheadingText.Text = string.Join(" · ", parts);

        UpdateTotals(filtered);
    }

    private void UpdateTotals(List<TransferRecord> filtered)
    {
        long up = filtered.Where(t => t.Kind == TransferKind.Upload && t.Outcome == TransferOutcome.Success)
                          .Sum(t => t.Bytes);
        long down = filtered.Where(t => t.Kind == TransferKind.Download && t.Outcome == TransferOutcome.Success)
                            .Sum(t => t.Bytes);
        int striped = filtered.Count(t => t.IsStriped);
        int failed = filtered.Count(t => t.Outcome == TransferOutcome.Failed);

        TotalUpText.Text = FormatBytes(up);
        TotalDownText.Text = FormatBytes(down);
        TotalStripedText.Text = striped.ToString("N0");
        TotalFailedText.Text = failed.ToString("N0");
    }

    // ── Row view model ──────────────────────────────────────────────────

    private object ToViewModel(TransferRecord t)
    {
        bool failed = t.Outcome == TransferOutcome.Failed;
        bool isDownload = t.Kind is TransferKind.Download or TransferKind.Placeholder;

        var verb = t.Kind switch
        {
            TransferKind.Upload => failed ? "Upload failed" : "Uploaded to",
            TransferKind.Download => failed ? "Download failed" : "Downloaded from",
            TransferKind.Delete => failed ? "Delete failed" : "Removed from",
            TransferKind.Placeholder => "Available on demand from",
            TransferKind.Move => "Moved to",
            _ => "Synced"
        };

        string where;
        if (t.IsStriped)
        {
            var names = (t.AccountIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => _accountNames.GetValueOrDefault(id, id))
                .ToList();
            where = names.Count > 0 ? string.Join(" + ", names) : "several accounts";
        }
        else
        {
            where = t.AccountId != null
                ? _accountNames.GetValueOrDefault(t.AccountId, t.AccountId)
                : "";
        }

        var detail = string.IsNullOrEmpty(where) ? verb : $"{verb} {where}";
        if (failed && !string.IsNullOrEmpty(t.Error))
            detail += $" — {t.Error}";
        else if (!string.IsNullOrEmpty(t.RelativePath) && t.RelativePath != t.FileName)
            detail += $" · {t.RelativePath}";

        return new TransferRow
        {
            TransferId = t.TransferId,
            FileName = t.FileName,
            Detail = detail,
            MediaGlyph = MediaKindClassifier.Glyph(MediaKindClassifier.Classify(t.FileName)),
            DirectionGlyph = isDownload ? "" : t.Kind == TransferKind.Delete ? "" : "",
            DirectionBrush = Brush(isDownload ? DirectionDown : DirectionUp),
            SizeText = t.Bytes > 0 ? FormatBytes(t.Bytes) : "",
            TimeText = FormatTimeAgo(t.TimestampUtc),
            StatusGlyph = failed ? "" : "",
            StatusBrush = Brush(failed ? StatusCritical : StatusGood),
            StripedVisible = t.IsStriped ? Visibility.Visible : Visibility.Collapsed,
            StripedText = t.IsStriped ? $"{t.ChunkCount} chunks" : ""
        };
    }

    // ── Interactions ────────────────────────────────────────────────────

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoryList.SelectedItem is not SidebarItem item) return;
        _category = item.Category ?? Category.All;
        ApplyFilters();
    }

    private void Media_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MediaList.SelectedItem is not SidebarItem item) return;
        _mediaFilter = item.Media;
        ApplyFilters();
    }

    private void PoolFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PoolFilter.SelectedIndex < 0) return;

        _poolFilter = PoolFilter.SelectedIndex == 0
            ? null
            : _poolNames.FirstOrDefault(kv => kv.Value == (string)PoolFilter.SelectedItem).Key;

        BuildSidebar();
        ApplyFilters();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _search = SearchBox.Text.Trim();
        ApplyFilters();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        if (App.SyncService == null) return;

        bool paused = !App.SyncService.Paused;
        App.SyncService.Paused = paused;
        if (App.RemoteSync != null) App.RemoteSync.Paused = paused;

        ClouderLog.Info(paused ? "Sync paused from the transfer manager" : "Sync resumed from the transfer manager");
        UpdatePauseButton();
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        await App.Store.ClearTransfersAsync(onlyCompleted: true);
        await LoadAsync();
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all history?",
            Content = "This removes the record of every transfer. Your files and their cloud "
                    + "copies are not affected.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await App.Store.ClearTransfersAsync();
        await LoadAsync();
    }

    // ── Detail view ─────────────────────────────────────────────────────

    private async void Transfer_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TransferRow row) return;

        var record = _all.FirstOrDefault(t => t.TransferId == row.TransferId);
        if (record == null) return;

        var panel = new StackPanel { Spacing = 14 };

        bool failed = record.Outcome == TransferOutcome.Failed;
        panel.Children.Add(new InfoBar
        {
            Title = record.FileName,
            Message = failed
                ? record.Error ?? "The transfer failed."
                : $"{record.Kind} completed {FormatTimeAgo(record.TimestampUtc)}.",
            Severity = failed ? InfoBarSeverity.Error : InfoBarSeverity.Success,
            IsOpen = true,
            IsClosable = false
        });

        var facts = new StackPanel { Spacing = 6 };
        void Fact(string label, string value) =>
            facts.Children.Add(BuildFact(label, value));

        Fact("Direction", record.Kind.ToString());
        Fact("Status", record.Outcome.ToString());
        Fact("Size", record.Bytes > 0 ? FormatBytes(record.Bytes) : "—");
        Fact("When", record.TimestampUtc.ToLocalTime().ToString("f"));

        if (record.DurationMs > 0)
            Fact("Duration", $"{record.DurationMs / 1000.0:F1} s");
        if (record.BytesPerSecond is { } speed)
            Fact("Average speed", $"{FormatBytes((long)speed)}/s");

        Fact("Pool", _poolNames.GetValueOrDefault(record.PoolId, record.PoolId));
        if (!string.IsNullOrEmpty(record.RelativePath))
            Fact("Path in pool", record.RelativePath);

        Fact("Storage layout", record.IsStriped
            ? $"Striped across {record.ChunkCount} chunks"
            : "Whole file on one account");

        panel.Children.Add(facts);

        // Where the bytes actually live — the chunk map for a striped file.
        if (record.ItemId != null)
        {
            try
            {
                var plans = await App.Store.GetStripePlansAsync(record.ItemId);
                if (plans.Count > 0)
                {
                    var chunkPanel = new StackPanel { Spacing = 4 };
                    chunkPanel.Children.Add(new TextBlock
                    {
                        Text = "Chunk placement",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        FontSize = 13
                    });

                    foreach (var plan in plans.OrderBy(p => p.ChunkIndex))
                    {
                        var accName = _accountNames.GetValueOrDefault(plan.AccountId, plan.AccountId);
                        chunkPanel.Children.Add(new TextBlock
                        {
                            Text = $"Chunk {plan.ChunkIndex}: {accName} — "
                                 + $"{FormatBytes(plan.Length)} at offset {FormatBytes(plan.Offset)}",
                            FontSize = 12,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                        });
                    }

                    panel.Children.Add(new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12),
                        Child = chunkPanel
                    });
                }
                else if (record.AccountId != null)
                {
                    Fact("Stored on", _accountNames.GetValueOrDefault(record.AccountId, record.AccountId));
                }
            }
            catch (Exception ex)
            {
                ClouderLog.Debug($"Could not load chunk layout: {ex.Message}");
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Transfer details",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = failed ? "Retry" : "Show in Explorer",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (failed)
            await RetryAsync(record);
        else
            ShowInExplorer(record);
    }

    private static Border BuildFact(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);

        return new Border { Child = grid };
    }

    /// <summary>Re-runs a sync for the pool, which picks the file up again if it still needs syncing.</summary>
    private async Task RetryAsync(TransferRecord record)
    {
        if (App.SyncService == null)
        {
            await ShowMessageAsync("Unavailable", "The sync service is not running.");
            return;
        }

        try
        {
            await App.SyncService.SyncPoolAsync(record.PoolId);
            await LoadAsync();
            await ShowMessageAsync("Retry finished",
                $"Re-ran the sync for {_poolNames.GetValueOrDefault(record.PoolId, "this pool")}. "
                + "Check the list for the new result.");
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Retry failed", ex);
            await ShowMessageAsync("Retry failed", ex.Message);
        }
    }

    private async void ShowInExplorer(TransferRecord record)
    {
        try
        {
            var pool = await App.Store.GetPoolAsync(record.PoolId);
            if (pool == null || string.IsNullOrEmpty(record.RelativePath)) return;

            var path = Path.Combine(pool.LocalPath, record.RelativePath);
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(pool.LocalPath))
                System.Diagnostics.Process.Start("explorer.exe", pool.LocalPath);
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not open Explorer: {ex.Message}");
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK"
        }.ShowAsync();
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

    // ── View models ─────────────────────────────────────────────────────

    private sealed class SidebarItem
    {
        public SidebarItem(Category? category, MediaKind? media, string glyph, string label, int count)
        {
            Category = category;
            Media = media;
            Glyph = glyph;
            Label = label;
            CountText = count > 0 ? count.ToString("N0") : "";
        }

        public Category? Category { get; }
        public MediaKind? Media { get; }
        public string Glyph { get; }
        public string Label { get; }
        public string CountText { get; }
    }

    private sealed class TransferRow
    {
        public required string TransferId { get; init; }
        public required string FileName { get; init; }
        public required string Detail { get; init; }
        public required string MediaGlyph { get; init; }
        public required string DirectionGlyph { get; init; }
        public required Brush DirectionBrush { get; init; }
        public required string SizeText { get; init; }
        public required string TimeText { get; init; }
        public required string StatusGlyph { get; init; }
        public required Brush StatusBrush { get; init; }
        public Visibility StripedVisible { get; init; }
        public required string StripedText { get; init; }
    }
}
