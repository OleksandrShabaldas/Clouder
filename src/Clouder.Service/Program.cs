using System.Windows.Forms;
using Clouder.CloudFilter;
using Clouder.Core.Logging;
using Clouder.Core.Providers;
using Clouder.Storage;

namespace Clouder.Service;

internal class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ClouderLog.Info("Clouder Service starting");
        ClouderLog.CleanOldLogs();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ClouderLog.Error("Fatal unhandled exception in service", e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.ThreadException += (_, e) =>
            ClouderLog.Error("WinForms thread exception", e.Exception);

        using var appContext = new ClouderServiceContext();
        Application.Run(appContext);

        ClouderLog.Info("Clouder Service stopped");
    }
}

internal sealed class ClouderServiceContext : ApplicationContext
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder");

    private readonly TrayIconManager _tray;
    private readonly List<SyncEngine> _engines = [];
    private SqliteMetadataStore? _store;

    public ClouderServiceContext()
    {
        _tray = new TrayIconManager();
        _tray.ExitRequested += () => ExitThread();
        _tray.SyncRequested += OnSyncRequested;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            var dbPath = Path.Combine(DataDir, "clouder.db");
            _store = new SqliteMetadataStore(dbPath);
            await _store.InitializeAsync();

            var registry = new ProviderRegistry();
            var pools = await _store.GetAllPoolsAsync();

            foreach (var pool in pools)
            {
                try
                {
                    if (!SyncRootRegistrar.IsRegistered(pool.PoolId))
                        await SyncRootRegistrar.RegisterAsync(pool.PoolId, pool.Name, pool.LocalPath);

                    var engine = new SyncEngine(pool.LocalPath, pool.PoolId, _store, registry);
                    engine.Connect();
                    _engines.Add(engine);
                    ClouderLog.Info($"Pool connected: {pool.Name}");
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"Failed to set up pool '{pool.Name}'", ex);
                }
            }

            _tray.UpdateStatus(_engines.Count, false);
            ClouderLog.Info($"Service ready: {_engines.Count} pool(s)");
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Service initialization failed", ex);
            MessageBox.Show($"Failed to start Clouder service:\n{ex.Message}",
                "Clouder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSyncRequested()
    {
        ClouderLog.Info("Manual sync triggered");
        _tray.UpdateStatus(_engines.Count, true);
        _tray.UpdateStatus(_engines.Count, false);
    }

    protected override void ExitThreadCore()
    {
        ClouderLog.Info("Service shutting down");
        foreach (var engine in _engines)
            engine.Dispose();
        _store?.DisposeAsync().AsTask().Wait();
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
