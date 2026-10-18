using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class DetailPage : UserControl
{
    public DetailPage()
    {
        InitializeComponent();
        Focusable = true;
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && DataContext is DetailViewModel { ActiveSkipPrompt: not null } vm)
            {
                _ = vm.ExecuteSkipPromptAsync();
                e.Handled = true;
            }
        };
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            vm.GoBack();
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.PlayAsync();
        }
    }

    private void OnWatchTrailerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            vm.PlayTrailer();
        }
    }

    private void OnPlayEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is EmbyItem ep && DataContext is DetailViewModel vm)
        {
            _ = vm.PlayAsync(ep);
        }
    }

    private void OnPlayedToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.TogglePlayedAsync();
        }
    }

    private void OnFavoriteToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.ToggleFavoriteAsync();
        }
    }

    private void OnWatchlistToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.ToggleWatchlistAsync();
        }
    }

    private void OnExecuteSkipClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.ExecuteSkipPromptAsync();
        }
    }
}
