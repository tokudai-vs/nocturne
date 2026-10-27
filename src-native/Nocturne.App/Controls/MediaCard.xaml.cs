using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;
using Nocturne.Core.Services;

namespace Nocturne.App.Controls;

public partial class MediaCard : UserControl
{
    public static readonly RoutedEvent CardClickEvent =
        EventManager.RegisterRoutedEvent(nameof(CardClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MediaCard));

    public event RoutedEventHandler CardClick
    {
        add => AddHandler(CardClickEvent, value);
        remove => RemoveHandler(CardClickEvent, value);
    }

    public MediaCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (s, e) =>
        {
            PosterImage.Source = null;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EmbyItem item)
        {
            WatchlistMenuItem.Visibility = MainViewModel.IsWatchlistable(item) ? Visibility.Visible : Visibility.Collapsed;
            TitleText.Text = item.DisplayTitle;
            SubtitleText.Text = item.SubtitleText;

            // Played badge
            PlayedBadge.Visibility = item.IsPlayed ? Visibility.Visible : Visibility.Collapsed;

            // Version badge for deduplicated titles
            if (item.VersionCount > 1)
            {
                VersionBadgeText.Text = $"{item.VersionCount} versions";
                VersionBadge.Visibility = Visibility.Visible;
            }
            else
            {
                VersionBadge.Visibility = Visibility.Collapsed;
            }

            // Progress bar & Continue Watching Context Menu
            var isResumable = (item.UserData?.PlaybackPositionTicks ?? 0) > 0 && !item.IsPlayed;
            if (isResumable)
            {
                ProgressBar.Value = item.ProgressPercentage;
                ProgressBar.Visibility = Visibility.Visible;
                RemoveFromContinueMenuItem.Visibility = Visibility.Visible;
                ContinueSeparator.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                RemoveFromContinueMenuItem.Visibility = Visibility.Collapsed;
                ContinueSeparator.Visibility = Visibility.Collapsed;
            }

            // Fallback tile
            FallbackTitleText.Text = item.DisplayTitle;
            FallbackYearText.Text = item.ProductionYear?.ToString() ?? string.Empty;
            PosterImage.Source = null;
            FallbackTile.Visibility = Visibility.Visible;

            // ── Artwork Resolution Order (§3.3): the URL the view model resolved, else Artwork on the same rules ──
            var settings = (Application.Current as App)?.Services.GetService<SettingsService>();
            string? finalImageUrl = null;

            if (settings != null)
            {
                finalImageUrl = !string.IsNullOrEmpty(item.PrimaryImageUrl) ? item.PrimaryImageUrl : Artwork.Poster(item, settings.Current.Servers, 320);

                if (!string.IsNullOrEmpty(finalImageUrl))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(finalImageUrl);
                        bmp.DecodePixelWidth = 220;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        if (bmp.CanFreeze) bmp.Freeze();
                        PosterImage.Source = bmp;
                        FallbackTile.Visibility = Visibility.Collapsed;
                    }
                    catch
                    {
                        PosterImage.Source = null;
                        FallbackTile.Visibility = Visibility.Visible;
                    }
                }
            }

            // Watchlist status check
            UpdateWatchlistUi(item);
        }
    }

    // §12.5: "+" / filled bookmark, revealed on hover and on keyboard focus (the button stays a tab stop at opacity 0),
    // always shown while the title is on the watchlist. Movies and series only.
    private void UpdateWatchlistUi(EmbyItem item)
    {
        WatchlistButton.Visibility = MainViewModel.IsWatchlistable(item) ? Visibility.Visible : Visibility.Collapsed;
        WatchlistButton.Opacity = item.IsInWatchlist || CardBox.IsMouseOver || WatchlistButton.IsKeyboardFocused ? 1 : 0;
        WatchlistIcon.Symbol = item.IsInWatchlist ? Wpf.Ui.Controls.SymbolRegular.Bookmark16 : Wpf.Ui.Controls.SymbolRegular.Add16;
        WatchlistIcon.Filled = item.IsInWatchlist;
        WatchlistIcon.Foreground = item.IsInWatchlist
            ? Application.Current?.Resources["NocturneAmberBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(229, 160, 13))
            : Application.Current?.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        var label = item.IsInWatchlist ? "Remove from Watchlist" : "Add to Watchlist";
        WatchlistButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(WatchlistButton, label);
    }

    private void OnWatchlistFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is EmbyItem item) UpdateWatchlistUi(item);
    }

    private async void OnWatchlistButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ToggleWatchlistAsync();
    }

    private async Task ToggleWatchlistAsync()
    {
        if (DataContext is not EmbyItem item || !MainViewModel.IsWatchlistable(item)) return;
        var main = (Application.Current as App)?.Services.GetService<MainViewModel>();
        if (main == null) return;
        var toggle = main.SetWatchlistedAsync(item, !item.IsInWatchlist); // flips the item before its first await
        UpdateWatchlistUi(item);
        await toggle;
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is EmbyItem hovered) UpdateWatchlistUi(hovered);
        CardBorder.BorderThickness = new Thickness(2);
        CardBorder.BorderBrush = Application.Current?.Resources["NocturneAmberBrush"] as Brush
            ?? new SolidColorBrush(Color.FromRgb(229, 160, 13));
        Panel.SetZIndex(this, 10);
        Panel.SetZIndex(CardBox, 10);

        var animX = new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(180));
        var animY = new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(180));
        CardScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        CardScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is EmbyItem item) UpdateWatchlistUi(item);

        CardBorder.BorderThickness = new Thickness(1);
        CardBorder.BorderBrush = Application.Current?.TryFindResource("CardStrokeColorDefaultBrush") as Brush
            ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        Panel.SetZIndex(this, 0);
        Panel.SetZIndex(CardBox, 0);

        var animX = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180));
        var animY = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180));
        CardScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        CardScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(CardClickEvent, this));
    }

    // Through the one playback path (owning server, PlaybackSession progress/stop reporting, Trakt queue). This
    // used to start mpv directly on the active server's client with no session: no stop report, no resume point.
    private void OnContextMenuPlayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            var main = (Application.Current as App)?.Services.GetService<MainViewModel>();
            if (main != null) _ = main.StartPlaybackAsync(item, item.UserData?.PlaybackPositionTicks ?? 0);
        }
    }

    private async void OnContextMenuTogglePlayedClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            var emby = (Application.Current as App)?.Services.GetService<EmbyClient>();
            if (emby != null)
            {
                var newPlayed = !item.IsPlayed;
                var success = newPlayed ? await emby.MarkPlayedAsync(item.Id) : await emby.MarkUnplayedAsync(item.Id);
                if (success)
                {
                    item.UserData ??= new UserItemData();
                    item.UserData.Played = newPlayed;
                    PlayedBadge.Visibility = newPlayed ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }

    private async void OnContextMenuToggleWatchlistClick(object sender, RoutedEventArgs e) => await ToggleWatchlistAsync();

    private async void OnContextMenuRemoveFromContinueClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            var homeVM = (Application.Current as App)?.Services.GetService<HomeViewModel>();
            if (homeVM != null)
            {
                await homeVM.RemoveFromContinueWatchingAsync(item);
            }
        }
    }
}
