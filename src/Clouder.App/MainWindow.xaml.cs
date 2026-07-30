using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Clouder.Core.Logging;
using Clouder_App.Pages;

namespace Clouder_App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(960, 680));
        AppWindow.Title = "Clouder";

        // Minimize to tray instead of closing
        AppWindow.Closing += OnClosing;

        NavFrame.Navigate(typeof(DashboardPage));
        _ = RefreshBadgeAsync();
    }

    public async Task RefreshBadgeAsync()
    {
        try
        {
            var count = await App.Store.GetUnreadCountAsync();
            DispatcherQueue.TryEnqueue(() => UpdateBadge(count));
        }
        catch { }
    }

    public void UpdateBadge(int unreadCount)
    {
        NotifBadge.Value = unreadCount;
        NotifBadge.Visibility = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Respect the "Minimize to tray on close" setting.
        if (App.AppConfig.MinimizeToTray)
        {
            args.Cancel = true;
            sender.Hide();
            ClouderLog.Debug("Window hidden to tray");
        }
        // Otherwise let the window close; the app keeps running in the tray
        // until the user chooses Exit from the tray menu.
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            NavFrame.Navigate(item.Tag switch
            {
                "dashboard" => typeof(DashboardPage),
                "accounts" => typeof(AccountsPage),
                "pools" => typeof(PoolsPage),
                "files" => typeof(FilesPage),
                "notifications" => typeof(NotificationsPage),
                "about" => typeof(AboutPage),
                _ => typeof(DashboardPage)
            });

            if (item.Tag is "notifications")
                _ = RefreshBadgeAsync();
        }
    }
}
