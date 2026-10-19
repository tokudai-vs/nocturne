using System.Windows;
using System.Windows.Controls;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is HomeViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            await vm.LoadAsync();
        }
    }

    private void OnPlayHeroClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm && vm.HeroItem != null)
        {
            _ = vm.PlayItemAsync(vm.HeroItem);
        }
    }

    private void OnRowItemClick(object sender, RoutedEventArgs e)
    {
        var item = (e as Controls.ItemClickEventArgs)?.Item
            ?? (e.OriginalSource as FrameworkElement)?.DataContext as EmbyItem
            ?? (e.Source as FrameworkElement)?.DataContext as EmbyItem;

        if (item != null && DataContext is HomeViewModel vm)
        {
            vm.OpenItemDetail(item);
        }
    }
}
