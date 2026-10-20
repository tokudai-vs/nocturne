using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class DetailPage : UserControl
{
    private System.Windows.Threading.DispatcherTimer? _castScrollTimer;
    private double _castScrollTargetOffset;
    private double _castScrollStartOffset;
    private DateTime _castScrollStartTime;
    private const double CastScrollDurationMs = 280;

    public DetailPage()
    {
        InitializeComponent();
        Focusable = true;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (s, e) =>
        {
            _castScrollTimer?.Stop();
            StopTrailer();
        };

        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible)
            {
                if (DataContext is DetailViewModel vm)
                {
                    UpdateBackgroundTrailer(vm);
                }
            }
            else
            {
                _castScrollTimer?.Stop();
                StopTrailer();
            }
        };

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

    private void OnWatchPartyButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.OpenWatchPartyAsync();
        }
    }

    private void OnCloseWatchPartyModalClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            vm.CloseWatchPartyModal();
        }
    }

    private void OnStartWatchPartyConfirmClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.StartWatchPartySessionAsync();
        }
    }

    private void OnCopyWatchPartyLinkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            vm.CopyWatchPartyLink();
        }
    }

    private void OnWatchPartyStartShowClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.StartWatchPartyShowAndPlayAsync();
        }
    }

    private void OnWatchPartyEndClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetailViewModel vm)
        {
            _ = vm.StopWatchPartySessionAsync();
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

    private bool _isCastHovered;

    private void UpdateCastScrollButtonVisibility()
    {
        if (CastScrollViewer == null) return;
        var scrollable = CastScrollViewer.ScrollableWidth > 5;
        if (!scrollable || !_isCastHovered)
        {
            CastScrollLeftButton.Opacity = 0;
            CastScrollRightButton.Opacity = 0;
            CastLeftScrim.Opacity = 0;
            CastRightScrim.Opacity = 0;
            return;
        }

        var canScrollLeft = CastScrollViewer.HorizontalOffset > 2;
        var canScrollRight = CastScrollViewer.HorizontalOffset < (CastScrollViewer.ScrollableWidth - 2);

        CastScrollLeftButton.Opacity = canScrollLeft ? 1 : 0;
        CastLeftScrim.Opacity = canScrollLeft ? 1 : 0;
        CastScrollRightButton.Opacity = canScrollRight ? 1 : 0;
        CastRightScrim.Opacity = canScrollRight ? 1 : 0;
    }

    private void OnCastRowMouseEnter(object sender, MouseEventArgs e)
    {
        _isCastHovered = true;
        UpdateCastScrollButtonVisibility();
    }

    private void OnCastRowMouseLeave(object sender, MouseEventArgs e)
    {
        _isCastHovered = false;
        CastScrollLeftButton.Opacity = 0;
        CastScrollRightButton.Opacity = 0;
        CastLeftScrim.Opacity = 0;
        CastRightScrim.Opacity = 0;
    }

    private void AnimateCastScroll(double targetOffset)
    {
        _castScrollTimer?.Stop();

        _castScrollStartOffset = CastScrollViewer.HorizontalOffset;
        var maxOffset = CastScrollViewer.ScrollableWidth;
        _castScrollTargetOffset = Math.Max(0, Math.Min(targetOffset, maxOffset));
        _castScrollStartTime = DateTime.UtcNow;

        _castScrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _castScrollTimer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.UtcNow - _castScrollStartTime).TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / CastScrollDurationMs);

            // Cubic ease-out: 1 - (1 - t)^3
            var ease = 1.0 - Math.Pow(1.0 - t, 3);
            var current = _castScrollStartOffset + (_castScrollTargetOffset - _castScrollStartOffset) * ease;

            CastScrollViewer.ScrollToHorizontalOffset(current);
            UpdateCastScrollButtonVisibility();

            if (t >= 1.0)
            {
                _castScrollTimer?.Stop();
            }
        };
        _castScrollTimer.Start();
    }

    private void OnCastScrollLeftClick(object sender, RoutedEventArgs e)
    {
        AnimateCastScroll(CastScrollViewer.HorizontalOffset - 350);
    }

    private void OnCastScrollRightClick(object sender, RoutedEventArgs e)
    {
        AnimateCastScroll(CastScrollViewer.HorizontalOffset + 350);
    }

    private void OnCastScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScrollViewer.ScrollToVerticalOffset(PageScrollViewer.VerticalOffset - (e.Delta * 0.8));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DetailViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is DetailViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            if (IsVisible)
            {
                UpdateBackgroundTrailer(newVm);
            }
        }
        else
        {
            StopTrailer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailViewModel.BackgroundTrailerUrl) && DataContext is DetailViewModel vm)
        {
            if (IsVisible)
            {
                UpdateBackgroundTrailer(vm);
            }
        }
        else if (e.PropertyName == nameof(DetailViewModel.Item))
        {
            PageScrollViewer?.ScrollToTop();
            CastScrollViewer?.ScrollToLeftEnd();
        }
    }

    private void UpdateBackgroundTrailer(DetailViewModel vm)
    {
        if (!IsVisible) return;
        if (!string.IsNullOrEmpty(vm.BackgroundTrailerUrl))
        {
            try
            {
                BackgroundTrailerPlayer.Source = new Uri(vm.BackgroundTrailerUrl);
                BackgroundTrailerPlayer.Play();
            }
            catch
            {
                BackgroundTrailerPlayer.Visibility = Visibility.Collapsed;
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
            BackgroundTrailerPlayer.Stop();
            BackgroundTrailerPlayer.Source = null;
            BackgroundTrailerPlayer.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void OnTrailerMediaOpened(object sender, RoutedEventArgs e)
    {
        BackgroundTrailerPlayer.Opacity = 0;
        BackgroundTrailerPlayer.Visibility = Visibility.Visible;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500));
        BackgroundTrailerPlayer.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void OnTrailerMediaEnded(object sender, RoutedEventArgs e)
    {
        BackgroundTrailerPlayer.Position = TimeSpan.Zero;
        BackgroundTrailerPlayer.Play();
    }

    private void OnTrailerMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        BackgroundTrailerPlayer.Visibility = Visibility.Collapsed;
        Nocturne.Core.Utils.NocturneLogger.Warn("DetailPage", $"Background trailer playback failed: {e.ErrorException?.Message}");
    }
}
