using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder_App.Pages;

public sealed partial class FilesPage : Page
{
    private List<FileViewModel> _allFiles = [];
    private string _currentFilter = "";

    public FilesPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadPoolsAsync();
        await RefreshConflictBannerAsync();
    }

    // ── Conflicts ───────────────────────────────────────────────────────

    private async Task RefreshConflictBannerAsync()
    {
        try
        {
            var conflicts = await App.Store.GetConflictsAsync();
            ConflictBar.IsOpen = conflicts.Count > 0;
            ConflictBar.Message = conflicts.Count == 1
                ? $"\"{conflicts[0].RelativePath}\" changed both on this PC and in the cloud. "
                  + "Neither copy has been changed — choose which one to keep."
                : $"{conflicts.Count} files changed both on this PC and in the cloud. "
                  + "No copies have been changed — choose which ones to keep.";
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to load conflicts", ex);
        }
    }

    private async void ResolveConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (App.RemoteSync == null)
        {
            await ShowErrorAsync("Unavailable", "The sync service is not running.");
            return;
        }

        var conflicts = await App.Store.GetConflictsAsync();
        if (conflicts.Count == 0)
        {
            await RefreshConflictBannerAsync();
            return;
        }

        var panel = new StackPanel { Spacing = 12 };
        var choices = new List<(string ConflictId, ComboBox Selector)>();

        panel.Children.Add(new InfoBar
        {
            Title = "Choose what to keep",
            Message = "\"Keep both\" saves your local copy under a new name and downloads the cloud "
                    + "version alongside it — the safest option when you're unsure.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        });

        foreach (var conflict in conflicts)
        {
            var selector = new ComboBox
            {
                ItemsSource = new[] { "Keep both", "Keep the local copy", "Keep the cloud copy" },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = conflict.RelativePath,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"This PC: {FormatBytes(conflict.LocalSize)}, edited {conflict.LocalModifiedUtc.ToLocalTime():g}",
                            FontSize = 12,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                        },
                        new TextBlock
                        {
                            Text = $"Cloud: {FormatBytes(conflict.RemoteSize)}, edited {conflict.RemoteModifiedUtc.ToLocalTime():g}",
                            FontSize = 12,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                        },
                        selector
                    }
                }
            };

            panel.Children.Add(card);
            choices.Add((conflict.ConflictId, selector));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Resolve {conflicts.Count} conflict(s)",
            Content = new ScrollViewer { Content = panel, MaxHeight = 500 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int resolved = 0, failed = 0;
        foreach (var (conflictId, selector) in choices)
        {
            var choice = selector.SelectedIndex switch
            {
                1 => ConflictResolutionChoice.KeepLocal,
                2 => ConflictResolutionChoice.KeepRemote,
                _ => ConflictResolutionChoice.KeepBoth
            };

            try
            {
                if (await App.RemoteSync.ResolveConflictAsync(conflictId, choice)) resolved++;
                else failed++;
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to resolve conflict '{conflictId}'", ex);
                failed++;
            }
        }

        await RefreshConflictBannerAsync();
        await LoadFilesAsync();

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Conflicts resolved",
            Content = failed == 0
                ? $"Resolved {resolved} conflict(s)."
                : $"Resolved {resolved} conflict(s). {failed} could not be resolved — check that the "
                  + "account is connected and the cloud copy still exists.",
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    private async Task LoadPoolsAsync()
    {
        var pools = await App.Store.GetAllPoolsAsync();
        var items = new List<string> { "All Pools" };
        items.AddRange(pools.Select(p => p.Name));
        PoolSelector.ItemsSource = items;
        PoolSelector.SelectedIndex = 0;
    }

    private async void PoolSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        await LoadFilesAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentFilter = SearchBox.Text.Trim();
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        var pools = await App.Store.GetAllPoolsAsync();
        var accounts = await App.Store.GetAllAccountsAsync();
        var accountMap = accounts.ToDictionary(a => a.AccountId, a => a.DisplayName);

        // Determine which accounts to show files for
        var memberAccountIds = new HashSet<string>();

        if (PoolSelector.SelectedIndex <= 0)
        {
            // "All Pools" — show all pool members
            foreach (var pool in pools)
                foreach (var m in pool.Members)
                    memberAccountIds.Add(m.AccountId);
        }
        else
        {
            var selectedPool = pools[PoolSelector.SelectedIndex - 1];
            foreach (var m in selectedPool.Members)
                memberAccountIds.Add(m.AccountId);
        }

        _allFiles = [];
        int versionedCount = 0, stripedCount = 0;
        long totalSize = 0;

        foreach (var accountId in memberAccountIds)
        {
            try
            {
                var items = await App.Store.GetItemsByAccountAsync(accountId);
                var accName = accountMap.GetValueOrDefault(accountId, accountId);

                foreach (var item in items)
                {
                    var versions = await App.Store.GetFileVersionsAsync(item.Id);
                    var stripePlans = await App.Store.GetStripePlansAsync(item.Id);
                    bool isVersioned = versions.Count > 0;
                    bool isStriped = stripePlans.Count > 0;

                    if (isVersioned) versionedCount++;
                    if (isStriped) stripedCount++;
                    if (item.Type == CloudItemType.File) totalSize += item.Size;

                    _allFiles.Add(new FileViewModel
                    {
                        FileId = item.Id,
                        RemoteId = item.RemoteId,
                        Name = item.Name,
                        AccountId = item.AccountId,
                        ProviderId = item.ProviderId,
                        AccountName = accName,
                        Type = item.Type,
                        TypeGlyph = item.Type == CloudItemType.Folder ? "" : GetFileGlyph(item.Name),
                        Size = item.Size,
                        SizeText = item.Type == CloudItemType.Folder ? "" : FormatBytes(item.Size),
                        ModifiedAtUtc = item.ModifiedAtUtc,
                        ModifiedText = FormatTimeAgo(item.ModifiedAtUtc),
                        VersionCount = versions.Count,
                        VersionCountText = versions.Count > 0 ? $"{versions.Count} ver" : "",
                        VersionedVisible = versions.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                        IsStriped = isStriped,
                        StripedVisible = isStriped ? Visibility.Visible : Visibility.Collapsed,
                        DownloadVisible = item.Type == CloudItemType.File ? Visibility.Visible : Visibility.Collapsed,
                        StateBadgeText = StateLabel(item.SyncState),
                        StateBadgeVisible = item.SyncState == SyncState.Synced ? Visibility.Collapsed : Visibility.Visible,
                        StateBadgeBrush = StateBrush(item.SyncState)
                    });
                }
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Error loading files for account {accountId}", ex);
            }
        }

        // Sort: folders first, then by name
        _allFiles = _allFiles
            .OrderBy(f => f.Type == CloudItemType.Folder ? 0 : 1)
            .ThenBy(f => f.Name)
            .ToList();

        StatsText.Text = $"{_allFiles.Count} item(s), {FormatBytes(totalSize)} total";
        VersionedCountText.Text = $"{versionedCount} versioned";
        StripedCountText.Text = $"{stripedCount} striped";

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(_currentFilter)
            ? _allFiles
            : _allFiles.Where(f => f.Name.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        FilesListView.ItemsSource = filtered;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesListView.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Download / reassemble ───────────────────────────────────────────

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fileId } btn) return;
        if (App.SyncService == null)
        {
            await ShowErrorAsync("Unavailable", "The sync service is not running.");
            return;
        }

        var item = await App.Store.GetItemAsync(fileId);
        if (item == null) { await ShowErrorAsync("Not found", "File not found."); return; }

        // Determine the destination: hydrate back into the pool's local folder.
        // FileId is "{poolId}|{relativePath}".
        var sep = fileId.IndexOf('|');
        string destPath;
        if (sep > 0)
        {
            var poolId = fileId[..sep];
            var relativePath = fileId[(sep + 1)..];
            var pool = await App.Store.GetPoolAsync(poolId);
            destPath = pool != null
                ? Path.Combine(pool.LocalPath, relativePath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", item.Name);
        }
        else
        {
            destPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", item.Name);
        }

        btn.IsEnabled = false;
        var progress = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Downloading",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressBar { IsIndeterminate = true },
                    new TextBlock
                    {
                        Text = item.Name + (await App.Store.GetStripePlansAsync(fileId) is { Count: > 0 } p
                            ? $"  (reassembling {p.Count} chunks)" : ""),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        var showing = progress.ShowAsync();

        try
        {
            await App.SyncService.DownloadFileAsync(fileId, destPath);
            progress.Hide();

            // Open Explorer with the file selected.
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{destPath}\""); }
            catch { }

            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Download complete",
                Content = $"Saved to:\n{destPath}",
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            progress.Hide();
            ClouderLog.Error($"Download failed for {fileId}", ex);
            await ShowErrorAsync("Download failed", ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ── Version History Dialog ──────────────────────────────────────────

    private async void ViewVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fileId }) return;

        var item = await App.Store.GetItemAsync(fileId);
        if (item == null)
        {
            await ShowErrorAsync("Not Found", "File not found in database.");
            return;
        }

        var versions = await App.Store.GetFileVersionsAsync(fileId);
        var stripePlans = await App.Store.GetStripePlansAsync(fileId);
        var accounts = await App.Store.GetAllAccountsAsync();
        var accountMap = accounts.ToDictionary(a => a.AccountId, a => a.DisplayName);

        var content = new StackPanel { Spacing = 16 };

        // File info card
        var fileInfo = new InfoBar
        {
            Title = item.Name,
            Message = $"Size: {FormatBytes(item.Size)} | Account: {accountMap.GetValueOrDefault(item.AccountId, item.AccountId)} | "
                    + $"Modified: {item.ModifiedAtUtc:g}",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        };
        content.Children.Add(fileInfo);

        // Stripe info
        if (stripePlans.Count > 0)
        {
            var stripePanel = new StackPanel { Spacing = 4 };
            stripePanel.Children.Add(new TextBlock
            {
                Text = $"Striped across {stripePlans.Count} chunk(s)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13
            });

            foreach (var plan in stripePlans.OrderBy(s => s.ChunkIndex))
            {
                var accName = accountMap.GetValueOrDefault(plan.AccountId, plan.AccountId);
                stripePanel.Children.Add(new TextBlock
                {
                    Text = $"  Chunk {plan.ChunkIndex}: {accName} (offset {FormatBytes(plan.Offset)}, {FormatBytes(plan.Length)})",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
            }

            content.Children.Add(new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = stripePanel
            });
        }

        // Version history
        if (versions.Count > 0)
        {
            var versionPanel = new StackPanel { Spacing = 4 };
            versionPanel.Children.Add(new TextBlock
            {
                Text = $"Version History ({versions.Count} version(s))",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13
            });

            foreach (var ver in versions.OrderByDescending(v => v.ModifiedAtUtc))
            {
                var verCard = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 4, 0, 0),
                    Child = new Grid
                    {
                        ColumnSpacing = 12,
                        Children =
                        {
                            SetColumn(new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = $"Version {ver.VersionId[..Math.Min(8, ver.VersionId.Length)]}",
                                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                        FontSize = 13
                                    },
                                    new TextBlock
                                    {
                                        Text = $"{ver.ModifiedAtUtc:g} | {FormatBytes(ver.Size)}"
                                             + (ver.ModifiedBy != null ? $" | by {ver.ModifiedBy}" : ""),
                                        FontSize = 12,
                                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                                    }
                                }
                            }, 0)
                        }
                    }
                };
                versionPanel.Children.Add(verCard);
            }

            content.Children.Add(versionPanel);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = "No version history available for this file.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 13
            });
        }

        // Fetch from provider button
        var fetchBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = "Fetch Versions from Provider" }
                }
            },
            Margin = new Thickness(0, 8, 0, 0)
        };
        fetchBtn.Click += async (_, _) =>
        {
            try
            {
                var provider = App.Providers.GetProvider(item.ProviderId);
                if (provider == null)
                {
                    await ShowErrorAsync("Provider Unavailable",
                        $"The {item.ProviderId} provider is not connected. Reconnect the account first.");
                    return;
                }

                if (!provider.Capabilities.HasFlag(Clouder.Core.Providers.ProviderCapabilities.VersionHistory))
                {
                    await ShowErrorAsync("Not Supported",
                        $"The {provider.DisplayName} provider does not support version history.");
                    return;
                }

                fetchBtn.IsEnabled = false;
                var fetched = await provider.GetVersionsAsync(item.AccountId, item.RemoteId);
                foreach (var v in fetched)
                {
                    v.FileId = item.Id;
                    await App.Store.AddFileVersionAsync(v);
                }

                await ShowErrorAsync("Versions Fetched",
                    $"Retrieved {fetched.Count} version(s) from {provider.DisplayName}.");
                fetchBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ClouderLog.Error("Failed to fetch versions", ex);
                await ShowErrorAsync("Error", ex.Message);
                fetchBtn.IsEnabled = true;
            }
        };
        content.Children.Add(fetchBtn);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "File Details",
            Content = new ScrollViewer { Content = content, MaxHeight = 500 },
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string StateLabel(SyncState state) => state switch
    {
        SyncState.PendingUpload => "Uploading",
        SyncState.PendingDownload => "Downloading",
        SyncState.Conflict => "Conflict",
        _ => ""
    };

    private static Microsoft.UI.Xaml.Media.Brush StateBrush(SyncState state) => state switch
    {
        SyncState.Conflict => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SteelBlue)
    };

    private static FrameworkElement SetColumn(FrameworkElement element, int col)
    {
        Grid.SetColumn(element, col);
        return element;
    }

    private static string GetFileGlyph(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "",
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac" => "",
            ".pdf" => "",
            ".doc" or ".docx" or ".txt" or ".rtf" => "",
            ".xls" or ".xlsx" or ".csv" => "",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "",
            ".exe" or ".msi" => "",
            _ => ""
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    private static string FormatTimeAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return utc.ToString("MMM d, yyyy");
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    // ── View model ──────────────────────────────────────────────────────

    private sealed class FileViewModel
    {
        public required string FileId { get; set; }
        public required string RemoteId { get; set; }
        public required string Name { get; set; }
        public required string AccountId { get; set; }
        public required string ProviderId { get; set; }
        public required string AccountName { get; set; }
        public CloudItemType Type { get; set; }
        public required string TypeGlyph { get; set; }
        public long Size { get; set; }
        public required string SizeText { get; set; }
        public DateTime ModifiedAtUtc { get; set; }
        public required string ModifiedText { get; set; }
        public int VersionCount { get; set; }
        public required string VersionCountText { get; set; }
        public Visibility VersionedVisible { get; set; }
        public bool IsStriped { get; set; }
        public Visibility StripedVisible { get; set; }
        public Visibility DownloadVisible { get; set; }
        public required string StateBadgeText { get; set; }
        public Visibility StateBadgeVisible { get; set; }
        public required Microsoft.UI.Xaml.Media.Brush StateBadgeBrush { get; set; }
    }
}
