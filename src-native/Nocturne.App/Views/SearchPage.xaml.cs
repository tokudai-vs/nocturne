using System.Windows;
using System.Windows.Controls;
using Nocturne.App.Controls;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class SearchPage : UserControl
{
    public SearchPage()
    {
        InitializeComponent();
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.DataContext is EmbyItem item && DataContext is SearchViewModel vm)
        {
            vm.OpenItemDetail(item);
        }
    }
}
