using Microsoft.UI.Xaml;
using Clouder.CloudFilter;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Email;
using Clouder.Storage;

namespace Clouder_App;

public partial class App : Application
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder");

    public static SqliteMetadataStore Store { get; private set; } = null!;
    public static ProviderRegistry Providers { get; private set; } = null!;
    public static ConfigService Config { get; private set; } = null!;
    public static TrayIcon Tray { get; private set; } = null!;
    public static EmailCheckService? EmailChecker { get; private set; }
    public static PoolSyncService? SyncService { get; private set; }
    public static RemoteSyncService? RemoteSync { get; private set; }
    public static ProviderConnectionManager Connection { get; private set; } = null!;
    public static ClouderConfig AppConfig { get; private set; } = new();

    private static readonly List<SyncEngine> Engines = [];
    private static System.Threading.Timer? _healthTimer;
    private static System.Threading.Timer? _periodicSyncTimer;
    private MainWindow? _window;
    private static MainWindow? _windowRef;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            ClouderLog.Info("Clouder starting (single process)");
            ClouderLog.CleanOldLogs();
            Directory.CreateDirectory(DataDir);

            // Initialize data layer
            var dbPath = Path.Combine(DataDir, "clouder.db");
            Store = new SqliteMetadataStore(dbPath);
            await Store.InitializeAsync();
            Providers = new ProviderRegistry();
            Config = new ConfigService(Store);
            AppConfig = await Config.LoadAsync();

            ClouderLog.Info("Database initialized");

            // Rehydrate providers from saved credentials/tokens so accounts
            // reconnect automatically after a restart (registers providers, no network).
            Connection = new ProviderConnectionManager(Store, Providers);
            await Connection.ReconnectAllAsync();

            // CfApi sync engines disabled (crashes Explorer on this system).
            // Instead: PoolSyncService (FileSystemWatcher) for local→cloud, and
            // RemoteSyncService (change polling) for cloud→local. They share the
            // conflict handler and remote-root resolver so both directions agree.
            var conflicts = new ConflictHandler(Store);
            var roots = new RemoteRootResolver(Store);
            SyncService = new PoolSyncService(Store, Providers, conflicts, roots);
            RemoteSync = new RemoteSyncService(Store, Providers, conflicts, roots, SyncService);
            ApplyConfigToSync();

            // Apply auto-start preference to the Windows registry.
            ApplyAutoStart(AppConfig.AutoStartOnLogin);

            // Start tray icon on background STA thread
            Tray = new TrayIcon();
            Tray.ShowWindowRequested += () => _window?.DispatcherQueue.TryEnqueue(ShowWindow);
            Tray.ExitRequested += () => _window?.DispatcherQueue.TryEnqueue(ExitApp);
            Tray.SyncRequested += () =>
            {
                ClouderLog.Info("Manual sync triggered from tray");
                _ = SyncAllPoolsAsync();
            };
            Tray.PauseChanged += paused =>
            {
                if (SyncService != null) SyncService.Paused = paused;
                if (RemoteSync != null) RemoteSync.Paused = paused;
                ClouderLog.Info(paused ? "Sync paused" : "Sync resumed");
                if (!paused) _ = SyncAllPoolsAsync();
            };
            Tray.Start();
            Tray.UpdateStatus(Engines.Count, false);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to initialize", ex);
        }

        _window = new MainWindow();
        _windowRef = _window;
        _window.Activate();

        // Start email monitoring (after window so badge updates work)
        StartEmailMonitoring(_window);

        // Verify provider connections in the background (refreshes tokens/quota).
        // Then start file sync watchers once connections are confirmed.
        _ = VerifyAndStartSyncAsync();
    }

    // ── Config application ──────────────────────────────────────────────

    /// <summary>Push the current ClouderConfig values into the running sync service.</summary>
    public static void ApplyConfigToSync()
    {
        if (SyncService == null) return;
        SyncService.ConflictPolicy = AppConfig.ConflictPolicy;
        SyncService.MaxConcurrentTransfers = Math.Max(1, AppConfig.MaxConcurrentTransfers);
        SyncService.StripeThresholdBytes = AppConfig.StripingPromptThresholdMb > 0
            ? AppConfig.StripingPromptThresholdMb * 1024L * 1024L
            : 0;
        SyncService.MaxUploadBytesPerSec = AppConfig.MaxUploadBytesPerSec;
        SyncService.MaxDownloadBytesPerSec = AppConfig.MaxDownloadBytesPerSec;
        SyncService.AutoReorganizeOnFull = AppConfig.AutoReorganizeOnFull;
        SyncService.MinFreeDiskBytes = AppConfig.MinFreeDiskMb > 0
            ? AppConfig.MinFreeDiskMb * 1024L * 1024L
            : 0;

        if (RemoteSync == null) return;
        RemoteSync.ConflictPolicy = AppConfig.ConflictPolicy;
        RemoteSync.MaxDownloadBytesPerSec = AppConfig.MaxDownloadBytesPerSec;
        RemoteSync.MinFreeDiskBytes = SyncService.MinFreeDiskBytes;
    }

    /// <summary>Re-read config from the store and apply everything live. Called by Settings page.</summary>
    public static async Task ReloadConfigAsync()
    {
        AppConfig = await Config.LoadAsync();
        ApplyConfigToSync();
        ApplyAutoStart(AppConfig.AutoStartOnLogin);
        RestartPeriodicSync();
    }

    private static void ApplyAutoStart(bool enabled)
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null) return;

            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (enabled && exePath != null)
                key.SetValue("Clouder", $"\"{exePath}\"");
            else
                key.DeleteValue("Clouder", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to set auto-start", ex);
        }
    }

    /// <summary>Create or store an app notification, honoring the ShowNotifications setting.</summary>
    public static async Task NotifyAsync(AppNotification notification)
    {
        if (!AppConfig.ShowNotifications) return;
        await Store.UpsertNotificationAsync(notification);
    }

    /// <summary>
    /// One full sync cycle: pull remote changes down first, then push local changes up.
    /// Pulling first means a file changed in both places is detected as a conflict
    /// before the uploader would blindly overwrite the cloud copy.
    /// </summary>
    private static async Task SyncAllPoolsAsync()
    {
        if (SyncService == null) return;

        if (RemoteSync is { Paused: false })
        {
            try { await RemoteSync.SyncAllPoolsAsync(); }
            catch (Exception ex) { ClouderLog.Error("Remote (cloud→local) sync failed", ex); }
        }

        try
        {
            var pools = await Store.GetAllPoolsAsync();
            foreach (var pool in pools)
            {
                try { await SyncService.SyncPoolAsync(pool.PoolId); }
                catch (Exception ex) { ClouderLog.Error($"Sync failed for pool '{pool.Name}'", ex); }
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Sync-all failed", ex);
        }
    }

    private static void RestartPeriodicSync()
    {
        _periodicSyncTimer?.Dispose();
        var interval = TimeSpan.FromSeconds(Math.Max(30, AppConfig.SyncIntervalSeconds));
        _periodicSyncTimer = new System.Threading.Timer(
            _ => { if (SyncService is { Paused: false }) _ = SyncAllPoolsAsync(); },
            null, interval, interval);
    }

    // ── Pool lifecycle (called from UI) ────────────────────────────────

    public static async Task ConnectPoolAsync(StoragePool pool)
    {
        try
        {
            if (!SyncRootRegistrar.IsRegistered(pool.PoolId))
                await SyncRootRegistrar.RegisterAsync(pool.PoolId, pool.Name, pool.LocalPath);

            var engine = new SyncEngine(pool.LocalPath, pool.PoolId, Store, Providers);
            engine.Connect();
            Engines.Add(engine);
            Tray?.UpdateStatus(Engines.Count, false);
            ClouderLog.Info($"Pool connected from UI: {pool.Name}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to connect pool '{pool.Name}'", ex);
            throw;
        }
    }

    public static void DisconnectPool(string poolId)
    {
        var engine = Engines.FirstOrDefault(e => e.PoolId == poolId);
        if (engine != null)
        {
            engine.Dispose();
            Engines.Remove(engine);
        }
        SyncRootRegistrar.Unregister(poolId);
        Tray?.UpdateStatus(Engines.Count, false);
        ClouderLog.Info($"Pool disconnected: {poolId}");
    }

    public static bool IsPoolConnected(string poolId) =>
        Engines.Any(e => e.PoolId == poolId);

    private static async Task VerifyAndStartSyncAsync()
    {
        // Verify accounts (silent token refresh where possible).
        try
        {
            await Connection.VerifyAllAsync();
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Account verification failed", ex);
        }

        // Start the file-sync watchers and do an initial sweep of each pool.
        try
        {
            if (SyncService != null)
            {
                await SyncService.StartAsync();
                ClouderLog.Info("Pool sync service started");

                // Initial sweep so files added while the app was closed get uploaded.
                await SyncAllPoolsAsync();

                // Periodic re-sync on the configured interval.
                RestartPeriodicSync();
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to start pool sync service", ex);
        }

        // Start periodic health checks (quota warnings, dead-account alerts).
        StartHealthChecks();
    }

    private static void StartHealthChecks()
    {
        var health = new HealthCheckService(Store, Providers);
        async void RunCheck(object? _)
        {
            try
            {
                var alerts = await health.RunChecksAsync();
                int created = 0;
                foreach (var alert in alerts)
                {
                    if (!AppConfig.ShowNotifications) break;
                    await Store.UpsertNotificationAsync(alert);
                    created++;
                }
                if (created > 0)
                {
                    var unread = await Store.GetUnreadCountAsync();
                    _windowRef?.DispatcherQueue.TryEnqueue(() => _windowRef!.UpdateBadge(unread));
                }
            }
            catch (Exception ex)
            {
                ClouderLog.Error("Health check run failed", ex);
            }
        }
        // First run after 1 minute, then hourly.
        _healthTimer = new System.Threading.Timer(RunCheck, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
    }

    private static void StartEmailMonitoring(MainWindow window)
    {
        try
        {
            var imapMonitor = new ImapEmailMonitor();

            // Gmail API monitor is created on demand from the saved Google OAuth
            // credentials. Returns null if Google was never connected.
            GmailEmailMonitor? GmailFactory()
            {
                try
                {
                    var creds = Connection.LoadGoogleCredentialsAsync().GetAwaiter().GetResult();
                    if (creds == null) return null;
                    var tokenPath = new Clouder.Providers.GoogleDrive.GoogleDriveSettings
                    {
                        ClientId = creds.Value.ClientId,
                        ClientSecret = creds.Value.ClientSecret
                    }.TokenStoragePath;
                    return new GmailEmailMonitor(creds.Value.ClientId, creds.Value.ClientSecret, tokenPath);
                }
                catch (Exception ex)
                {
                    ClouderLog.Error("Failed to create Gmail monitor", ex);
                    return null;
                }
            }

            EmailChecker = new EmailCheckService(Store, imapMonitor, GmailFactory);
            EmailChecker.UnreadCountChanged += count =>
            {
                window.DispatcherQueue.TryEnqueue(() => window.UpdateBadge(count));
            };
            EmailChecker.Start(TimeSpan.FromMinutes(30));
            ClouderLog.Info("Email monitoring initialized");
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to start email monitoring", ex);
        }
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.AppWindow.Show();
        _window.Activate();
    }

    private void ExitApp()
    {
        ClouderLog.Info("Exit requested from tray");
        _healthTimer?.Dispose();
        _periodicSyncTimer?.Dispose();
        SyncService?.Dispose();
        EmailChecker?.Dispose();
        foreach (var engine in Engines)
            engine.Dispose();
        Tray.Dispose();
        _window?.Close();
        Environment.Exit(0);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        ClouderLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ClouderLog.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
