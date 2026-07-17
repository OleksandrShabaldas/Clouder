using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder_App.Pages;

public sealed partial class SettingsPage : Page
{
    private ClouderConfig _config = new();
    private ConfigService _configService = null!;
    private bool _loading = true;

    public List<string> StrategyOptions { get; } = Enum.GetNames<PlacementStrategy>().ToList();
    public List<string> ConflictOptions { get; } = Enum.GetNames<ConflictResolution>().ToList();

    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _configService = new ConfigService(App.Store);
        _config = await _configService.LoadAsync();
        ApplyConfigToUI();
        await RefreshRulesAsync();
        _loading = false;
    }

    private void ApplyConfigToUI()
    {
        _loading = true;

        AutoStartToggle.IsOn = _config.AutoStartOnLogin;
        TrayToggle.IsOn = _config.MinimizeToTray;
        NotifyToggle.IsOn = _config.ShowNotifications;
        DefaultStrategyBox.SelectedItem = _config.DefaultStrategy.ToString();

        SyncIntervalBox.Value = _config.SyncIntervalSeconds;
        ConflictBox.SelectedItem = _config.ConflictPolicy.ToString();
        MaxConcurrentBox.Value = _config.MaxConcurrentTransfers;
        UploadLimitBox.Value = _config.MaxUploadBytesPerSec / (1024 * 1024);
        DownloadLimitBox.Value = _config.MaxDownloadBytesPerSec / (1024 * 1024);

        CacheLimitBox.Value = _config.CacheSizeLimitMb;
        DehydrateDaysBox.Value = _config.AutoDehydrateDays;
        MinFreeDiskBox.Value = _config.MinFreeDiskMb;

        StripeThresholdBox.Value = _config.StripingPromptThresholdMb;
        AutoReorgToggle.IsOn = _config.AutoReorganizeOnFull;

        _loading = false;
    }

    private void ReadConfigFromUI()
    {
        _config.AutoStartOnLogin = AutoStartToggle.IsOn;
        _config.MinimizeToTray = TrayToggle.IsOn;
        _config.ShowNotifications = NotifyToggle.IsOn;
        _config.DefaultStrategy = Enum.TryParse<PlacementStrategy>(DefaultStrategyBox.SelectedItem as string, out var s)
            ? s : PlacementStrategy.FillFirst;

        _config.SyncIntervalSeconds = (int)SyncIntervalBox.Value;
        _config.ConflictPolicy = Enum.TryParse<ConflictResolution>(ConflictBox.SelectedItem as string, out var c)
            ? c : ConflictResolution.NewestWins;
        _config.MaxConcurrentTransfers = (int)MaxConcurrentBox.Value;
        _config.MaxUploadBytesPerSec = (long)(UploadLimitBox.Value * 1024 * 1024);
        _config.MaxDownloadBytesPerSec = (long)(DownloadLimitBox.Value * 1024 * 1024);

        _config.CacheSizeLimitMb = (long)CacheLimitBox.Value;
        _config.AutoDehydrateDays = (int)DehydrateDaysBox.Value;
        _config.MinFreeDiskMb = (long)MinFreeDiskBox.Value;

        _config.StripingPromptThresholdMb = (long)StripeThresholdBox.Value;
        _config.AutoReorganizeOnFull = AutoReorgToggle.IsOn;
    }

    private async void Setting_Changed(object sender, object e)
    {
        if (_loading) return;
        ReadConfigFromUI();
        await _configService.SaveAsync(_config);
        await App.ReloadConfigAsync();   // apply settings live (sync, auto-start, intervals)
        SaveIndicator.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    // ── File Rules ──────────────────────────────────────────────────────

    private async Task RefreshRulesAsync()
    {
        var rules = await App.Store.GetFileRulesAsync();
        RulesListView.ItemsSource = rules.Select(r => new
        {
            r.RuleId,
            r.Name,
            r.IsEnabled,
            TypeLabel = r.Type.ToString(),
            Description = FormatRuleDescription(r)
        }).ToList();
    }

    private static string FormatRuleDescription(FileRule r) => r.Type switch
    {
        FileRuleType.FileExtension => $"Pattern: {r.Pattern} → {r.Action}",
        FileRuleType.MinFileSize => $"Files > {r.Pattern} → {r.Action}",
        FileRuleType.MaxFileSize => $"Files < {r.Pattern} → {r.Action}",
        FileRuleType.FolderPath => $"Path: {r.Pattern} → {r.Action}",
        FileRuleType.ExcludePattern => $"Exclude: {r.Pattern}",
        _ => r.Pattern
    };

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "Rule name", PlaceholderText = "Route videos to MEGA" };
        var typeBox = new ComboBox
        {
            Header = "Rule type",
            ItemsSource = Enum.GetNames<FileRuleType>(),
            SelectedIndex = 0
        };
        var patternBox = new TextBox
        {
            Header = "Pattern",
            PlaceholderText = "*.mp4, *.mkv (comma-separated for extensions)"
        };
        var actionBox = new ComboBox
        {
            Header = "Action",
            ItemsSource = Enum.GetNames<FileRuleAction>(),
            SelectedIndex = 0
        };
        var priorityBox = new NumberBox
        {
            Header = "Priority (higher = evaluated first)",
            Value = 0, Minimum = 0, Maximum = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var accounts = await App.Store.GetAllAccountsAsync();
        var targetBox = new ComboBox
        {
            Header = "Target account (for routing rules)",
            ItemsSource = accounts.Select(a => $"{a.DisplayName} ({a.AccountId})").Prepend("(none)").ToList(),
            SelectedIndex = 0
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add File Rule",
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { nameBox, typeBox, patternBox, actionBox, targetBox, priorityBox }
                },
                MaxHeight = 450
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var targetIdx = targetBox.SelectedIndex;
        string? targetAccountId = targetIdx > 0 ? accounts[targetIdx - 1].AccountId : null;

        var rule = new FileRule
        {
            RuleId = $"rule-{Guid.NewGuid():N}",
            Name = nameBox.Text.Trim(),
            Type = Enum.Parse<FileRuleType>((string)typeBox.SelectedItem),
            Pattern = patternBox.Text.Trim(),
            Action = Enum.Parse<FileRuleAction>((string)actionBox.SelectedItem),
            TargetAccountId = targetAccountId,
            Priority = (int)priorityBox.Value,
            IsEnabled = true
        };

        if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Pattern)) return;

        await App.Store.UpsertFileRuleAsync(rule);
        await RefreshRulesAsync();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ruleId })
        {
            await App.Store.DeleteFileRuleAsync(ruleId);
            await RefreshRulesAsync();
        }
    }

    private async void RuleToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { Tag: string ruleId } toggle) return;

        var rules = await App.Store.GetFileRulesAsync();
        var rule = rules.FirstOrDefault(r => r.RuleId == ruleId);
        if (rule == null) return;

        rule.IsEnabled = toggle.IsOn;
        await App.Store.UpsertFileRuleAsync(rule);
    }
}
