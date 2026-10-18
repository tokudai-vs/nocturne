using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
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

            // Progress bar
            if (item.ProgressPercentage > 0 && !item.IsPlayed)
            {
                ProgressBar.Value = item.ProgressPercentage;
                ProgressBar.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressBar.Visibility = Visibility.Collapsed;
            }

            // Image URL
            var emby = (Application.Current as App)?.Services.GetService<EmbyClient>();
            if (emby != null)
            {
                var tag = item.ImageTags?.GetValueOrDefault("Primary");
                var url = emby.BuildImageUrl(item.Id, "Primary", 320, tag);
                if (!string.IsNullOrEmpty(url))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(url);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        PosterImage.Source = bmp;
                    }
                    catch { }
                }
            }
        }
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
}
