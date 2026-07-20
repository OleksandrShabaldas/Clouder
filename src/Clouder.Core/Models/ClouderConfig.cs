namespace Clouder.Core.Models;

public enum ConflictResolution
{
    NewestWins,
    KeepBoth,
    AlwaysAsk
}

public sealed class ClouderConfig
{
    // General
    public bool AutoStartOnLogin { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public PlacementStrategy DefaultStrategy { get; set; } = PlacementStrategy.FillFirst;

    // Sync
    public int SyncIntervalSeconds { get; set; } = 300;
    public ConflictResolution ConflictPolicy { get; set; } = ConflictResolution.NewestWins;
    public long MaxUploadBytesPerSec { get; set; }
    public long MaxDownloadBytesPerSec { get; set; }
    public int MaxConcurrentTransfers { get; set; } = 4;

    // Storage
    public long CacheSizeLimitMb { get; set; }
    public int AutoDehydrateDays { get; set; }
    public long MinFreeDiskMb { get; set; } = 1024;

    // File striping
    public long StripingPromptThresholdMb { get; set; } = 100;
    public bool AutoReorganizeOnFull { get; set; } = true;

    /// <summary>
    /// Show pools in Explorer's sidebar with on-demand (placeholder) files, via the
    /// Windows Cloud Files API. Off by default: a faulty cloud-filter provider can hang
    /// or crash Explorer, so this stays opt-in until verified on the user's machine.
    /// </summary>
    public bool ExplorerIntegrationEnabled { get; set; }
}
