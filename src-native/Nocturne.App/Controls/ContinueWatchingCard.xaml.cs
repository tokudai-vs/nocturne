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

public partial class ContinueWatchingCard : UserControl
{
    public static readonly RoutedEvent PlayClickEvent =
        EventManager.RegisterRoutedEvent(nameof(PlayClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ContinueWatchingCard));

    public static readonly RoutedEvent ShowClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ShowClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ContinueWatchingCard));

    public static readonly RoutedEvent EpisodeClickEvent =
        EventManager.RegisterRoutedEvent(nameof(EpisodeClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ContinueWatchingCard));

    public event RoutedEventHandler PlayClick
    {
        add => AddHandler(PlayClickEvent, value);
        remove => RemoveHandler(PlayClickEvent, value);
    }

    public event RoutedEventHandler ShowClick
    {
        add => AddHandler(ShowClickEvent, value);
        remove => RemoveHandler(ShowClickEvent, value);
    }

    public event RoutedEventHandler EpisodeClick
    {
        add => AddHandler(EpisodeClickEvent, value);
        remove => RemoveHandler(EpisodeClickEvent, value);
    }

    public ContinueWatchingCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EmbyItem item)
        {
            WatchlistMenuItem.Visibility = MainViewModel.IsWatchlistable(item) ? Visibility.Visible : Visibility.Collapsed;
            // Line 1: Show / Series Name in Bold
            var isEpisode = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase);
            if (isEpisode && !string.IsNullOrWhiteSpace(item.SeriesName))
            {
                ShowNameText.Text = item.SeriesName;
            }
            else
            {
                ShowNameText.Text = item.DisplayTitle;
            }

            // Line 2: Season & Episode Name (e.g. S2:E3 - Everything Beautiful, Everything Terrible)
            if (isEpisode)
            {
                if (item.ParentIndexNumber.HasValue && item.IndexNumber.HasValue)
                {
                    EpisodeNameText.Text = $"S{item.ParentIndexNumber}:E{item.IndexNumber} - {item.Name}";
                }
                else
                {
                    EpisodeNameText.Text = item.Name;
                }
            }
            else
            {
                EpisodeNameText.Text = item.ProductionYear > 0 ? $"{item.ProductionYear}" : item.OfficialRating ?? string.Empty;
            }

            // Slim progress bar showing watched percentage
            var pct = item.ProgressPercentage;
            if (pct > 0)
            {
                ProgressBar.Value = pct;
                ProgressTrack.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressTrack.Visibility = Visibility.Collapsed;
            }

            // Load 16:9 Landscape Artwork (Episode Primary thumbnail or Backdrop)
            // Spec §3.3 (A0): the URL the view model resolved, else the group's poster, else its backdrop (Artwork).
            var settings = (Application.Current as App)?.Services.GetService<SettingsService>();
            if (settings != null)
            {
                var servers = settings.Current.Servers;
                var url = !string.IsNullOrEmpty(item.PrimaryImageUrl)
                    ? item.PrimaryImageUrl
                    : Artwork.Poster(item, servers, 500) ?? Artwork.Backdrop(item, servers, 500);

                if (!string.IsNullOrEmpty(url))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(url);
                        bmp.DecodePixelWidth = 320;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        if (bmp.CanFreeze) bmp.Freeze();
                        ArtImage.Source = bmp;
                    }
                    catch { }
                }
            }
        }
    }

    private void OnArtClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            RaiseEvent(new ItemClickEventArgs(PlayClickEvent, this, item));
        }
    }

    private void OnShowNameClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is EmbyItem item)
        {
            // If it's an episode and has SeriesId, create target for the Show
            if (!string.IsNullOrEmpty(item.SeriesId))
            {
                var showTarget = new EmbyItem
                {
                    Id = item.SeriesId,
                    ServerId = item.ServerId,
                    ServerName = item.ServerName,
                    Name = item.SeriesName ?? item.Name,
                    Type = "Series"
                };
                RaiseEvent(new ItemClickEventArgs(ShowClickEvent, this, showTarget));
            }
            else
            {
                RaiseEvent(new ItemClickEventArgs(ShowClickEvent, this, item));
            }
        }
    }

    private void OnEpisodeNameClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is EmbyItem item)
        {
            RaiseEvent(new ItemClickEventArgs(EpisodeClickEvent, this, item));
        }
    }

    private void OnArtMouseEnter(object sender, MouseEventArgs e)
    {
        HoverScrim.Opacity = 1.0;
        HoverPlayButton.Opacity = 1.0;
        ArtBorder.BorderThickness = new Thickness(2);
        ArtBorder.BorderBrush = Application.Current?.Resources["NocturneAmberBrush"] as Brush
            ?? new SolidColorBrush(Color.FromRgb(229, 160, 13));
        Panel.SetZIndex(this, 10);
        Panel.SetZIndex(ArtBox, 10);

        var animX = new DoubleAnimation(1.03, TimeSpan.FromMilliseconds(180));
        var animY = new DoubleAnimation(1.03, TimeSpan.FromMilliseconds(180));
        ArtScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        ArtScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void OnArtMouseLeave(object sender, MouseEventArgs e)
    {
        HoverScrim.Opacity = 0.0;
        HoverPlayButton.Opacity = 0.0;
        ArtBorder.BorderThickness = new Thickness(1);
        ArtBorder.BorderBrush = Application.Current?.TryFindResource("CardStrokeColorDefaultBrush") as Brush
            ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        Panel.SetZIndex(this, 0);
        Panel.SetZIndex(ArtBox, 0);

        var animX = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180));
        var animY = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180));
        ArtScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        ArtScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void OnShowNameMouseEnter(object sender, MouseEventArgs e)
    {
        if (Application.Current?.Resources["NocturneAmberBrush"] is Brush amber)
        {
            ShowNameText.Foreground = amber;
        }
    }

    private void OnShowNameMouseLeave(object sender, MouseEventArgs e)
    {
        if (Application.Current?.TryFindResource("TextFillColorPrimaryBrush") is Brush primary)
        {
            ShowNameText.Foreground = primary;
        }
    }

    private void OnEpisodeNameMouseEnter(object sender, MouseEventArgs e)
    {
        if (Application.Current?.Resources["NocturneAmberBrush"] is Brush amber)
        {
            EpisodeNameText.Foreground = amber;
        }
    }

    private void OnEpisodeNameMouseLeave(object sender, MouseEventArgs e)
    {
        if (Application.Current?.TryFindResource("TextFillColorSecondaryBrush") is Brush secondary)
        {
            EpisodeNameText.Foreground = secondary;
        }
    }

    private void OnContextMenuPlayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item)
        {
            RaiseEvent(new ItemClickEventArgs(PlayClickEvent, this, item));
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
                }
            }
        }
    }

    private async void OnContextMenuToggleWatchlistClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmbyItem item && MainViewModel.IsWatchlistable(item))
        {
            var main = (Application.Current as App)?.Services.GetService<MainViewModel>();
            if (main != null) await main.SetWatchlistedAsync(item, !item.IsInWatchlist);
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
