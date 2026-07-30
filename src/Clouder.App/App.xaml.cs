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
    public static CfPlaceholderSink? PlaceholderSink { get; private set; }
    public static HydrationService? Hydration { get; private set; }
    public static ToastNotifier? Toasts { get; private set; }
    public static CacheEvictionService? CacheEviction { get; private set; }
    public static ProviderConnectionManager Connection { get; private set; } = null!;
    public static ClouderConfig AppConfig { get; private set; } = new();
    public static UpdateService? Updates { get; private set; }

    private static readonly List<SyncEngine> Engines = [];
    private static System.Threading.Timer? _healthTimer;
    private static System.Threading.Timer? _periodicSyncTimer;
    private static System.Threading.Timer? _updateTimer;
    private MainWindow? _window;
    private static MainWindow? _windowRef;

    // ── Single instance ─────────────────────────────────────────────────
    // Two copies would run two sets of file watchers and tray icons against the
    // same database and pool folders, racing each other.
    private const string SingleInstanceMutexName = @"Local\Clouder.SingleInstance";
    private const string ShowWindowEventName = @"Local\Clouder.ShowWindow";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // If Clouder is already running, wake that instance's window and quit.
        if (!TryClaimSingleInstance())
        {
            ClouderLog.Info("Another instance is already running — bringing it to the front");
            Environment.Exit(0);
            return;
        }

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

            // Explorer integration is opt-in (see ClouderConfig.ExplorerIntegrationEnabled).
            PlaceholderSink = new CfPlaceholderSink();
            SyncService.Placeholders = PlaceholderSink;
            RemoteSync.Placeholders = PlaceholderSink;
            Hydration = new HydrationService(Store, Providers);
            CacheEviction = new CacheEvictionService(Store, PlaceholderSink);

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

            Toasts = new ToastNotifier(Tray) { Enabled = AppConfig.ShowNotifications };

            // Conflicts are the one thing that always needs a person, so surface them
            // the moment they're detected rather than at the next health check.
            conflicts.ConflictDetected += (poolId, relativePath) =>
            {
                Toasts?.Show(new AppNotification
                {
                    NotificationId = $"toast-conflict-{poolId}-{relativePath}",
                    Title = "Sync conflict",
                    Body = $"\"{Path.GetFileName(relativePath)}\" changed here and in the cloud. "
                         + "Open Files to choose which copy to keep.",
                    Source = "sync",
                    Severity = NotificationSeverity.Warning,
                    TimestampUtc = DateTime.UtcNow,
                    IsRead = false
                });
            };
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

        // Updating the app has nothing to do with the cloud accounts, so this must not
        // sit behind verification — an unreachable account can leave that awaiting a
        // network timeout for minutes, and updates would silently never be checked.
        StartUpdateChecks();
    }

    /// <summary>
    /// Claims the single-instance mutex. Returns false if another Clouder is already
    /// running, having signalled it to show its window first.
    /// </summary>
    private static bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);

            if (!isFirstInstance)
            {
                // Nudge the running instance, then let this one exit.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
                    {
                        using (existing) existing.Set();
                    }
                }
                catch (Exception ex)
                {
                    ClouderLog.Debug($"Could not signal the running instance: {ex.Message}");
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }

            StartShowWindowListener();
            return true;
        }
        catch (Exception ex)
        {
            // Never let the guard itself stop the app from starting.
            ClouderLog.Error("Single-instance check failed; starting anyway", ex);
            return true;
        }
    }

    /// <summary>Waits for a second launch to signal us, and restores the window when it does.</summary>
    private static void StartShowWindowListener()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (_showWindowEvent == null) return;
                    _showWindowEvent.WaitOne();

                    var window = _windowRef;
                    window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            window.AppWindow.Show();
                            window.Activate();
                        }
                        catch (Exception ex)
                        {
                            ClouderLog.Debug($"Could not restore the window: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    ClouderLog.Debug($"Show-window listener stopped: {ex.Message}");
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClouderShowWindowListener"
        };
        listener.Start();
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

        if (Toasts != null) Toasts.Enabled = AppConfig.ShowNotifications;

        if (CacheEviction != null)
        {
            CacheEviction.CacheLimitBytes = AppConfig.CacheSizeLimitMb > 0
                ? AppConfig.CacheSizeLimitMb * 1024L * 1024L
                : 0;
            CacheEviction.DehydrateAfterDays = AppConfig.AutoDehydrateDays;
        }

        if (RemoteSync == null) return;
        RemoteSync.ConflictPolicy = AppConfig.ConflictPolicy;
        RemoteSync.MaxDownloadBytesPerSec = AppConfig.MaxDownloadBytesPerSec;
        RemoteSync.MinFreeDiskBytes = SyncService.MinFreeDiskBytes;
    }

    /// <summary>Re-read config from the store and apply everything live. Called by Settings page.</summary>
    public static async Task ReloadConfigAsync()
    {
        bool explorerWasOn = Engines.Count > 0;

        AppConfig = await Config.LoadAsync();
        ApplyConfigToSync();
        ApplyAutoStart(AppConfig.AutoStartOnLogin);
        RestartPeriodicSync();
        RestartUpdateChecks();

        if (AppConfig.ExplorerIntegrationEnabled && !explorerWasOn)
            await EnableExplorerIntegrationAsync();
        else if (!AppConfig.ExplorerIntegrationEnabled && explorerWasOn)
            DisableExplorerIntegration();
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

    /// <summary>
    /// Records an app notification and, for anything needing attention, raises a
    /// Windows notification too.
    ///
    /// The notification is always stored; ShowNotifications only governs whether it
    /// interrupts. Suppressing the record as well (the old behaviour) meant turning
    /// notifications off silently discarded the history of what went wrong.
    /// </summary>
    public static async Task NotifyAsync(AppNotification notification)
    {
        await Store.UpsertNotificationAsync(notification);

        if (AppConfig.ShowNotifications)
            Toasts?.Show(notification);

        try
        {
            var unread = await Store.GetUnreadCountAsync();
            _windowRef?.DispatcherQueue.TryEnqueue(() => _windowRef!.UpdateBadge(unread));
        }
        catch { /* badge is cosmetic */ }
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

    // ── Explorer integration (CfApi) — opt-in ──────────────────────────

    /// <summary>
    /// Registers every pool as a Windows sync root and connects its cloud-filter engine,
    /// so pool folders appear in Explorer's sidebar with on-demand files.
    /// Any pool that fails is skipped rather than taking the app down with it.
    /// </summary>
    public static async Task EnableExplorerIntegrationAsync()
    {
        if (Hydration == null || PlaceholderSink == null) return;

        if (!SyncRootRegistrar.IsSupported())
        {
            ClouderLog.Warn("Explorer integration is not supported on this Windows build");
            return;
        }

        var pools = await Store.GetAllPoolsAsync();
        foreach (var pool in pools)
        {
            if (Engines.Any(e => e.PoolId == pool.PoolId)) continue;

            try
            {
                await ConnectPoolAsync(pool);
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Explorer integration failed for pool '{pool.Name}'", ex);
            }
        }

        Tray?.UpdateStatus(Engines.Count, false);
    }

    public static void DisableExplorerIntegration()
    {
        foreach (var engine in Engines.ToList())
        {
            PlaceholderSink?.Deactivate(engine.PoolId);
            try { engine.Dispose(); }
            catch (Exception ex) { ClouderLog.Error($"Error disconnecting pool {engine.PoolId}", ex); }
        }
        Engines.Clear();
        Tray?.UpdateStatus(0, false);
        ClouderLog.Info("Explorer integration disabled");
    }

    public static async Task ConnectPoolAsync(StoragePool pool)
    {
        if (Hydration == null || PlaceholderSink == null)
            throw new InvalidOperationException("Sync services are not initialized.");

        try
        {
            if (!SyncRootRegistrar.IsRegistered(pool.PoolId))
                await SyncRootRegistrar.RegisterAsync(pool.PoolId, pool.Name, pool.LocalPath);

            var engine = new SyncEngine(pool.LocalPath, pool.PoolId, Hydration);
            engine.Connect();
            Engines.Add(engine);
            PlaceholderSink.Activate(pool.PoolId);
            Tray?.UpdateStatus(Engines.Count, false);
            ClouderLog.Info($"Explorer integration active for pool: {pool.Name}");

            // Files synced before the integration was switched on are ordinary files as
            // far as Explorer is concerned, and would sit at "Sync pending" forever.
            if (SyncService != null)
                await SyncService.ReconcilePlaceholdersAsync(pool.PoolId);
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to connect pool '{pool.Name}'", ex);
            throw;
        }
    }

    public static void DisconnectPool(string poolId, string? localPath = null)
    {
        var engine = Engines.FirstOrDefault(e => e.PoolId == poolId);
        if (engine != null)
        {
            engine.Dispose();
            Engines.Remove(engine);
        }
        PlaceholderSink?.Deactivate(poolId);
        SyncRootRegistrar.Unregister(poolId, localPath);
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

        // Explorer integration last: it's opt-in and must never block sync from starting.
        if (AppConfig.ExplorerIntegrationEnabled)
        {
            try { await EnableExplorerIntegrationAsync(); }
            catch (Exception ex) { ClouderLog.Error("Failed to enable Explorer integration", ex); }
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
                foreach (var alert in alerts)
                    await NotifyAsync(alert);   // stores, toasts if enabled, updates the badge

                // Housekeeping on the same hourly tick: free local disk for files that
                // are safely in the cloud, and keep transfer history from growing forever.
                if (CacheEviction != null)
                    await CacheEviction.RunAsync();
                await Store.PruneTransfersAsync();
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

    // ── Updates ─────────────────────────────────────────────────────────

    private static bool _updatePromptOpen;

    private static void StartUpdateChecks()
    {
        Updates = new UpdateService();

        if (!Updates.IsSupported)
        {
            ClouderLog.Info("Update checks are off — Clouder is not running from an installed build");
            return;
        }

        ClouderLog.Info($"Updater ready (current version {Updates.CurrentVersion ?? "unknown"})");
        RestartUpdateChecks();
    }

    /// <summary>Re-arms the update timer from the current config. Called by the Settings page.</summary>
    public static void RestartUpdateChecks()
    {
        _updateTimer?.Dispose();
        _updateTimer = null;

        if (Updates is not { IsSupported: true } || !AppConfig.AutoCheckForUpdates) return;

        var interval = TimeSpan.FromHours(Math.Max(1, AppConfig.UpdateCheckIntervalHours));
        // First check two minutes in, so it isn't competing with the initial sync sweep.
        _updateTimer = new System.Threading.Timer(
            _ => _ = RunAutomaticUpdateCheckAsync(), null, TimeSpan.FromMinutes(2), interval);
    }

    private static async Task RunAutomaticUpdateCheckAsync()
    {
        if (Updates is not { IsSupported: true }) return;

        try
        {
            // Already downloaded and waiting for a restart — don't ask again on every tick.
            if (Updates.IsRestartPending) return;

            var info = await Updates.CheckAsync();
            if (info == null) return;

            var version = info.TargetFullRelease.Version.ToString();
            ClouderLog.Info($"Update {version} available — downloading");
            await Updates.DownloadAsync(info);
            ClouderLog.Info($"Update {version} downloaded, pending restart");

            OfferRestart(info, version);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Automatic update check failed", ex);
        }
    }

    /// <summary>
    /// Asks whether to restart into the new version. A ContentDialog needs a visible
    /// XamlRoot and Clouder spends most of its life minimized to the tray, so when the
    /// window is hidden this falls back to a notification the user can act on later.
    /// </summary>
    public static void OfferRestart(Velopack.UpdateInfo info, string version)
    {
        var window = _windowRef;
        bool canPrompt = window != null && !_updatePromptOpen;
        try
        {
            canPrompt = canPrompt && window!.AppWindow.IsVisible;
        }
        catch
        {
            canPrompt = false;
        }

        if (!canPrompt)
        {
            _ = NotifyAsync(new AppNotification
            {
                NotificationId = $"update-ready-{version}",
                Title = $"Clouder {version} is ready",
                Body = "The update is downloaded. Restart Clouder to finish installing it.",
                Source = "Updater",
                Severity = NotificationSeverity.Info,
                TimestampUtc = DateTime.UtcNow
            });
            return;
        }

        window!.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_updatePromptOpen) return;
            _updatePromptOpen = true;
            try
            {
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    XamlRoot = window.Content.XamlRoot,
                    Title = $"Clouder {version} is ready",
                    Content = "The update has been downloaded. Restarting takes a few seconds, "
                            + "and any sync in progress resumes automatically afterwards.",
                    PrimaryButtonText = "Restart now",
                    CloseButtonText = "Later",
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary
                };

                if (await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    ApplyUpdateAndRestart(info.TargetFullRelease);
            }
            catch (Exception ex)
            {
                ClouderLog.Error("Update restart prompt failed", ex);
            }
            finally
            {
                _updatePromptOpen = false;
            }
        });
    }

    /// <summary>
    /// Releases everything holding a file handle or the single-instance claim, then hands
    /// off to Velopack, which swaps the install folder and relaunches.
    ///
    /// Releasing the mutex is not optional: the relaunched copy runs the same
    /// single-instance check, and would see this process's claim and quit immediately.
    /// </summary>
    public static void ApplyUpdateAndRestart(Velopack.VelopackAsset asset)
    {
        if (Updates is not { IsSupported: true }) return;

        ClouderLog.Info("Shutting down services for update");

        _healthTimer?.Dispose();
        _periodicSyncTimer?.Dispose();
        _updateTimer?.Dispose();
        SyncService?.Dispose();
        EmailChecker?.Dispose();
        foreach (var engine in Engines)
            engine.Dispose();

        try { Tray?.Dispose(); } catch { /* tray already gone */ }

        try
        {
            _showWindowEvent?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { /* already released */ }

        Updates.ApplyAndRestart(asset);
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
        _updateTimer?.Dispose();
        SyncService?.Dispose();
        EmailChecker?.Dispose();
        foreach (var engine in Engines)
            engine.Dispose();
        Tray.Dispose();

        // Release the single-instance claim so the next launch starts cleanly.
        try
        {
            _showWindowEvent?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { /* already gone */ }

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
