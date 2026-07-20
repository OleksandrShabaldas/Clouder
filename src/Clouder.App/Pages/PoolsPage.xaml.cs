using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Validation;

namespace Clouder_App.Pages;

public sealed partial class PoolsPage : Page
{
    private static readonly Dictionary<PlacementStrategy, string> StrategyDescriptions = new()
    {
        // These describe how a file is placed WITHIN a fill tier. Tiers themselves are
        // always consumed lowest-first — see the Accounts dialog.
        [PlacementStrategy.FillFirst] =
            "Packs one account at a time: within a tier, the account with the least free "
            + "space that still fits gets the file. Keeps free space consolidated instead "
            + "of leaving gaps everywhere.",

        [PlacementStrategy.RoundRobin] =
            "Keeps accounts in a tier at similar fill levels by always choosing the one "
            + "with the lowest usage percentage. Best for spreading wear evenly.",

        [PlacementStrategy.LargestFree] =
            "Places each file on whichever account in the tier has the most free space. "
            + "Best for keeping maximum headroom on every account.",

        [PlacementStrategy.Custom] =
            "Your file rules decide placement (e.g. videos to one account, documents to "
            + "another). Anything no rule matches is spread evenly within its tier."
    };

    public PoolsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshPoolsAsync();
    }

    private async Task RefreshPoolsAsync()
    {
        var pools = await App.Store.GetAllPoolsAsync();
        var accounts = await App.Store.GetAllAccountsAsync();
        var accountMap = accounts.ToDictionary(a => a.AccountId, a => a);

        PoolsListView.ItemsSource = pools.Select(p =>
        {
            long poolTotal = 0, poolUsed = 0;
            var memberDetails = new List<object>();

            foreach (var m in p.Members)
            {
                if (accountMap.TryGetValue(m.AccountId, out var acc))
                {
                    var hasQuota = acc.Quota != null && acc.Quota.TotalBytes > 0;
                    long mTotal = acc.Quota?.TotalBytes ?? 0;
                    long mUsed = acc.Quota?.UsedBytes ?? 0;
                    poolTotal += mTotal;
                    poolUsed += mUsed;
                    var mPct = mTotal > 0 ? (double)mUsed / mTotal * 100.0 : 0;

                    memberDetails.Add(new
                    {
                        PriorityBadge = $"P{m.Priority}",
                        AccountName = acc.DisplayName + (m.IsEnabled ? "" : " (off)"),
                        UsagePercent = mPct,
                        QuotaText = hasQuota
                            ? $"{FormatBytes(mUsed)} / {FormatBytes(mTotal)}"
                            : "No quota data"
                    });
                }
                else
                {
                    memberDetails.Add(new
                    {
                        PriorityBadge = $"P{m.Priority}",
                        AccountName = m.AccountId + " (missing)",
                        UsagePercent = 0.0,
                        QuotaText = "Account not found"
                    });
                }
            }

            var poolPct = poolTotal > 0 ? (double)poolUsed / poolTotal * 100.0 : 0;

            return new
            {
                p.PoolId, p.Name, p.LocalPath,
                StrategyLabel = p.DefaultStrategy.ToString(),
                ModeLabel = p.Mode.ToString(),
                CapacityText = poolTotal > 0
                    ? $"{FormatBytes(poolUsed)} / {FormatBytes(poolTotal)}"
                    : $"{p.Members.Count} member(s) - no quota data",
                UsagePercent = poolPct,
                UsagePercentText = poolTotal > 0 ? $"{poolPct:F1}% used" : "",
                MemberDetails = memberDetails
            };
        }).ToList();
    }

    // ── Create Pool ─────────────────────────────────────────────────────

    private async void CreatePool_Click(object sender, RoutedEventArgs e)
    {
        var accounts = await App.Store.GetAllAccountsAsync();
        if (accounts.Count == 0)
        {
            await ShowErrorAsync("No Accounts", "Connect at least one cloud account before creating a pool.");
            return;
        }

        var nameBox = new TextBox { Header = "Pool Name", PlaceholderText = "My Cloud Pool" };
        var pathBox = new TextBox
        {
            Header = "Local Path",
            PlaceholderText = @"C:\Users\You\Clouder\MyPool",
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Clouder", "Pool1")
        };
        var strategyBox = new ComboBox
        {
            Header = "Placement Strategy",
            ItemsSource = Enum.GetNames<PlacementStrategy>(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var strategyDesc = new TextBlock
        {
            Text = StrategyDescriptions[PlacementStrategy.FillFirst],
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Padding = new Thickness(0, 0, 0, 4)
        };
        strategyBox.SelectionChanged += (_, _) =>
        {
            if (strategyBox.SelectedItem is string name && Enum.TryParse<PlacementStrategy>(name, out var s))
                strategyDesc.Text = StrategyDescriptions[s];
        };

        var accountChecks = new StackPanel { Spacing = 4 };
        foreach (var acc in accounts)
        {
            accountChecks.Children.Add(new CheckBox
            {
                Content = $"{acc.DisplayName} ({acc.ProviderId})",
                Tag = acc.AccountId,
                IsChecked = true
            });
        }

        var content = new StackPanel
        {
            Spacing = 12,
            Children = { nameBox, pathBox, strategyBox, strategyDesc, new TextBlock { Text = "Members:" }, accountChecks }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create Storage Pool",
            Content = new ScrollViewer { Content = content, MaxHeight = 400 },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var poolName = nameBox.Text.Trim();
        var localPath = pathBox.Text.Trim();
        var (nameValid, nameErr) = InputValidator.ValidatePoolName(poolName);
        var (pathValid, pathErr) = InputValidator.ValidateLocalPath(localPath);
        if (!nameValid) { await ShowErrorAsync("Validation", nameErr!); return; }
        if (!pathValid) { await ShowErrorAsync("Validation", pathErr!); return; }

        var strategy = Enum.Parse<PlacementStrategy>((string)strategyBox.SelectedItem);
        var selectedAccounts = accountChecks.Children
            .OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select((cb, i) =>
            {
                var accId = (string)cb.Tag;
                var acc = accounts.First(a => a.AccountId == accId);
                return new PoolMember
                {
                    AccountId = accId,
                    ProviderId = acc.ProviderId,
                    Priority = i,
                    IsEnabled = true
                };
            }).ToList();

        var pool = new StoragePool
        {
            PoolId = $"pool-{Guid.NewGuid():N}",
            Name = poolName,
            LocalPath = localPath,
            Mode = PoolMode.Auto,
            DefaultStrategy = strategy,
            Members = selectedAccounts
        };

        Directory.CreateDirectory(localPath);
        await App.Store.UpsertPoolAsync(pool);
        App.SyncService?.WatchPool(pool);
        ClouderLog.Info($"Pool created: {poolName} at {localPath} with {selectedAccounts.Count} member(s)");
        await RefreshPoolsAsync();
    }

    // ── Edit Pool ───────────────────────────────────────────────────────

    private async void EditPool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId }) return;

        var pool = await App.Store.GetPoolAsync(poolId);
        if (pool == null) return;

        var nameBox = new TextBox { Header = "Pool Name", Text = pool.Name };
        var strategyBox = new ComboBox
        {
            Header = "Placement Strategy",
            ItemsSource = Enum.GetNames<PlacementStrategy>(),
            SelectedItem = pool.DefaultStrategy.ToString(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var strategyDesc = new TextBlock
        {
            Text = StrategyDescriptions[pool.DefaultStrategy],
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Padding = new Thickness(0, 0, 0, 4)
        };
        strategyBox.SelectionChanged += (_, _) =>
        {
            if (strategyBox.SelectedItem is string name && Enum.TryParse<PlacementStrategy>(name, out var s))
                strategyDesc.Text = StrategyDescriptions[s];
        };
        var modeBox = new ComboBox
        {
            Header = "Pool Mode",
            ItemsSource = Enum.GetNames<PoolMode>(),
            SelectedItem = pool.Mode.ToString(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Edit Pool",
            Content = new StackPanel { Spacing = 12, Children = { nameBox, strategyBox, strategyDesc, modeBox } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        pool.Name = nameBox.Text.Trim();
        pool.DefaultStrategy = Enum.Parse<PlacementStrategy>((string)strategyBox.SelectedItem);
        pool.Mode = Enum.Parse<PoolMode>((string)modeBox.SelectedItem);
        await App.Store.UpsertPoolAsync(pool);
        await RefreshPoolsAsync();
    }

    // ── Delete Pool ─────────────────────────────────────────────────────

    private async void DeletePool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId }) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete Pool",
            Content = "This will remove the pool configuration. Cloud files will not be deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            App.SyncService?.UnwatchPool(poolId);
            await App.Store.DeletePoolAsync(poolId);
            await RefreshPoolsAsync();
        }
    }

    // ── Open in Explorer ────────────────────────────────────────────────

    private async void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId }) return;

        var pool = await App.Store.GetPoolAsync(poolId);
        if (pool == null) return;

        Directory.CreateDirectory(pool.LocalPath);
        System.Diagnostics.Process.Start("explorer.exe", pool.LocalPath);
    }

    // ── Sync Now ─────────────────────────────────────────────────────────

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId } btn) return;
        if (App.SyncService == null)
        {
            await ShowErrorAsync("Sync not available", "The sync service is not running.");
            return;
        }

        btn.IsEnabled = false;
        try
        {
            var progressDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Syncing...",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new ProgressBar { IsIndeterminate = true },
                        new TextBlock { Text = "Scanning and uploading files to cloud...", Name = "SyncStatusText" }
                    }
                },
                CloseButtonText = "Run in Background"
            };

            var syncTask = App.SyncService.SyncPoolAsync(poolId, new Progress<Clouder.Storage.SyncProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (progressDialog.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                    {
                        tb.Text = $"Progress: {p.Completed}/{p.Total} — {p.Synced} uploaded, {p.Skipped} skipped, {p.Failed} failed";
                    }
                });
            }));

            // Show dialog — user can close it to let sync run in background
            var dialogTask = progressDialog.ShowAsync();

            await syncTask;

            progressDialog.Hide();

            // Refresh quotas after sync
            await RefreshPoolsAsync();

            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Sync Complete",
                Content = "All local files have been synced to the cloud.",
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Sync failed for pool {poolId}", ex);
            await ShowErrorAsync("Sync Failed", ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ── Pool Management ─────────────────────────────────────────────────

    private async void ManagePool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId }) return;

        var pool = await App.Store.GetPoolAsync(poolId);
        if (pool == null) return;

        var actions = new StackPanel { Spacing = 12 };

        var stripeAllBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = "Stripe Everything", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = "Split every file into chunks stored on different accounts. "
                                     + "Warning: this is NOT redundancy — if any one account is lost, "
                                     + "its chunks (and thus the whole file) become unreadable.",
                                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                            }
                        }
                    }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12)
        };

        var unstripeAllBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = "Unstripe Everything", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = "Consolidate all striped files so each file lives on a single account. "
                                     + "Files will be reassembled and placed according to the pool's strategy.",
                                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                            }
                        }
                    }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12)
        };

        var autoReorgBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = "Auto Reorganize", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = "Rebalance files across accounts based on the current placement strategy. "
                                     + "Moves files from fuller drives to emptier ones to optimize usage.",
                                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                            }
                        }
                    }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12)
        };

        // File stats
        int totalFiles = 0, stripedFiles = 0;
        foreach (var member in pool.Members)
        {
            try
            {
                var items = await App.Store.GetItemsByAccountAsync(member.AccountId);
                totalFiles += items.Count(i => i.Type == CloudItemType.File);
            }
            catch { }
        }

        var stripePlans = new List<string>();
        // Check for striped files across all items in this pool
        foreach (var member in pool.Members)
        {
            try
            {
                var items = await App.Store.GetItemsByAccountAsync(member.AccountId);
                foreach (var item in items.Where(i => i.Type == CloudItemType.File))
                {
                    var plans = await App.Store.GetStripePlansAsync(item.Id);
                    if (plans.Count > 0 && !stripePlans.Contains(item.Id))
                    {
                        stripePlans.Add(item.Id);
                        stripedFiles++;
                    }
                }
            }
            catch { }
        }

        var statsBar = new InfoBar
        {
            Title = "Pool Statistics",
            Message = $"{totalFiles} file(s) tracked, {stripedFiles} striped across multiple accounts",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        };

        actions.Children.Add(statsBar);
        actions.Children.Add(stripeAllBtn);
        actions.Children.Add(unstripeAllBtn);
        actions.Children.Add(autoReorgBtn);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Manage Pool: {pool.Name}",
            Content = new ScrollViewer { Content = actions, MaxHeight = 500 },
            CloseButtonText = "Close"
        };

        // Wire up button actions — they close the dialog and run the operation
        stripeAllBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            await StripeAllAsync(pool);
        };
        unstripeAllBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            await UnstripeAllAsync(pool);
        };
        autoReorgBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            await AutoReorganizeAsync(pool);
        };

        await dialog.ShowAsync();
    }

    private async Task StripeAllAsync(StoragePool pool)
    {
        if (pool.Members.Count(m => m.IsEnabled) < 2)
        {
            await ShowErrorAsync("Cannot stripe", "Striping requires at least 2 enabled member accounts in the pool.");
            return;
        }
        if (App.SyncService == null) { await ShowErrorAsync("Unavailable", "Sync service not running."); return; }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Stripe All Files",
            Content = "This downloads each non-striped file, splits it into chunks across your "
                    + "member accounts, and deletes the original single-account copy.\n\n"
                    + "This moves real data and may take a while and use bandwidth. Continue?",
            PrimaryButtonText = "Start Striping",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // Collect distinct tracked files in this pool.
        var fileIds = await GetPoolFileIdsAsync(pool);
        int done = 0, skipped = 0, failed = 0;

        foreach (var fileId in fileIds)
        {
            try
            {
                var ok = await App.SyncService.ForceStripeAsync(fileId);
                if (ok) done++; else skipped++;
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Force-stripe failed for {fileId}", ex);
                failed++;
            }
        }

        await RefreshPoolsAsync();
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Striping Complete",
            Content = $"Striped {done} file(s). {skipped} skipped (already striped or too small), {failed} failed.",
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    private async Task UnstripeAllAsync(StoragePool pool)
    {
        if (App.SyncService == null) { await ShowErrorAsync("Unavailable", "Sync service not running."); return; }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Unstripe All Files",
            Content = "This reassembles every striped file and stores it on a single account, "
                    + "then deletes the chunks.\n\nThis moves real data and may take a while. Continue?",
            PrimaryButtonText = "Start Unstriping",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var fileIds = await GetPoolFileIdsAsync(pool);
        int done = 0, skipped = 0, failed = 0;

        foreach (var fileId in fileIds)
        {
            try
            {
                var plans = await App.Store.GetStripePlansAsync(fileId);
                if (plans.Count == 0) { skipped++; continue; }
                var ok = await App.SyncService.ConsolidateAsync(fileId);
                if (ok) done++; else failed++;
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Consolidate failed for {fileId}", ex);
                failed++;
            }
        }

        await RefreshPoolsAsync();
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Unstriping Complete",
            Content = $"Consolidated {done} file(s). {skipped} were not striped, {failed} failed.",
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    /// <summary>Returns the distinct tracked file ids belonging to a pool.</summary>
    private static async Task<List<string>> GetPoolFileIdsAsync(StoragePool pool)
    {
        var ids = new HashSet<string>();
        var prefix = pool.PoolId + "|";
        foreach (var member in pool.Members)
        {
            try
            {
                var items = await App.Store.GetItemsByAccountAsync(member.AccountId);
                foreach (var item in items.Where(i => i.Type == CloudItemType.File && i.Id.StartsWith(prefix)))
                    ids.Add(item.Id);
            }
            catch { }
        }
        return ids.ToList();
    }

    private async Task AutoReorganizeAsync(StoragePool pool)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Auto Reorganize",
            Content = "This will analyze file distribution and create a plan to rebalance files "
                    + "according to the pool's placement strategy.\n\n"
                    + "Files will be moved from fuller drives to emptier ones. "
                    + "This requires active provider connections.",
            PrimaryButtonText = "Reorganize",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var poolManager = new Clouder.Storage.StoragePoolManager(App.Store, App.Providers);
            var plan = await poolManager.PlanReorganizationAsync(pool.PoolId, 0);

            if (plan.Moves.Count == 0)
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Already Balanced",
                    Content = "The pool is already well-balanced. No file moves are needed.",
                    CloseButtonText = "OK"
                }.ShowAsync();
                return;
            }

            var moveConfirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reorganization Plan",
                Content = $"Plan: move {plan.Moves.Count} file(s), freeing {FormatBytes(plan.FreedBytes)}.\n\n"
                        + "Would you like to execute this plan now?",
                PrimaryButtonText = "Execute",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await moveConfirm.ShowAsync() != ContentDialogResult.Primary) return;

            var progress = new Progress<Clouder.Core.Storage.ReorgProgress>(p =>
            {
                ClouderLog.Info($"Reorg progress: {p.CompletedMoves}/{p.TotalMoves} moves, "
                              + $"{FormatBytes(p.TransferredBytes)}/{FormatBytes(p.TotalBytes)}");
            });

            await poolManager.ExecuteReorganizationAsync(plan, progress);

            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reorganization Complete",
                Content = $"Moved {plan.Moves.Count} file(s), freed {FormatBytes(plan.FreedBytes)}.",
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Auto reorganization failed", ex);
            await ShowErrorAsync("Reorganization Failed", ex.Message);
        }

        await RefreshPoolsAsync();
    }

    // ── Drive Priorities ────────────────────────────────────────────────

    private async void EditPriorities_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string poolId }) return;

        var pool = await App.Store.GetPoolAsync(poolId);
        if (pool == null) return;

        var accounts = await App.Store.GetAllAccountsAsync();
        var accountMap = accounts.ToDictionary(a => a.AccountId);

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new InfoBar
        {
            Title = "Fill order",
            Message = "Accounts are filled tier by tier, lowest number first. Accounts sharing a tier "
                    + "are used together, with the pool's placement strategy choosing between them — "
                    + "so tier 0 on three accounts spreads files across all three, and tier 1 is only "
                    + "touched once those three are full.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        });

        panel.Children.Add(new InfoBar
        {
            Title = "Limits are optional",
            Message = "Set a usage cap to stop this pool growing past a certain size on an account, "
                    + "or a reserve to always leave space free — useful when you also keep unrelated "
                    + "files in that cloud storage. 0 means no limit.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        });

        var controls = new List<(string AccountId, NumberBox Tier, ToggleSwitch Enabled, NumberBox Cap, NumberBox Reserve)>();

        foreach (var member in pool.Members.OrderBy(m => m.Priority).ThenBy(m => m.AccountId, StringComparer.Ordinal))
        {
            var account = accountMap.GetValueOrDefault(member.AccountId);
            var accName = account?.DisplayName ?? member.AccountId;
            var quotaText = account?.Quota is { TotalBytes: > 0 } q
                ? $"{FormatBytes(q.UsedBytes)} of {FormatBytes(q.TotalBytes)} used"
                : "no quota data";

            var tierBox = new NumberBox
            {
                Header = "Fill tier",
                Value = member.Priority,
                Minimum = 0,
                Maximum = 99,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };

            var enabledSwitch = new ToggleSwitch
            {
                Header = "Use this account",
                IsOn = member.IsEnabled
            };

            var capBox = new NumberBox
            {
                Header = "Usage cap (GB, 0 = no limit)",
                Value = BytesToGb(member.MaxUsageBytes),
                Minimum = 0,
                Maximum = 100000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };

            var reserveBox = new NumberBox
            {
                Header = "Keep free (GB, 0 = none)",
                Value = BytesToGb(member.ReserveBytes),
                Minimum = 0,
                Maximum = 100000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };

            var limitsGrid = new Grid { ColumnSpacing = 12 };
            limitsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            limitsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(capBox, 0);
            Grid.SetColumn(reserveBox, 1);
            limitsGrid.Children.Add(capBox);
            limitsGrid.Children.Add(reserveBox);

            var header = new StackPanel { Spacing = 1 };
            header.Children.Add(new TextBlock { Text = accName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = quotaText,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var topRow = new Grid { ColumnSpacing = 12 };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(header, 0);
            Grid.SetColumn(tierBox, 1);
            topRow.Children.Add(header);
            topRow.Children.Add(tierBox);

            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children = { topRow, limitsGrid, enabledSwitch }
                }
            };

            panel.Children.Add(card);
            controls.Add((member.AccountId, tierBox, enabledSwitch, capBox, reserveBox));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Accounts in {pool.Name}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var (accountId, tier, enabled, cap, reserve) in controls)
        {
            var member = pool.Members.First(m => m.AccountId == accountId);
            member.Priority = (int)tier.Value;
            member.IsEnabled = enabled.IsOn;
            member.MaxUsageBytes = GbToBytes(cap.Value);
            member.ReserveBytes = GbToBytes(reserve.Value);
        }

        if (!pool.Members.Any(m => m.IsEnabled))
        {
            await ShowErrorAsync("At least one account is required",
                "A pool needs one enabled account to store anything. Nothing was changed.");
            return;
        }

        await App.Store.UpsertPoolAsync(pool);
        ClouderLog.Info($"Updated member settings for pool '{pool.Name}'");
        await RefreshPoolsAsync();
    }

    private static double BytesToGb(long bytes) =>
        bytes <= 0 ? 0 : Math.Round(bytes / (1024.0 * 1024 * 1024), 2);

    private static long GbToBytes(double gb) =>
        double.IsNaN(gb) || gb <= 0 ? 0 : (long)(gb * 1024 * 1024 * 1024);

    // ── Merge Pools ─────────────────────────────────────────────────────

    private async void MergePools_Click(object sender, RoutedEventArgs e)
    {
        var pools = await App.Store.GetAllPoolsAsync();
        if (pools.Count < 2)
        {
            await ShowErrorAsync("Cannot merge", "You need at least 2 pools to merge.");
            return;
        }

        var targetCombo = new ComboBox
        {
            Header = "Keep this pool (target)",
            ItemsSource = pools.Select(p => p.Name).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var sourceChecks = new StackPanel { Spacing = 4 };
        void RebuildSourceChecks()
        {
            sourceChecks.Children.Clear();
            for (int i = 0; i < pools.Count; i++)
            {
                if (i == targetCombo.SelectedIndex) continue;
                sourceChecks.Children.Add(new CheckBox
                {
                    Content = $"{pools[i].Name}  ({pools[i].Members.Count} member(s), {pools[i].LocalPath})",
                    Tag = pools[i].PoolId,
                    IsChecked = false
                });
            }
        }
        RebuildSourceChecks();
        targetCombo.SelectionChanged += (_, _) => RebuildSourceChecks();

        var mergeInfo = new InfoBar
        {
            Title = "How merge works",
            Message = "All accounts from the selected pools will be added to the target pool. "
                    + "Duplicate accounts are skipped. The source pools are then deleted. "
                    + "The target pool keeps its name, path, and strategy.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        };

        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                mergeInfo,
                targetCombo,
                new TextBlock { Text = "Merge these pools into it:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                sourceChecks
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Merge Pools",
            Content = new ScrollViewer { Content = content, MaxHeight = 450 },
            PrimaryButtonText = "Merge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (targetCombo.SelectedIndex < 0) return;
        var target = pools[targetCombo.SelectedIndex];

        var sourceIds = sourceChecks.Children
            .OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag)
            .ToList();

        if (sourceIds.Count == 0)
        {
            await ShowErrorAsync("Nothing selected", "Select at least one pool to merge into the target.");
            return;
        }

        var existingAccountIds = target.Members.Select(m => m.AccountId).ToHashSet();
        int added = 0;

        foreach (var sourceId in sourceIds)
        {
            var source = pools.First(p => p.PoolId == sourceId);
            foreach (var member in source.Members)
            {
                if (existingAccountIds.Contains(member.AccountId))
                    continue;

                target.Members.Add(new PoolMember
                {
                    AccountId = member.AccountId,
                    ProviderId = member.ProviderId,
                    Priority = target.Members.Count,
                    IsEnabled = member.IsEnabled,
                    RootFolderId = member.RootFolderId
                });
                existingAccountIds.Add(member.AccountId);
                added++;
            }

            App.SyncService?.UnwatchPool(sourceId);
            await App.Store.DeletePoolAsync(sourceId);
        }

        await App.Store.UpsertPoolAsync(target);
        ClouderLog.Info($"Merged {sourceIds.Count} pool(s) into '{target.Name}' - {added} member(s) added");
        await RefreshPoolsAsync();

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Pools merged",
            Content = $"Merged {sourceIds.Count} pool(s) into \"{target.Name}\".\n"
                    + $"{added} new account(s) added, {sourceIds.Count} pool(s) removed.",
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
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
}
