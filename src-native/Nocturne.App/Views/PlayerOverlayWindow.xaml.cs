using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.App.Views;

public partial class PlayerOverlayWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Window _parentWindow;
    private readonly DispatcherTimer _inactivityTimer;
    private readonly DispatcherTimer _singleClickTimer;
    private bool _isDraggingSlider;
    private double _previousVolume = 100;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    public PlayerOverlayWindow(MainViewModel viewModel, Window parentWindow)
    {
        _viewModel = viewModel;
        _parentWindow = parentWindow;
        DataContext = _viewModel;

        InitializeComponent();

        SourceInitialized += (s, e) => ApplyBorderlessDwm();

        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _inactivityTimer.Tick += (s, e) =>
        {
            _inactivityTimer.Stop();
            HideControls();
        };

        _singleClickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _singleClickTimer.Tick += (s, e) =>
        {
            _singleClickTimer.Stop();
            _ = _viewModel.TogglePauseAsync();
        };

        KeyDown += OnWindowKeyDown;

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Volume))
            {
                UpdateVolumeIcon();
            }
            else if (e.PropertyName == nameof(MainViewModel.CurrentPlaybackSeconds))
            {
                if (!_isDraggingSlider)
                {
                    TimelineSlider.Value = _viewModel.CurrentPlaybackSeconds;
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.TotalDurationSeconds))
            {
                TimelineSlider.Maximum = Math.Max(1, _viewModel.TotalDurationSeconds);
            }
        };
    }

    private void ApplyBorderlessDwm()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int colorNone = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
                int doNotRound = DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
            }
        }
        catch { }
    }

    private void EnsureOwner()
    {
        if (Owner != null) return;

        try
        {
            if (_parentWindow.IsVisible)
            {
                Owner = _parentWindow;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("PlayerOverlay", $"Could not set Window.Owner: {ex.Message}");
            try
            {
                var parentHwnd = new WindowInteropHelper(_parentWindow).Handle;
                if (parentHwnd != IntPtr.Zero)
                {
                    new WindowInteropHelper(this).Owner = parentHwnd;
                }
            }
            catch { }
        }
    }

    public void SyncWithParent()
    {
        if (!_viewModel.IsPlayerActive)
        {
            Mouse.OverrideCursor = null;
            if (IsVisible) Hide();
            return;
        }

        if (!_parentWindow.IsVisible || _parentWindow.WindowState == WindowState.Minimized)
        {
            Mouse.OverrideCursor = null;
            Hide();
            return;
        }

        var source = PresentationSource.FromVisual(_parentWindow);
        if (source == null) return;

        var pt = _parentWindow.PointToScreen(new Point(0, 0));
        var dpiX = source.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = source.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        Left = pt.X / dpiX;
        Top = pt.Y / dpiY;
        Width = _parentWindow.ActualWidth;
        Height = _parentWindow.ActualHeight;

        EnsureOwner();
        UpdateFullscreenIcon();
        UpdateVolumeIcon();
        UpdateCornerRadius();

        TimelineSlider.Maximum = Math.Max(1, _viewModel.TotalDurationSeconds);
        if (!_isDraggingSlider)
        {
            TimelineSlider.Value = _viewModel.CurrentPlaybackSeconds;
        }

        if (_viewModel.IsPlayerActive && !IsVisible)
        {
            Show();
            ResetInactivityTimer();
        }
    }

    private void UpdateFullscreenIcon()
    {
        if (FullscreenIcon == null) return;
        bool isFull = _parentWindow is MainWindow mw && mw.IsFullscreen;
        FullscreenIcon.Symbol = isFull ? SymbolRegular.ArrowMinimize24 : SymbolRegular.ArrowMaximize24;
    }

    private void UpdateVolumeIcon()
    {
        if (VolumeSpeakerIcon == null) return;
        VolumeSpeakerIcon.Symbol = _viewModel.Volume <= 0 ? SymbolRegular.SpeakerMute24 : SymbolRegular.Speaker224;
    }

    private void UpdateCornerRadius()
    {
        bool isFullscreen = _parentWindow is MainWindow mw && mw.IsFullscreen;
        bool isMaximized = _parentWindow.WindowState == WindowState.Maximized;

        if (isFullscreen || isMaximized)
        {
            if (OverlayRootBorder != null) OverlayRootBorder.CornerRadius = new CornerRadius(0);
            if (TopControlsBar != null) TopControlsBar.CornerRadius = new CornerRadius(0);
            if (BottomControlsBar != null) BottomControlsBar.CornerRadius = new CornerRadius(0);
        }
        else
        {
            if (OverlayRootBorder != null) OverlayRootBorder.CornerRadius = new CornerRadius(8);
            if (TopControlsBar != null) TopControlsBar.CornerRadius = new CornerRadius(8, 8, 0, 0);
            if (BottomControlsBar != null) BottomControlsBar.CornerRadius = new CornerRadius(0, 0, 8, 8);
        }
    }

    private void ShowControls()
    {
        TopControlsBar.Opacity = 1;
        BottomControlsBar.Opacity = 1;
        Mouse.OverrideCursor = null;
    }

    private void HideControls()
    {
        if (!IsVisible || !_viewModel.IsPlayerActive || _isDraggingSlider) return;
        TrackSelectorFlyout.Visibility = Visibility.Collapsed;
        TopControlsBar.Opacity = 0;
        BottomControlsBar.Opacity = 0;
        Mouse.OverrideCursor = Cursors.None;
    }

    private void ResetInactivityTimer()
    {
        ShowControls();
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == VisibilityProperty && Visibility != Visibility.Visible)
        {
            _inactivityTimer.Stop();
            TrackSelectorFlyout.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        TrackSelectorFlyout.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _inactivityTimer.Stop();
        _singleClickTimer.Stop();
        Mouse.OverrideCursor = null;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        ResetInactivityTimer();
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetInactivityTimer();

        // If the track flyout is currently open, clicking anywhere on the overlay closes it first
        if (TrackSelectorFlyout.Visibility == Visibility.Visible)
        {
            TrackSelectorFlyout.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        // If clicked directly on the transparent background (not on buttons or slider)
        if (e.OriginalSource == OverlayRootGrid || e.OriginalSource == OverlayRootBorder)
        {
            if (e.ClickCount >= 2)
            {
                _singleClickTimer.Stop();
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                _singleClickTimer.Stop();
                _singleClickTimer.Start();
                e.Handled = true;
            }
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _inactivityTimer.Stop();
        _singleClickTimer.Stop();
        Mouse.OverrideCursor = null;
        Hide();
        if (_parentWindow is MainWindow mw)
        {
            mw.ExitFullscreen();
            mw.RestoreForeground();
        }
        _ = _viewModel.StopPlaybackAsync();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _ = _viewModel.TogglePauseAsync();
    }

    private void OnSeekBackClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _ = _viewModel.SeekRelativeAsync(-10);
    }

    private void OnSeekForwardClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _ = _viewModel.SeekRelativeAsync(10);
    }

    private void OnPrevEpisodeClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _ = _viewModel.PlayPrevEpisodeAsync();
    }

    private void OnNextEpisodeClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _ = _viewModel.PlayNextEpisodeAsync();
    }

    private void OnSkipPromptClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        _viewModel.ExecuteSkipPrompt();
    }

    private void OnMuteToggleClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        if (_viewModel.Volume > 0)
        {
            _previousVolume = _viewModel.Volume;
            _viewModel.Volume = 0;
        }
        else
        {
            _viewModel.Volume = _previousVolume > 0 ? _previousVolume : 100;
        }
        UpdateVolumeIcon();
    }

    private void OnAudioTrackClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        if (TrackSelectorFlyout.Visibility == Visibility.Visible && FlyoutTitle.Text == "Audio Tracks")
        {
            TrackSelectorFlyout.Visibility = Visibility.Collapsed;
            return;
        }

        var streams = _viewModel.AvailableAudioStreams;
        var items = new List<(string label, string? badge, bool isSelected, Func<Task> onSelect)>();

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var mpvTrackId = i + 1;
            var title = !string.IsNullOrWhiteSpace(s.DisplayTitle)
                ? s.DisplayTitle
                : (!string.IsNullOrWhiteSpace(s.Language) ? s.Language : $"Track {mpvTrackId}");
            var badge = !string.IsNullOrWhiteSpace(s.Codec) ? s.Codec.ToUpperInvariant() : null;
            if (s.Channels.HasValue && s.Channels > 0)
            {
                badge = badge != null ? $"{badge} {s.Channels}ch" : $"{s.Channels}ch";
            }
            bool isSelected = (_viewModel.CurrentAudioTrackId == mpvTrackId) || (_viewModel.CurrentAudioTrackId == null && i == 0);

            items.Add((title, badge, isSelected, async () =>
            {
                await _viewModel.SetAudioTrackAsync(mpvTrackId);
            }));
        }

        PopulateTrackFlyout("Audio Tracks", SymbolRegular.SoundSource24, "Press A to cycle audio tracks", items);
    }

    private void OnSubtitleTrackClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        if (TrackSelectorFlyout.Visibility == Visibility.Visible && FlyoutTitle.Text == "Subtitles")
        {
            TrackSelectorFlyout.Visibility = Visibility.Collapsed;
            return;
        }

        var streams = _viewModel.AvailableSubtitleStreams;
        var items = new List<(string label, string? badge, bool isSelected, Func<Task> onSelect)>();

        bool isOffSelected = (_viewModel.CurrentSubtitleTrackId == 0) || (_viewModel.CurrentSubtitleTrackId == null && streams.Count == 0);
        items.Add(("Off", null, isOffSelected, async () =>
        {
            await _viewModel.SetSubtitleTrackAsync(0);
        }));

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var mpvTrackId = i + 1;
            var title = !string.IsNullOrWhiteSpace(s.DisplayTitle)
                ? s.DisplayTitle
                : (!string.IsNullOrWhiteSpace(s.Language) ? s.Language : $"Subtitle {mpvTrackId}");
            var badge = !string.IsNullOrWhiteSpace(s.Codec) ? s.Codec.ToUpperInvariant() : null;
            bool isSelected = (_viewModel.CurrentSubtitleTrackId == mpvTrackId) || (_viewModel.CurrentSubtitleTrackId == null && i == 0 && s.IsDefault == true);

            items.Add((title, badge, isSelected, async () =>
            {
                await _viewModel.SetSubtitleTrackAsync(mpvTrackId);
            }));
        }

        PopulateTrackFlyout("Subtitles", SymbolRegular.ClosedCaption24, "Press S to cycle subtitles", items);
    }

    private void PopulateTrackFlyout(
        string title,
        SymbolRegular headerIcon,
        string shortcutHint,
        List<(string label, string? badge, bool isSelected, Func<Task> onSelect)> items)
    {
        FlyoutTitle.Text = title;
        FlyoutHeaderIcon.Symbol = headerIcon;
        FlyoutShortcutHint.Text = shortcutHint;
        FlyoutTrackList.Children.Clear();

        foreach (var item in items)
        {
            var btn = new WpfButton
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(10, 8, 10, 8),
                Background = item.isSelected ? new SolidColorBrush(Color.FromArgb(0x28, 0xE5, 0xA0, 0x0D)) : System.Windows.Media.Brushes.Transparent,
                BorderBrush = item.isSelected ? (SolidColorBrush)Application.Current.Resources["NocturneAmberBrush"] : System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(item.isSelected ? 1.5 : 1)
            };

            var template = new ControlTemplate(typeof(WpfButton));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetBinding(Border.BackgroundProperty, new Binding("Background") { Source = btn });
            borderFactory.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { Source = btn });
            borderFactory.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { Source = btn });
            borderFactory.SetBinding(Border.PaddingProperty, new Binding("Padding") { Source = btn });

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);
            template.VisualTree = borderFactory;
            btn.Template = template;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (item.isSelected)
            {
                var checkIcon = new SymbolIcon
                {
                    Symbol = SymbolRegular.Checkmark24,
                    FontSize = 14,
                    Foreground = (SolidColorBrush)Application.Current.Resources["NocturneAmberBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(checkIcon, 0);
                grid.Children.Add(checkIcon);
            }

            var labelBlock = new WpfTextBlock
            {
                Text = item.label,
                FontSize = 13,
                FontWeight = item.isSelected ? FontWeights.Bold : FontWeights.Normal,
                Foreground = item.isSelected ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(labelBlock, 1);
            grid.Children.Add(labelBlock);

            if (!string.IsNullOrWhiteSpace(item.badge))
            {
                var badgeBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badgeBorder.Child = new WpfTextBlock
                {
                    Text = item.badge,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))
                };
                Grid.SetColumn(badgeBorder, 2);
                grid.Children.Add(badgeBorder);
            }

            btn.Content = grid;
            btn.MouseEnter += (s, e) =>
            {
                if (!item.isSelected)
                    btn.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            };
            btn.MouseLeave += (s, e) =>
            {
                if (!item.isSelected)
                    btn.Background = System.Windows.Media.Brushes.Transparent;
            };

            btn.Click += async (s, e) =>
            {
                ResetInactivityTimer();
                TrackSelectorFlyout.Visibility = Visibility.Collapsed;
                await item.onSelect();
            };

            FlyoutTrackList.Children.Add(btn);
        }

        TrackSelectorFlyout.Visibility = Visibility.Visible;
    }

    private void OnCloseFlyoutClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        TrackSelectorFlyout.Visibility = Visibility.Collapsed;
    }

    private void OnFlyoutMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetInactivityTimer();
        e.Handled = true;
    }

    private void OnFullscreenToggleClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_parentWindow is MainWindow mw)
        {
            mw.ToggleFullscreen();
        }
        else
        {
            if (_parentWindow.WindowState == WindowState.Maximized)
            {
                _parentWindow.WindowState = WindowState.Normal;
            }
            else
            {
                _parentWindow.WindowState = WindowState.Maximized;
            }
            SyncWithParent();
        }
        UpdateFullscreenIcon();
    }

    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new();
    private string? _lastThumbnailUrl;

    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
        _inactivityTimer.Stop();
        ShowControls();
        OnTimelineSliderMouseMove(sender, e);
    }

    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        _ = _viewModel.SeekToAsync(TimelineSlider.Value);
        ResetInactivityTimer();
        if (TimelineThumbnailPopup != null)
        {
            TimelineThumbnailPopup.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTimelineSliderMouseMove(object sender, MouseEventArgs e)
    {
        if (TimelineSlider.ActualWidth <= 0 || _viewModel.TotalDurationSeconds <= 0) return;

        var posOnSlider = e.GetPosition(TimelineSlider);
        double ratio = Math.Clamp(posOnSlider.X / TimelineSlider.ActualWidth, 0.0, 1.0);
        double targetSeconds = ratio * _viewModel.TotalDurationSeconds;

        var posInWindow = e.GetPosition(this);
        UpdateThumbnailPopup(targetSeconds, posInWindow.X);
    }

    private void OnTimelineSliderMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSlider && TimelineThumbnailPopup != null)
        {
            TimelineThumbnailPopup.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateThumbnailPopup(double targetSeconds, double mouseX)
    {
        if (TimelineThumbnailPopup == null) return;

        var ts = TimeSpan.FromSeconds(Math.Max(0, targetSeconds));
        ThumbnailTimeText.Text = ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");

        // Center popup horizontally over mouse cursor
        double popupWidth = 192;
        double left = Math.Clamp(mouseX - (popupWidth / 2.0), 16, Math.Max(16, ActualWidth - popupWidth - 16));
        TimelineThumbnailPopup.Margin = new Thickness(left, 0, 0, 94);
        TimelineThumbnailPopup.Visibility = Visibility.Visible;

        // Find chapter
        var item = _viewModel.CurrentItem;
        string? imageUrl = null;
        if (item?.Chapters != null && item.Chapters.Count > 0)
        {
            ChapterInfo? matchedChapter = null;
            int matchedIdx = -1;
            for (int i = item.Chapters.Count - 1; i >= 0; i--)
            {
                var ch = item.Chapters[i];
                if ((double)ch.StartPositionTicks / 10_000_000.0 <= targetSeconds)
                {
                    matchedChapter = ch;
                    matchedIdx = i;
                    break;
                }
            }

            if (matchedChapter != null)
            {
                if (!string.IsNullOrWhiteSpace(matchedChapter.Name))
                {
                    ThumbnailChapterText.Text = matchedChapter.Name;
                    ThumbnailChapterText.Visibility = Visibility.Visible;
                }
                else
                {
                    ThumbnailChapterText.Visibility = Visibility.Collapsed;
                }

                imageUrl = _viewModel.GetChapterImageUrl(matchedIdx, matchedChapter.ImageTag);
            }
            else
            {
                ThumbnailChapterText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ThumbnailChapterText.Visibility = Visibility.Collapsed;
        }

        // Fallback to item backdrop / primary image if no chapter image available
        imageUrl ??= _viewModel.GetPreviewFallbackImageUrl();

        if (string.IsNullOrEmpty(imageUrl))
        {
            ThumbnailPreviewImage.Source = null;
            return;
        }

        if (imageUrl == _lastThumbnailUrl && ThumbnailPreviewImage.Source != null) return;
        _lastThumbnailUrl = imageUrl;

        if (_thumbnailCache.TryGetValue(imageUrl, out var cachedBmp))
        {
            ThumbnailPreviewImage.Source = cachedBmp;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(imageUrl, UriKind.Absolute);
            bmp.DecodePixelWidth = 320;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();

            if (bmp.IsDownloading)
            {
                bmp.DownloadCompleted += (s, e) =>
                {
                    _thumbnailCache[imageUrl] = bmp;
                    if (_lastThumbnailUrl == imageUrl)
                    {
                        ThumbnailPreviewImage.Source = bmp;
                    }
                };
                bmp.DownloadFailed += (s, e) =>
                {
                    var fallback = _viewModel.GetPreviewFallbackImageUrl();
                    if (!string.IsNullOrEmpty(fallback) && fallback != imageUrl)
                    {
                        LoadFallbackThumbnail(fallback);
                    }
                };
            }
            else
            {
                _thumbnailCache[imageUrl] = bmp;
                ThumbnailPreviewImage.Source = bmp;
            }
        }
        catch
        {
            ThumbnailPreviewImage.Source = null;
        }
    }

    private void LoadFallbackThumbnail(string fallbackUrl)
    {
        try
        {
            if (_thumbnailCache.TryGetValue(fallbackUrl, out var cached))
            {
                ThumbnailPreviewImage.Source = cached;
                return;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(fallbackUrl, UriKind.Absolute);
            bmp.DecodePixelWidth = 320;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            _thumbnailCache[fallbackUrl] = bmp;
            ThumbnailPreviewImage.Source = bmp;
        }
        catch { }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        ResetInactivityTimer();

        switch (e.Key)
        {
            case Key.Space:
                _ = _viewModel.TogglePauseAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                if (TrackSelectorFlyout.Visibility == Visibility.Visible)
                {
                    TrackSelectorFlyout.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }
                OnExitClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                _ = _viewModel.SeekRelativeAsync(-10);
                e.Handled = true;
                break;
            case Key.Right:
                _ = _viewModel.SeekRelativeAsync(10);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.Volume = Math.Min(100, _viewModel.Volume + 5);
                UpdateVolumeIcon();
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.Volume = Math.Max(0, _viewModel.Volume - 5);
                UpdateVolumeIcon();
                e.Handled = true;
                break;
            case Key.M:
                OnMuteToggleClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.A:
                _ = _viewModel.CycleAudioAsync();
                e.Handled = true;
                break;
            case Key.S:
            case Key.C:
                _ = _viewModel.CycleSubtitleAsync();
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_viewModel.IsSkipPromptVisible)
                {
                    _viewModel.ExecuteSkipPrompt();
                    e.Handled = true;
                }
                break;
            case Key.OemPeriod: // >
            case Key.N:
                if (_viewModel.CanGoNextEpisode)
                {
                    _ = _viewModel.PlayNextEpisodeAsync();
                    e.Handled = true;
                }
                break;
            case Key.OemComma: // <
            case Key.P:
                if (_viewModel.CanGoPrevEpisode)
                {
                    _ = _viewModel.PlayPrevEpisodeAsync();
                    e.Handled = true;
                }
                break;
        }
    }
}
