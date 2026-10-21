using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.App.ViewModels;
using Nocturne.Core.Services;

namespace Nocturne.App.Views;

public partial class WatchlistPage : UserControl
{
    public WatchlistPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WatchlistViewModel vm)
        {
            await vm.RefreshAsync();
        }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is WatchlistCardItem item)
        {
            if (item.IsInLibrary && item.Card.GroupId > 0)
            {
                var mainVm = (Application.Current as App)?.Services.GetService<MainViewModel>();
                if (mainVm != null)
                {
                    mainVm.OpenGroupDetail(item.Card.GroupId);
                }
            }
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement elem && elem.DataContext is WatchlistCardItem item)
        {
            if (DataContext is WatchlistViewModel vm)
            {
                await vm.RemoveFromWatchlistAsync(item.Card);
            }
        }
    }
}
