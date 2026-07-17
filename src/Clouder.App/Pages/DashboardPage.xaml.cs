using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Clouder.Core.Models;

namespace Clouder_App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var accounts = await App.Store.GetAllAccountsAsync();
        var pools = await App.Store.GetAllPoolsAsync();

        // ── Accounts ──────────────────────────────────────────────
        if (accounts.Count == 0)
        {
            NoAccountsText.Visibility = Visibility.Visible;
            AccountsList.ItemsSource = null;
            TotalStorageUsed.Text = "No storage connected";
            TotalStorageTotal.Text = "";
            TotalStorageBar.Value = 0;
        }
        else
        {
            NoAccountsText.Visibility = Visibility.Collapsed;

            long totalBytes = 0, usedBytes = 0;
            foreach (var a in accounts)
            {
                if (a.Quota != null)
                {
                    totalBytes += a.Quota.TotalBytes;
                    usedBytes += a.Quota.UsedBytes;
                }
            }

            var totalPct = totalBytes > 0 ? (double)usedBytes / totalBytes * 100.0 : 0;
            TotalStorageBar.Value = totalPct;
            TotalStorageUsed.Text = totalBytes > 0
                ? $"{FormatBytes(usedBytes)} used"
                : $"{accounts.Count} account(s) connected";
            TotalStorageTotal.Text = totalBytes > 0
                ? $"{FormatBytes(totalBytes)} total ({totalPct:F1}%)"
                : "No quota data - refresh quotas on Accounts page";

            AccountsList.ItemsSource = accounts.Select(a =>
            {
                var hasQuota = a.Quota != null && a.Quota.TotalBytes > 0;
                var pct = hasQuota ? Math.Min(100.0, (double)a.Quota!.UsedBytes / a.Quota.TotalBytes * 100.0) : 0;
                return new
                {
                    a.DisplayName,
                    Email = a.Email ?? a.ProviderId,
                    ProviderGlyph = a.ProviderId switch
                    {
                        "google-drive" => "",
                        "mega" => "",
                        _ => ""
                    },
                    UsagePercent = pct,
                    StorageText = hasQuota
                        ? $"{FormatBytes(a.Quota!.UsedBytes)} / {FormatBytes(a.Quota.TotalBytes)} ({pct:F1}%)"
                        : a.ProviderId switch
                        {
                            "google-drive" => "Google Drive",
                            "mega" => "MEGA",
                            _ => a.ProviderId
                        }
                };
            }).ToList();
        }

        // ── Pools ─────────────────────────────────────────────────
        if (pools.Count == 0)
        {
            NoPoolsText.Visibility = Visibility.Visible;
            PoolsList.ItemsSource = null;
        }
        else
        {
            NoPoolsText.Visibility = Visibility.Collapsed;
            var accountMap = accounts.ToDictionary(a => a.AccountId);

            PoolsList.ItemsSource = pools.Select(p =>
            {
                long poolTotal = 0, poolUsed = 0;
                foreach (var m in p.Members)
                {
                    if (accountMap.TryGetValue(m.AccountId, out var acc) && acc.Quota != null)
                    {
                        poolTotal += acc.Quota.TotalBytes;
                        poolUsed += acc.Quota.UsedBytes;
                    }
                }

                var pct = poolTotal > 0 ? (double)poolUsed / poolTotal * 100.0 : 0;
                return new
                {
                    p.Name,
                    StrategyLabel = p.DefaultStrategy.ToString(),
                    SizeText = poolTotal > 0
                        ? $"{FormatBytes(poolUsed)} / {FormatBytes(poolTotal)}"
                        : $"{p.Members.Count} member(s)",
                    UsagePercent = pct,
                    MemberInfo = $"{p.Members.Count} account(s), {p.DefaultStrategy} strategy"
                };
            }).ToList();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }
}
