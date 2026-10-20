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

            // Image URL
            var emby = (Application.Current as App)?.Services.GetService<EmbyClient>();
            var settings = (Application.Current as App)?.Services.GetService<SettingsService>();
            if (emby != null)
            {
                ServerConfig? server = null;
                if (!string.IsNullOrEmpty(item.ServerId) && settings != null)
                {
                    server = settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId);
                }

                var tag = item.ImageTags?.GetValueOrDefault("Primary");
                var url = !string.IsNullOrEmpty(item.PrimaryImageUrl)
                    ? item.PrimaryImageUrl
                    : emby.BuildImageUrl(item.Id, "Primary", 320, tag, server?.Url, server?.AccessToken);

                if (!string.IsNullOrEmpty(url))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(url);
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
        }
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
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

    private async void OnContextMenuPlayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            var mpv = (Application.Current as App)?.Services.GetService<MpvController>();
            var emby = (Application.Current as App)?.Services.GetService<EmbyClient>();
            var db = (Application.Current as App)?.Services.GetService<DatabaseService>();
            var trakt = (Application.Current as App)?.Services.GetService<TraktService>();

            if (mpv != null && emby != null)
            {
                var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
                var streamUrl = emby.GetStreamUrl(item.Id, mediaSourceId);
                var startTicks = item.UserData?.PlaybackPositionTicks ?? 0;

                await mpv.PlayAsync(streamUrl, item.Name, startTicks);
                if (trakt != null) await trakt.ScrobbleStartAsync(item, item.ProgressPercentage);
                db?.RecordWatchEvent(item.Id, emby.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);
            }
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

    private async void OnContextMenuToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            var emby = (Application.Current as App)?.Services.GetService<EmbyClient>();
            if (emby != null)
            {
                var newFav = !(item.UserData?.IsFavorite ?? false);
                var success = await emby.ToggleFavoriteAsync(item.Id, newFav);
                if (success)
                {
                    item.UserData ??= new UserItemData();
                    item.UserData.IsFavorite = newFav;
                }
            }
        }
    }

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
