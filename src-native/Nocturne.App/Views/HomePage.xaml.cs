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
    }

    private void OnPlayHeroClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm && vm.HeroItem != null)
        {
            _ = vm.PlayItemAsync(vm.HeroItem);
        }
    }

    private void OnRowItemClick(object sender, EmbyItem item)
    {
        if (DataContext is HomeViewModel vm)
        {
            vm.OpenItemDetail(item);
        }
    }
}
