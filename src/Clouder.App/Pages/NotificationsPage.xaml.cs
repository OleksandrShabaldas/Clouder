using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Validation;
using Clouder.Email;

namespace Clouder_App.Pages;

public sealed partial class NotificationsPage : Page
{
    private IReadOnlyList<AppNotification> _allNotifications = [];
    private string _activeFilter = "All";

    public NotificationsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshNotificationsAsync();
    }

    private async Task RefreshNotificationsAsync()
    {
        _allNotifications = await App.Store.GetNotificationsAsync(limit: 200);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = _activeFilter switch
        {
            "Unread" => _allNotifications.Where(n => !n.IsRead),
            "Critical" => _allNotifications.Where(n => n.Severity == NotificationSeverity.Critical),
            "Warning" => _allNotifications.Where(n => n.Severity == NotificationSeverity.Warning),
            "Info" => _allNotifications.Where(n => n.Severity == NotificationSeverity.Info),
            _ => _allNotifications.AsEnumerable()
        };

        var items = filtered.Select(n => new NotificationViewModel(n)).ToList();
        NotificationsListView.ItemsSource = items;

        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsListView.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshBtn.IsEnabled = false;
        try
        {
            if (App.EmailChecker != null)
                await App.EmailChecker.RunCheckAsync();
            await RefreshNotificationsAsync();
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
        }
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        await App.Store.MarkAllNotificationsReadAsync();
        await RefreshNotificationsAsync();
    }

    private async void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await App.Store.MarkNotificationReadAsync(id);
            await RefreshNotificationsAsync();
        }
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        var filterButtons = new[] { FilterAll, FilterUnread, FilterCritical, FilterWarning, FilterInfo };
        foreach (var btn in filterButtons)
            btn.IsChecked = btn == clicked;

        _activeFilter = clicked.Content?.ToString() ?? "All";
        ApplyFilter();
    }

    // ── Email configuration dialog ──────────────────────────────────────

    private async void ConfigureEmail_Click(object sender, RoutedEventArgs e)
    {
        var accounts = await App.Store.GetAllAccountsAsync();
        if (accounts.Count == 0)
        {
            await ShowErrorAsync("No accounts", "Connect a cloud storage account first, then configure email monitoring.");
            return;
        }

        var accountCombo = new ComboBox
        {
            Header = "Account",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = accounts.Select(a => $"{a.DisplayName} ({a.Email ?? a.AccountId})").ToList(),
            SelectedIndex = 0
        };

        var methodCombo = new ComboBox
        {
            Header = "Access Method",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "IMAP", "Gmail API (Google accounts only)" },
            SelectedIndex = 0
        };

        var imapPanel = new StackPanel { Spacing = 8 };
        var hostBox = new TextBox { Header = "IMAP Server", PlaceholderText = "imap.gmail.com" };
        var portBox = new NumberBox { Header = "Port", Value = 993, Minimum = 1, Maximum = 65535 };
        var sslCheck = new CheckBox { Content = "Use SSL/TLS", IsChecked = true };
        var userBox = new TextBox { Header = "Username / Email", PlaceholderText = "you@example.com" };
        var passBox = new PasswordBox { Header = "Password / App Password" };
        imapPanel.Children.Add(hostBox);
        imapPanel.Children.Add(portBox);
        imapPanel.Children.Add(sslCheck);
        imapPanel.Children.Add(userBox);
        imapPanel.Children.Add(passBox);

        var intervalBox = new NumberBox
        {
            Header = "Check interval (minutes)",
            Value = 30,
            Minimum = 5,
            Maximum = 1440
        };

        var gmailInfo = new InfoBar
        {
            Title = "Gmail API",
            Message = "Uses the same Google OAuth credentials as your Drive connection. "
                    + "You may be prompted to grant Gmail read access on first check.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false,
            Visibility = Visibility.Collapsed
        };

        var imapInfo = new InfoBar
        {
            Title = "Gmail users",
            Message = "Use imap.gmail.com with an App Password. "
                    + "Generate one at myaccount.google.com → Security → 2-Step Verification → App passwords.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false
        };

        methodCombo.SelectionChanged += (_, _) =>
        {
            bool isGmail = methodCombo.SelectedIndex == 1;
            imapPanel.Visibility = isGmail ? Visibility.Collapsed : Visibility.Visible;
            gmailInfo.Visibility = isGmail ? Visibility.Visible : Visibility.Collapsed;
            imapInfo.Visibility = isGmail ? Visibility.Collapsed : Visibility.Visible;
        };

        accountCombo.SelectionChanged += async (_, _) =>
        {
            if (accountCombo.SelectedIndex < 0 || accountCombo.SelectedIndex >= accounts.Count) return;
            var acct = accounts[accountCombo.SelectedIndex];
            userBox.Text = acct.Email ?? "";

            if (acct.ProviderId == "google-drive")
            {
                hostBox.Text = "imap.gmail.com";
                methodCombo.SelectedIndex = 0;
            }
            else
            {
                hostBox.Text = "";
                methodCombo.SelectedIndex = 0;
            }

            var existing = await App.Store.GetEmailConfigAsync(acct.AccountId);
            if (existing != null)
            {
                methodCombo.SelectedIndex = existing.Method == EmailAccessMethod.GmailApi ? 1 : 0;
                hostBox.Text = existing.ImapHost ?? hostBox.Text;
                portBox.Value = existing.ImapPort;
                sslCheck.IsChecked = existing.UseSsl;
                userBox.Text = existing.ImapUsername ?? userBox.Text;
                intervalBox.Value = existing.CheckIntervalMinutes;
            }
        };

        // Trigger initial load
        if (accounts.Count > 0)
        {
            var first = accounts[0];
            userBox.Text = first.Email ?? "";
            if (first.ProviderId == "google-drive")
                hostBox.Text = "imap.gmail.com";

            var existing = await App.Store.GetEmailConfigAsync(first.AccountId);
            if (existing != null)
            {
                methodCombo.SelectedIndex = existing.Method == EmailAccessMethod.GmailApi ? 1 : 0;
                hostBox.Text = existing.ImapHost ?? hostBox.Text;
                portBox.Value = existing.ImapPort;
                sslCheck.IsChecked = existing.UseSsl;
                userBox.Text = existing.ImapUsername ?? userBox.Text;
                intervalBox.Value = existing.CheckIntervalMinutes;
            }
        }

        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(accountCombo);
        content.Children.Add(methodCombo);
        content.Children.Add(imapInfo);
        content.Children.Add(gmailInfo);
        content.Children.Add(imapPanel);
        content.Children.Add(intervalBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Configure Email Monitoring",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 500,
                Padding = new Thickness(0, 0, 12, 0)
            },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.None) return;

        if (accountCombo.SelectedIndex < 0 || accountCombo.SelectedIndex >= accounts.Count) return;
        var selectedAccount = accounts[accountCombo.SelectedIndex];

        if (result == ContentDialogResult.Secondary)
        {
            var existingConfig = await App.Store.GetEmailConfigAsync(selectedAccount.AccountId);
            if (existingConfig != null)
                await App.Store.DeleteEmailConfigAsync(existingConfig.ConfigId);
            ClouderLog.Info($"Email monitoring removed for {selectedAccount.DisplayName}");
            return;
        }

        bool useGmail = methodCombo.SelectedIndex == 1;

        var config = new EmailAccountConfig
        {
            ConfigId = $"emc-{selectedAccount.AccountId}",
            AccountId = selectedAccount.AccountId,
            Method = useGmail ? EmailAccessMethod.GmailApi : EmailAccessMethod.Imap,
            ImapHost = hostBox.Text.Trim(),
            ImapPort = (int)portBox.Value,
            UseSsl = sslCheck.IsChecked == true,
            ImapUsername = userBox.Text.Trim(),
            CheckIntervalMinutes = (int)intervalBox.Value,
            IsEnabled = true
        };

        if (!useGmail && !string.IsNullOrEmpty(passBox.Password))
        {
            config.ImapPasswordProtected = CredentialProtector.Protect(passBox.Password);
        }
        else if (!useGmail)
        {
            var existingConfig = await App.Store.GetEmailConfigAsync(selectedAccount.AccountId);
            config.ImapPasswordProtected = existingConfig?.ImapPasswordProtected;
        }

        if (!useGmail)
        {
            var (hostValid, hostErr) = InputValidator.ValidateNotEmpty(config.ImapHost ?? "", "IMAP Server");
            if (!hostValid) { await ShowErrorAsync("Validation", hostErr!); return; }
            if (config.ImapPasswordProtected == null)
            {
                await ShowErrorAsync("Validation", "Password is required for IMAP.");
                return;
            }
        }

        await App.Store.UpsertEmailConfigAsync(config);
        ClouderLog.Info($"Email monitoring configured for {selectedAccount.DisplayName} ({config.Method})");

        await ShowSuccessAsync("Email monitoring configured",
            $"Monitoring {(useGmail ? "Gmail API" : $"IMAP ({config.ImapHost})")} for {selectedAccount.DisplayName}.\n"
            + $"Checking every {config.CheckIntervalMinutes} minutes.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    private async Task ShowSuccessAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "",
                        FontSize = 36,
                        Foreground = new SolidColorBrush(Colors.Green)
                    },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
                }
            },
            CloseButtonText = "OK"
        }.ShowAsync();
    }
}

internal sealed class NotificationViewModel
{
    private readonly AppNotification _n;

    public NotificationViewModel(AppNotification n)
    {
        _n = n;
    }

    public string NotificationId => _n.NotificationId;
    public string Title => _n.Title;
    public string Body => _n.Body;
    public bool IsRead => _n.IsRead;

    public string SeverityGlyph => _n.Severity switch
    {
        NotificationSeverity.Critical => "",
        NotificationSeverity.Warning => "",
        _ => ""
    };

    public Brush SeverityBrush => _n.Severity switch
    {
        NotificationSeverity.Critical => new SolidColorBrush(Colors.Red),
        NotificationSeverity.Warning => new SolidColorBrush(Colors.Orange),
        _ => new SolidColorBrush(Colors.DodgerBlue)
    };

    public Windows.UI.Text.FontWeight TitleWeight => _n.IsRead
        ? Microsoft.UI.Text.FontWeights.Normal
        : Microsoft.UI.Text.FontWeights.SemiBold;

    public Thickness BorderThickness => _n.IsRead
        ? new Thickness(0)
        : new Thickness(2, 0, 0, 0);

    public Brush BorderBrush => SeverityBrush;

    public Visibility MarkReadVisibility => _n.IsRead ? Visibility.Collapsed : Visibility.Visible;

    public string TimeAgo
    {
        get
        {
            var elapsed = DateTime.UtcNow - _n.TimestampUtc;
            if (elapsed.TotalMinutes < 1) return "just now";
            if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            return _n.TimestampUtc.ToLocalTime().ToString("MMM d");
        }
    }

    public string AccountLabel => _n.RelatedAccountId != null ? $"Account: {_n.RelatedAccountId}" : "";
    public Visibility AccountLabelVisibility =>
        string.IsNullOrEmpty(_n.RelatedAccountId) ? Visibility.Collapsed : Visibility.Visible;
}
