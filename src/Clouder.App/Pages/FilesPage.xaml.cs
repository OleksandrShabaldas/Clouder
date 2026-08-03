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

        // Version history — previous copies Clouder kept when the file was replaced.
        var versionPanel = new StackPanel { Spacing = 6 };
        versionPanel.Children.Add(new TextBlock
        {
            Text = versions.Count > 0
                ? $"Previous versions ({versions.Count})"
                : "Previous versions",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13
        });

        if (versions.Count == 0)
        {
            versionPanel.Children.Add(new TextBlock
            {
                Text = App.AppConfig.FileVersioningEnabled
                    ? "No previous versions yet. Clouder keeps a copy each time this file is "
                      + "replaced by a newer one."
                    : "Version history is turned off in Settings, so replaced copies are deleted "
                      + "rather than kept.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        foreach (var ver in versions.OrderByDescending(v => v.VersionNumber))
        {
            var where = ver.IsStriped
                ? "split across accounts"
                : ver.AccountId != null
                    ? accountMap.GetValueOrDefault(ver.AccountId, ver.AccountId)
                    : "unknown account";

            var archived = ver.CreatedAtUtc == DateTime.MinValue
                ? ""
                : $" · kept {FormatTimeAgo(ver.CreatedAtUtc)}";

            var info = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Version {ver.VersionNumber}",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        FontSize = 13
                    },
                    new TextBlock
                    {
                        Text = $"{FormatBytes(ver.Size)} · modified {ver.ModifiedAtUtc.ToLocalTime():g}{archived}",
                        FontSize = 12,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    },
                    new TextBlock
                    {
                        Text = where,
                        FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                    }
                }
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

            var restoreBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE777", FontSize = 13 },
                Padding = new Thickness(8, 5, 8, 5)
            };
            ToolTipService.SetToolTip(restoreBtn, "Restore this version");
            restoreBtn.Click += async (_, _) => await RestoreVersionAsync(ver, item.Name);

            var saveBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE896", FontSize = 13 },
                Padding = new Thickness(8, 5, 8, 5)
            };
            ToolTipService.SetToolTip(saveBtn, "Save a copy to Downloads");
            saveBtn.Click += async (_, _) => await SaveVersionCopyAsync(ver, item.Name);

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
                Padding = new Thickness(8, 5, 8, 5)
            };
            ToolTipService.SetToolTip(deleteBtn, "Delete this version");
            deleteBtn.Click += async (_, _) => await DeleteVersionAsync(ver);

            actions.Children.Add(restoreBtn);
            actions.Children.Add(saveBtn);
            actions.Children.Add(deleteBtn);

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            row.Children.Add(info);
            row.Children.Add(actions);

            versionPanel.Children.Add(new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = row
            });
        }

        content.Children.Add(versionPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "File Details",
            Content = new ScrollViewer { Content = content, MaxHeight = 500 },
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
    }

    // ── Version actions ─────────────────────────────────────────────────

    private async Task RestoreVersionAsync(FileVersion version, string fileName)
    {
        if (App.Versions == null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Restore version {version.VersionNumber}?",
            Content = $"\"{fileName}\" will be put back to the version from "
                    + $"{version.ModifiedAtUtc.ToLocalTime():g}. The copy that is current now is "
                    + "kept as a version too, so this can be undone.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var path = await App.Versions.RestoreAsync(version.VersionId);

            // Push it to the cloud now rather than waiting for the next sweep.
            var poolId = version.FileId[..version.FileId.IndexOf('|')];
            if (App.SyncService != null)
                await App.SyncService.SyncPoolAsync(poolId);

            await LoadFilesAsync();
            await ShowErrorAsync("Version restored",
                $"\"{fileName}\" is back to version {version.VersionNumber}.\n\nSaved to: {path}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to restore version {version.VersionNumber}", ex);
            await ShowErrorAsync("Restore failed", ex.Message);
        }
    }

    private async Task SaveVersionCopyAsync(FileVersion version, string fileName)
    {
        if (App.Versions == null) return;

        try
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                $"{stem} (v{version.VersionNumber}){ext}");

            await App.Versions.SaveVersionAsAsync(version.VersionId, target);

            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\""); }
            catch { }

            await ShowErrorAsync("Copy saved", $"Version {version.VersionNumber} saved to:\n{target}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to save version {version.VersionNumber}", ex);
            await ShowErrorAsync("Could not save the copy", ex.Message);
        }
    }

    private async Task DeleteVersionAsync(FileVersion version)
    {
        if (App.Versions == null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete version {version.VersionNumber}?",
            Content = "This removes the stored copy permanently. The current version of the "
                    + "file is not affected.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await App.Versions.DeleteVersionAsync(version.VersionId);
            await LoadFilesAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to delete version {version.VersionNumber}", ex);
            await ShowErrorAsync("Could not delete the version", ex.Message);
        }
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
