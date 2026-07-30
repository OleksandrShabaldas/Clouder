using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Clouder.Core.Logging;
using Velopack;

namespace Clouder_App;

/// <summary>
/// Replaces the XAML-generated entry point (see DISABLE_XAML_GENERATED_MAIN in the csproj).
///
/// Velopack has to run before anything else: when Windows launches Clouder as part of an
/// install, update or uninstall it passes hook arguments, and Velopack handles those and
/// exits the process. Starting WinUI first would flash a window — or worse, begin syncing —
/// during what is supposed to be a silent maintenance run.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build()
                .SetArgs(args)
                .Run();
        }
        catch (Exception ex)
        {
            // A broken update hook must never stop the app from starting — the worst
            // acceptable outcome is that this launch simply isn't updated.
            ClouderLog.Error("Velopack startup hook failed", ex);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
