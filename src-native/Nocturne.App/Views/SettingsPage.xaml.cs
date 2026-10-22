using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nocturne.App.ViewModels;

namespace Nocturne.App.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;
    private Point _dragStartPoint;
    private ServerPriorityItem? _draggedItem;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (s, e) => ViewModel?.RefreshDatabaseStats();
    }

    private void OnMoveServerUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string serverId } && ViewModel != null)
        {
            ViewModel.MoveServerUp(serverId);
        }
    }

    private void OnMoveServerDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string serverId } && ViewModel != null)
        {
            ViewModel.MoveServerDown(serverId);
        }
    }

    private void OnServerItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualParent<Button>(dep);
            if (btn != null) return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is ServerPriorityItem item)
        {
            _dragStartPoint = e.GetPosition(null);
            _draggedItem = item;
        }
    }

    private void OnServerItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
            return;

        Point currentPos = e.GetPosition(null);
        Vector diff = _dragStartPoint - currentPos;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe)
            {
                DragDrop.DoDragDrop(fe, _draggedItem, DragDropEffects.Move);
                _draggedItem = null;
            }
        }
    }

    private void OnServerItemDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ServerPriorityItem)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnServerItemDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ServerPriorityItem)) &&
            sender is FrameworkElement fe &&
            fe.DataContext is ServerPriorityItem targetItem)
        {
            var sourceItem = e.Data.GetData(typeof(ServerPriorityItem)) as ServerPriorityItem;
            if (sourceItem != null && sourceItem != targetItem && ViewModel != null)
            {
                int oldIndex = ViewModel.Servers.IndexOf(sourceItem);
                int newIndex = ViewModel.Servers.IndexOf(targetItem);
                if (oldIndex >= 0 && newIndex >= 0)
                {
                    ViewModel.MoveServer(oldIndex, newIndex);
                }
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent) return typedParent;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
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

    private void OnAddDiscoveredServerClick(object sender, RoutedEventArgs e)
    {
        OnAddSingleConnectServerClick(sender, e);
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

    private async void OnConflictSplitsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.LoadConflictSplitsAsync();
        }
    }

    private async void OnDeletionSweepClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RunDeletionSweepAsync();
        }
    }

    private async void OnAutoMapCombinedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.AutoMapCombinedGroupsAsync();
        }
    }

    private void OnCreateTraktAppClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTraktAppCreationUrl();
    }

    private void OnManageTraktAppsClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTraktManageAppsUrl();
    }

    private void OnOpenTraktActivateClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTraktActivationUrl();
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

    private async void OnTraktFullReconcileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RunTraktFullReconcileAsync();
        }
    }

    private async void OnRunTmdbEnrichClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RunTmdbEnrichmentAsync();
        }
    }

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.TriggerSyncAsync();
        }
    }

    private void OnToggleSyncDetailsClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleSyncDetails();
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show("Clear all cached items and deduplication groups?", "Clear Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            ViewModel?.ClearCache();
        }
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string category } && ViewModel != null)
        {
            ViewModel.SelectCategory(category);
        }
    }

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenLogsFolder();
    }

    private void OnOpenMainLogClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenMainLog();
    }

    private void OnOpenCrashLogClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenCrashLog();
    }
}
