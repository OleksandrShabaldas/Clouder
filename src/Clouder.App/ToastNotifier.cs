using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder_App;

/// <summary>
/// Surfaces important events as Windows notifications.
///
/// Clouder spends most of its life minimised to the tray, so anything needing
/// attention — a sync failure, a conflict, an account that stopped working — would
/// otherwise sit unseen in the Notifications page. Only Warning and Critical events
/// raise a toast; routine successes stay in the in-app list.
///
/// Uses the tray icon's balloon rather than the WinRT toast APIs, which require
/// package identity that this unpackaged build doesn't have.
/// </summary>
public sealed class ToastNotifier
{
    private readonly TrayIcon _tray;
    private DateTime _lastToastUtc = DateTime.MinValue;
    private string? _lastKey;

    /// <summary>Don't fire the same notification repeatedly, e.g. per file in a failing sweep.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public ToastNotifier(TrayIcon tray) => _tray = tray;

    /// <summary>Master switch, mirroring the "Show sync notifications" setting.</summary>
    public bool Enabled { get; set; } = true;

    public void Show(AppNotification notification)
    {
        if (!Enabled) return;
        if (notification.Severity == NotificationSeverity.Info) return;

        // Collapse repeats of the same underlying problem.
        var key = $"{notification.Source}|{notification.Title}";
        if (key == _lastKey && DateTime.UtcNow - _lastToastUtc < Cooldown) return;

        _lastKey = key;
        _lastToastUtc = DateTime.UtcNow;

        try
        {
            _tray.ShowBalloon(
                notification.Title,
                Summarize(notification.Body),
                notification.Severity == NotificationSeverity.Critical);
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not show a notification: {ex.Message}");
        }
    }

    /// <summary>Balloon text is short-lived and narrow — show the first meaningful line.</summary>
    private static string Summarize(string body)
    {
        var firstLine = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? body;

        return firstLine.Length <= 180 ? firstLine : firstLine[..177] + "...";
    }
}
