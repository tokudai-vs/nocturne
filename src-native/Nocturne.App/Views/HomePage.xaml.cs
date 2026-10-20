using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.App.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (s, e) => StopTrailer();
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible)
            {
                if (DataContext is HomeViewModel vm)
                {
                    UpdateTrailer(vm);
                }
            }
            else
            {
                StopTrailer();
            }
        };

        Loaded += async (_, _) =>
        {
            if (DataContext is HomeViewModel vm && vm.ContinueWatching.Count == 0 && vm.LatestMovies.Count == 0)
            {
                await vm.LoadAsync();
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is HomeViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is HomeViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            if (IsVisible)
            {
                UpdateTrailer(newVm);
            }
        }
        else
        {
            StopTrailer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.HeroTrailerUrl) && DataContext is HomeViewModel vm)
        {
            if (IsVisible)
            {
                UpdateTrailer(vm);
            }
        }
    }

    private void UpdateTrailer(HomeViewModel vm)
    {
        if (!IsVisible) return;
        if (!string.IsNullOrEmpty(vm.HeroTrailerUrl))
        {
            try
            {
                HeroTrailerPlayer.Source = new Uri(vm.HeroTrailerUrl);
                HeroTrailerPlayer.Play();
            }
            catch
            {
                HeroTrailerPlayer.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            StopTrailer();
        }
    }

    private void StopTrailer()
    {
        try
        {
            HeroTrailerPlayer.Stop();
            HeroTrailerPlayer.Source = null;
            HeroTrailerPlayer.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void OnTrailerMediaOpened(object sender, RoutedEventArgs e)
    {
        HeroTrailerPlayer.Opacity = 0;
        HeroTrailerPlayer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500));
        HeroTrailerPlayer.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void OnTrailerMediaEnded(object sender, RoutedEventArgs e)
    {
        HeroTrailerPlayer.Position = TimeSpan.Zero;
        HeroTrailerPlayer.Play();
    }

    private void OnTrailerMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HeroTrailerPlayer.Visibility = Visibility.Collapsed;
        NocturneLogger.Warn("HomePage", $"Hero trailer playback failed: {e.ErrorException?.Message}");
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

    private void OnContinueWatchingPlayClick(object sender, RoutedEventArgs e)
    {
        if (e is Controls.ItemClickEventArgs args && DataContext is HomeViewModel vm)
        {
            _ = vm.PlayItemAsync(args.Item);
        }
    }

    private void OnContinueWatchingShowClick(object sender, RoutedEventArgs e)
    {
        if (e is Controls.ItemClickEventArgs args && DataContext is HomeViewModel vm)
        {
            vm.OpenItemDetail(args.Item);
        }
    }

    private void OnContinueWatchingEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (e is Controls.ItemClickEventArgs args && DataContext is HomeViewModel vm)
        {
            vm.OpenItemDetail(args.Item);
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
