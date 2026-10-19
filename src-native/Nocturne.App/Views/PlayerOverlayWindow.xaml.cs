using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Nocturne.App.ViewModels;

namespace Nocturne.App.Views;

public partial class PlayerOverlayWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Window _parentWindow;
    private readonly DispatcherTimer _inactivityTimer;
    private bool _isDraggingSlider;

    public PlayerOverlayWindow(MainViewModel viewModel, Window parentWindow)
    {
        _viewModel = viewModel;
        _parentWindow = parentWindow;
        DataContext = _viewModel;

        InitializeComponent();

        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _inactivityTimer.Tick += (s, e) =>
        {
            _inactivityTimer.Stop();
            HideControls();
        };

        KeyDown += OnWindowKeyDown;
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
            Nocturne.Core.Utils.NocturneLogger.Warn("PlayerOverlay", $"Could not set Window.Owner: {ex.Message}");
            try
            {
                var parentHwnd = new System.Windows.Interop.WindowInteropHelper(_parentWindow).Handle;
                if (parentHwnd != IntPtr.Zero)
                {
                    new System.Windows.Interop.WindowInteropHelper(this).Owner = parentHwnd;
                }
            }
            catch { }
        }
    }

    public void SyncWithParent()
    {
        if (!_viewModel.IsPlayerActive)
        {
            if (IsVisible) Hide();
            return;
        }

        if (!_parentWindow.IsVisible || _parentWindow.WindowState == WindowState.Minimized)
        {
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

        if (_viewModel.IsPlayerActive && !IsVisible)
        {
            Show();
            ResetInactivityTimer();
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
        if (_isDraggingSlider) return;
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

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        ResetInactivityTimer();
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetInactivityTimer();

        // If clicked directly on the transparent background (not on buttons or slider)
        if (e.OriginalSource == OverlayRootGrid)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                _ = _viewModel.TogglePauseAsync();
                e.Handled = true;
            }
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _inactivityTimer.Stop();
        Mouse.OverrideCursor = null;
        Hide();
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

    private void OnFullscreenToggleClick(object sender, RoutedEventArgs e)
    {
        ResetInactivityTimer();
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
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

    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
        _inactivityTimer.Stop();
        ShowControls();
    }

    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        _ = _viewModel.SeekToAsync(TimelineSlider.Value);
        ResetInactivityTimer();
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
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.Volume = Math.Max(0, _viewModel.Volume - 5);
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
