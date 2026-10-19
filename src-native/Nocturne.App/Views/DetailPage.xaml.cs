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

    private void OnToggleEpisodePlayedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is EmbyItem ep && DataContext is DetailViewModel vm)
        {
            _ = vm.ToggleEpisodePlayedAsync(ep);
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

    private void OnSimilarItemClick(object sender, RoutedEventArgs e)
    {
        var item = (e as Controls.ItemClickEventArgs)?.Item
            ?? (e.OriginalSource as FrameworkElement)?.DataContext as EmbyItem
            ?? (e.Source as FrameworkElement)?.DataContext as EmbyItem;

        if (item != null && DataContext is DetailViewModel vm)
        {
            _ = vm.LoadItemAsync(item.Id);
        }
    }

    private void OnCastRowMouseEnter(object sender, MouseEventArgs e)
    {
        CastScrollLeftButton.Opacity = 1;
        CastScrollRightButton.Opacity = 1;
    }

    private void OnCastRowMouseLeave(object sender, MouseEventArgs e)
    {
        CastScrollLeftButton.Opacity = 0;
        CastScrollRightButton.Opacity = 0;
    }

    private void OnCastScrollLeftClick(object sender, RoutedEventArgs e)
    {
        CastScrollViewer.ScrollToHorizontalOffset(Math.Max(0, CastScrollViewer.HorizontalOffset - 350));
    }

    private void OnCastScrollRightClick(object sender, RoutedEventArgs e)
    {
        CastScrollViewer.ScrollToHorizontalOffset(CastScrollViewer.HorizontalOffset + 350);
    }

    private void OnCastScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled && sender is UIElement element)
        {
            e.Handled = true;

            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };

            var parent = System.Windows.Media.VisualTreeHelper.GetParent(element) as UIElement
                         ?? (element as FrameworkElement)?.Parent as UIElement;
            parent?.RaiseEvent(eventArg);
        }
    }
}
