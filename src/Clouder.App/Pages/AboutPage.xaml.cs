using System.Reflection;
using Microsoft.UI.Xaml.Controls;

namespace Clouder_App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "0.1.0"} (Preview)";

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder");
        DataPathText.Text = $"Database: {Path.Combine(dataDir, "clouder.db")}";
        LogPathText.Text = $"Logs: {Path.Combine(dataDir, "logs")}";
    }
}
