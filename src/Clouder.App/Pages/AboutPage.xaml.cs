using System.Reflection;
using Clouder.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clouder_App.Pages;

public sealed partial class AboutPage : Page
{
    /// <summary>Downloaded and waiting for the user to press Restart now.</summary>
    private Velopack.VelopackAsset? _readyToInstall;

    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "0.1.0"} (Preview)";

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder");
        DataPathText.Text = $"Database: {Path.Combine(dataDir, "clouder.db")}";
        LogPathText.Text = $"Logs: {Path.Combine(dataDir, "logs")}";

        InitUpdateUi();
    }

    private void InitUpdateUi()
    {
        var updates = App.Updates;

        if (updates is not { IsSupported: true })
        {
            UpdateStatusText.Text =
                "Updates are unavailable because Clouder is running from a plain build folder "
                + "rather than an installed copy. Install it using the Setup on the Releases "
                + "page to get automatic updates.";
            CheckUpdatesButton.IsEnabled = false;
            return;
        }

        // An earlier run may have already downloaded one.
        var pending = updates.PendingAsset;
        if (pending != null)
        {
            _readyToInstall = pending;
            UpdateStatusText.Text = $"Version {pending.Version} is downloaded and installs when you restart.";
            RestartButton.Visibility = Visibility.Visible;
            return;
        }

        UpdateStatusText.Text = App.AppConfig.AutoCheckForUpdates
            ? $"Clouder checks for updates automatically every {App.AppConfig.UpdateCheckIntervalHours} hours."
            : "Automatic update checks are turned off in Settings.";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var updates = App.Updates;
        if (updates is not { IsSupported: true }) return;

        CheckUpdatesButton.IsEnabled = false;
        RestartButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking for updates…";

        try
        {
            var info = await updates.CheckAsync();
            if (info == null)
            {
                UpdateStatusText.Text =
                    $"Clouder is up to date (version {updates.CurrentVersion ?? "unknown"}).";
                return;
            }

            var version = info.TargetFullRelease.Version.ToString();
            UpdateStatusText.Text = $"Downloading version {version}…";
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = Visibility.Visible;

            // The progress callback arrives on a background thread.
            var queue = DispatcherQueue;
            await updates.DownloadAsync(info,
                percent => queue.TryEnqueue(() => UpdateProgress.Value = percent));

            _readyToInstall = info.TargetFullRelease;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"Version {version} is ready to install.";
            RestartButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Manual update check failed", ex);
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "Could not check for updates — see the log for details.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_readyToInstall == null) return;
        App.ApplyUpdateAndRestart(_readyToInstall);
    }
}
