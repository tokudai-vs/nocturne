using System.Windows;
using System.Windows.Controls;
using Nocturne.App.ViewModels;

namespace Nocturne.App.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnSwitchServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string serverId } && ViewModel != null)
        {
            ViewModel.SwitchActiveServer(serverId);
        }
    }

    private void OnRemoveServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string serverId } && ViewModel != null)
        {
            var res = MessageBox.Show("Are you sure you want to remove this server? Cached library items from this server will be deleted.",
                                      "Remove Server", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                ViewModel.RemoveServer(serverId);
            }
        }
    }

    private async void OnAddServerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.NewPassword = NewServerPasswordInput.Password;
            await ViewModel.AddServerAsync();
            NewServerPasswordInput.Password = string.Empty;
        }
    }

    private async void OnStartEmbyConnectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.StartEmbyConnectFlowAsync();
        }
    }

    private void OnOpenPinBrowserClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenPinBrowser();
    }

    private void OnCancelEmbyConnectClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.CancelEmbyConnectFlow();
    }

    private async void OnAddAllConnectServersClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.AddAllConnectServersAsync();
        }
    }

    private async void OnAddSingleConnectServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiscoveredConnectServerUi item } && ViewModel != null)
        {
            await ViewModel.AddSingleConnectServerAsync(item);
        }
    }

    private void OnDoneConnectServersClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.CloseConnectFlow();
    }

    private void OnAddCombinedGroupClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddCombinedGroup();
    }

    private void OnRemoveCombinedGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string groupName } && ViewModel != null)
        {
            ViewModel.RemoveCombinedGroup(groupName);
        }
    }

    private async void OnRebuildDedupClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RebuildDedupIndexAsync();
        }
    }

    private async void OnConnectTraktClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.StartTraktAuthAsync();
        }
    }

    private void OnDisconnectTraktClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DisconnectTrakt();
    }

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.TriggerSyncAsync();
        }
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show("Clear all cached items and deduplication groups?", "Clear Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            ViewModel?.ClearCache();
        }
    }
}
