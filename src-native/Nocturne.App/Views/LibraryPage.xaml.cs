using System.Windows;
using System.Windows.Controls;
using Nocturne.App.Controls;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is LibraryViewModel vm && vm.Views.Count == 0)
            {
                await vm.LoadViewsAsync();
            }
        };
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.DataContext is EmbyItem item && DataContext is LibraryViewModel vm)
        {
            vm.OpenItemDetail(item);
        }
    }
}
