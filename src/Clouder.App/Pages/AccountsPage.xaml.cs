using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Clouder.Core.Logging;
using Clouder.Core.Validation;
using Clouder.Providers.GoogleDrive;
using Clouder.Providers.Mega;

namespace Clouder_App.Pages;

public sealed partial class AccountsPage : Page
{
    private bool _subscribed;

    public AccountsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed && App.Connection != null)
        {
            App.Connection.StateChanged += OnConnectionStateChanged;
            _subscribed = true;
            Unloaded += (_, _) =>
            {
                if (App.Connection != null)
                    App.Connection.StateChanged -= OnConnectionStateChanged;
                _subscribed = false;
            };
        }
        await RefreshAccountsAsync();
    }

    private void OnConnectionStateChanged()
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshAccountsAsync());
    }

    private async Task RefreshAccountsAsync()
    {
        var accounts = await App.Store.GetAllAccountsAsync();
        AccountsListView.ItemsSource = accounts.Select(a =>
        {
            var hasQuota = a.Quota != null && a.Quota.TotalBytes > 0;
            var usagePct = hasQuota ? Math.Min(100.0, (double)a.Quota!.UsedBytes / a.Quota.TotalBytes * 100.0) : 0;
            var state = App.Connection?.GetState(a.AccountId) ?? ConnectionState.Unknown;
            var (statusText, statusColor) = MapState(state);
            return new
            {
                a.AccountId,
                a.DisplayName,
                Email = a.Email ?? "",
                ProviderLabel = a.ProviderId switch
                {
                    "google-drive" => "Google Drive",
                    "mega" => "MEGA",
                    _ => a.ProviderId
                },
                ProviderGlyph = a.ProviderId switch
                {
                    "google-drive" => "",
                    "mega" => "",
                    _ => ""
                },
                StatusText = statusText,
                StatusColor = statusColor,
                UsagePercent = usagePct,
                QuotaVisible = hasQuota ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed,
                QuotaUsedText = hasQuota ? $"{FormatBytes(a.Quota!.UsedBytes)} used" : "",
                QuotaTotalText = hasQuota ? $"{FormatBytes(a.Quota!.TotalBytes)} total ({usagePct:F1}%)" : ""
            };
        }).ToList();
    }

    private static (string Text, Microsoft.UI.Xaml.Media.SolidColorBrush Color) MapState(ConnectionState state)
    {
        return state switch
        {
            ConnectionState.Connected => ("Connected", Brush(Microsoft.UI.Colors.LimeGreen)),
            ConnectionState.Reconnecting => ("Reconnecting...", Brush(Microsoft.UI.Colors.Orange)),
            ConnectionState.Failed => ("Connection failed", Brush(Microsoft.UI.Colors.OrangeRed)),
            ConnectionState.NeedsCredentials => ("Reconnect needed", Brush(Microsoft.UI.Colors.OrangeRed)),
            _ => ("Unknown", Brush(Microsoft.UI.Colors.Gray))
        };
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string accountId } btn) return;
        btn.IsEnabled = false;
        try
        {
            var account = await App.Store.GetAccountAsync(accountId);
            if (account == null) return;

            // If the provider isn't registered (e.g. Google needs OAuth creds), rehydrate first.
            if (App.Providers.GetProvider(account.ProviderId) == null)
                await App.Connection.ReconnectAllAsync();

            var ok = await App.Connection.VerifyAccountAsync(account);
            if (!ok && account.ProviderId == "google-drive"
                    && App.Providers.GetProvider("google-drive") == null)
            {
                await ShowErrorAsync("Reconnect failed",
                    "No saved Google credentials found. Please reconnect this Google account using Connect Google Drive.");
            }
            await RefreshAccountsAsync();
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void RefreshQuotas_Click(object sender, RoutedEventArgs e)
    {
        RefreshQuotasBtn.IsEnabled = false;
        try
        {
            var accounts = await App.Store.GetAllAccountsAsync();
            int updated = 0;
            foreach (var account in accounts)
            {
                var provider = App.Providers.GetProvider(account.ProviderId);
                if (provider == null) continue;
                try
                {
                    var quota = await provider.GetQuotaAsync(account.AccountId);
                    account.Quota = quota;
                    await App.Store.UpsertAccountAsync(account);
                    updated++;
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"Failed to refresh quota for {account.DisplayName}", ex);
                }
            }
            await RefreshAccountsAsync();
            if (updated > 0)
                ClouderLog.Info($"Refreshed quotas for {updated} account(s)");
        }
        finally
        {
            RefreshQuotasBtn.IsEnabled = true;
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

    // ── Google Drive ────────────────────────────────────────────────────

    private async void AddGoogle_Click(object sender, RoutedEventArgs e)
    {
        var clientIdBox = new TextBox
        {
            Header = "Client ID",
            PlaceholderText = "paste Client ID here"
        };
        var clientSecretBox = new PasswordBox
        {
            Header = "Client Secret",
            PlaceholderText = "paste Client Secret here"
        };

        var guide = new StackPanel { Spacing = 16 };

        guide.Children.Add(BuildInfoBar(
            "First time? Follow these steps to get your credentials.",
            "This takes about 2 minutes. You only need to do it once per Google account."));

        guide.Children.Add(BuildStep("1", "Create a Google Cloud project",
            "Open the link below, sign in, and click \"Create Project\". Name it anything (e.g. \"Clouder\").",
            "https://console.cloud.google.com/projectcreate",
            "Open Google Cloud → New Project"));

        guide.Children.Add(BuildStep("2", "Enable the Google Drive API",
            "After creating the project, open this link and click \"Enable\".",
            "https://console.cloud.google.com/apis/library/drive.googleapis.com",
            "Open Drive API page → Enable"));

        guide.Children.Add(BuildStep("3", "Set up the consent screen",
            "Open the link below. Choose \"External\", click Create. Fill in the app name (e.g. \"Clouder\") and your email for both required email fields. Click Save and Continue through the Scopes page (skip it).",
            "https://console.cloud.google.com/apis/credentials/consent",
            "Open OAuth consent screen"));

        guide.Children.Add(BuildStep("4", "Add yourself as a test user (IMPORTANT)",
            "Still on the consent screen, go to the \"Test users\" section (or \"Audience\" tab). Click \"+ Add Users\" and enter the exact Gmail address you will sign in with. Without this, Google will block access with a 403 error.",
            "https://console.cloud.google.com/apis/credentials/consent",
            "Open OAuth consent screen → Test users"));

        guide.Children.Add(BuildStep("5", "Create OAuth credentials",
            "Open the link below. Click \"+ Create Credentials\" → \"OAuth client ID\". Choose \"Desktop app\" as the type. Click Create. You'll see a Client ID and Client Secret — copy both.",
            "https://console.cloud.google.com/apis/credentials",
            "Open Credentials page"));

        guide.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "6. Paste your credentials", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    BuildFieldWithPaste(clientIdBox),
                    BuildFieldWithPaste(clientSecretBox)
                }
            }
        });

        guide.Children.Add(new InfoBar
        {
            Title = "Getting a 403 \"Access blocked\" error?",
            Message = "Go back to Step 4 and make sure you added your Gmail address as a test user. The email must match exactly.",
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Connect Google Drive",
            Content = new ScrollViewer
            {
                Content = guide,
                MaxHeight = 500,
                Padding = new Thickness(0, 0, 12, 0)
            },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var clientId = clientIdBox.Text?.Trim() ?? "";
        var clientSecret = clientSecretBox.Password?.Trim() ?? "";

        var (cidValid, cidErr) = InputValidator.ValidateNotEmpty(clientId, "Client ID");
        var (secValid, secErr) = InputValidator.ValidateNotEmpty(clientSecret, "Client Secret");
        if (!cidValid) { await ShowErrorAsync("Validation", cidErr!); return; }
        if (!secValid) { await ShowErrorAsync("Validation", secErr!); return; }

        AddGoogleBtn.IsEnabled = false;
        try
        {
            ClouderLog.Info("Connecting Google Drive account...");
            var provider = new GoogleDriveProvider(new GoogleDriveSettings
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            });

            var infoDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Waiting for Google sign-in...",
                Content = "A browser window should have opened. Sign in with your Google account and grant access. This dialog will close automatically when done.",
                CloseButtonText = "Cancel"
            };
            _ = infoDialog.ShowAsync();

            var account = await provider.ConnectAccountAsync();
            infoDialog.Hide();

            await App.Store.UpsertAccountAsync(account);
            App.Providers.Register(provider);

            // Persist OAuth credentials so the provider can auto-reconnect on restart.
            await App.Connection.SaveGoogleCredentialsAsync(clientId, clientSecret);
            App.Connection.SetState(account.AccountId, ConnectionState.Connected);

            ClouderLog.Info($"Google Drive connected: {account.Email}");
            await RefreshAccountsAsync();

            await ShowSuccessAsync("Google Drive connected",
                $"Signed in as {account.DisplayName} ({account.Email})");
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Google Drive connection failed", ex);
            await ShowErrorAsync("Google Drive connection failed",
                $"{ex.Message}\n\nMake sure you completed all setup steps and the Client ID/Secret are correct.");
        }
        finally
        {
            AddGoogleBtn.IsEnabled = true;
        }
    }

    // ── MEGA ────────────────────────────────────────────────────────────

    private async void AddMega_Click(object sender, RoutedEventArgs e)
    {
        var emailBox = new TextBox { Header = "Email", PlaceholderText = "you@example.com" };
        var passwordBox = new PasswordBox { Header = "Password" };

        var guide = new StackPanel { Spacing = 16 };

        guide.Children.Add(BuildInfoBar(
            "Enter your MEGA account credentials.",
            "Your password is used to derive an encryption key and is never stored in plain text."));

        var signupLink = new HyperlinkButton
        {
            Content = "Don't have an account? Sign up at mega.nz (20 GB free)",
            NavigateUri = new Uri("https://mega.nz/register"),
            Padding = new Thickness(0)
        };
        guide.Children.Add(signupLink);

        guide.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = new StackPanel { Spacing = 12, Children = { emailBox, passwordBox } }
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Connect MEGA",
            Content = new ScrollViewer
            {
                Content = guide,
                MaxHeight = 400,
                Padding = new Thickness(0, 0, 12, 0)
            },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var email = emailBox.Text?.Trim() ?? "";
        var password = passwordBox.Password ?? "";
        var (emailValid, emailErr) = InputValidator.ValidateEmail(email);
        var (pwValid, pwErr) = InputValidator.ValidateNotEmpty(password, "Password");
        if (!emailValid) { await ShowErrorAsync("Validation", emailErr!); return; }
        if (!pwValid) { await ShowErrorAsync("Validation", pwErr!); return; }

        AddMegaBtn.IsEnabled = false;
        try
        {
            ClouderLog.Info($"Connecting MEGA account: {email}");
            var provider = new MegaProvider(new MegaSettings());
            var account = await provider.ConnectAccountAsync(email, password);
            await App.Store.UpsertAccountAsync(account);
            App.Providers.Register(provider);

            // Persist credentials so the session can be restored on restart / after expiry.
            await App.Connection.SaveMegaCredentialsAsync(account.AccountId, email, password);
            App.Connection.SetState(account.AccountId, ConnectionState.Connected);

            ClouderLog.Info($"MEGA connected: {email}");
            await RefreshAccountsAsync();

            var quotaMb = account.Quota != null
                ? $"{account.Quota.UsedBytes / (1024 * 1024)} MB used of {account.Quota.TotalBytes / (1024 * 1024)} MB"
                : "";
            await ShowSuccessAsync("MEGA connected", $"Signed in as {email}\n{quotaMb}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error("MEGA connection failed", ex);
            await ShowErrorAsync("MEGA connection failed",
                $"{ex.Message}\n\nCheck your email and password. If you have 2FA enabled, you may need an app-specific password.");
        }
        finally
        {
            AddMegaBtn.IsEnabled = true;
        }
    }

    // ── Disconnect ──────────────────────────────────────────────────────

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string accountId)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Disconnect Account",
                Content = "Are you sure? Files from this account will be removed from all pools.",
                PrimaryButtonText = "Disconnect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                var account = await App.Store.GetAccountAsync(accountId);

                // Remove provider-side tokens/session and stored credentials.
                if (account != null)
                {
                    try
                    {
                        var provider = App.Providers.GetProvider(account.ProviderId);
                        if (provider != null)
                            await provider.DisconnectAccountAsync(accountId);
                    }
                    catch (Exception ex)
                    {
                        ClouderLog.Error($"Error disconnecting provider for {accountId}", ex);
                    }
                }

                await App.Store.DeleteAccountAsync(accountId);
                await RefreshAccountsAsync();
            }
        }
    }

    // ── UI helpers ──────────────────────────────────────────────────────

    private static Grid BuildFieldWithPaste(Control field)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(field, 0);
        grid.Children.Add(field);

        var pasteBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = "Paste" }
                }
            },
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };
        pasteBtn.Click += async (_, _) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (field is TextBox tb) tb.Text = text.Trim();
                    else if (field is PasswordBox pb) pb.Password = text.Trim();
                }
            }
            catch { }
        };
        Grid.SetColumn(pasteBtn, 1);
        grid.Children.Add(pasteBtn);

        return grid;
    }

    private static StackPanel BuildStep(string number, string title, string description, string url, string linkText)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = number,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold
            }
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var step = new StackPanel { Spacing = 6 };
        step.Children.Add(header);
        step.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 13,
            Padding = new Thickness(32, 0, 0, 0)
        });
        step.Children.Add(new HyperlinkButton
        {
            Content = linkText,
            NavigateUri = new Uri(url),
            Padding = new Thickness(32, 0, 0, 0),
            FontSize = 13
        });

        return step;
    }

    private static InfoBar BuildInfoBar(string title, string message) => new()
    {
        Title = title,
        Message = message,
        Severity = InfoBarSeverity.Informational,
        IsOpen = true,
        IsClosable = false
    };

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
                    new FontIcon { Glyph = "", FontSize = 36, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green) },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
                }
            },
            CloseButtonText = "OK"
        }.ShowAsync();
    }
}
