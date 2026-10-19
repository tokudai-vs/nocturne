using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Nocturne.App.ViewModels;
using Nocturne.App.Views;
using Nocturne.Core.Utils;

namespace Nocturne.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private PlayerOverlayWindow? _playerOverlay;
    private bool _isFullscreen = false;
    private WindowState _previousState = WindowState.Normal;
    private WindowStyle _previousStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResize = ResizeMode.CanResize;

    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousState = WindowState;
            _previousStyle = WindowStyle;
            _previousResize = ResizeMode;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            ExitFullscreen();
        }

        _playerOverlay?.SyncWithParent();
    }

    public void ExitFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = _previousStyle;
            ResizeMode = _previousResize;
            WindowState = _previousState;
            _isFullscreen = false;
            _playerOverlay?.SyncWithParent();
        }
    }

    private PlayerOverlayWindow GetOrCreatePlayerOverlay()
    {
        if (_playerOverlay == null)
        {
            _playerOverlay = new PlayerOverlayWindow(_viewModel, this);
        }
        return _playerOverlay;
    }

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlayerActive))
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel.IsPlayerActive)
                    {
                        GetOrCreatePlayerOverlay().SyncWithParent();
                    }
                    else
                    {
                        ExitFullscreen();
                        _playerOverlay?.Hide();
                        Mouse.OverrideCursor = null;
                    }
                });
            }
        };

        LocationChanged += (s, e) => { if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent(); };
        SizeChanged += (s, e) => { if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent(); };
        StateChanged += (s, e) => { if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent(); };
        Closing += (s, e) =>
        {
            try { _playerOverlay?.Close(); } catch { }
        };

        // Wire embedded player host requests
        _viewModel.RequestEmbeddedPlayback += async (item, startTicks) =>
        {
            try
            {
                // Ensure HWND is created and measured with Render priority
                var retries = 0;
                while (PlayerHwndHost.Hwnd == IntPtr.Zero && retries < 20)
                {
                    await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                    if (PlayerHwndHost.Hwnd != IntPtr.Zero) break;
                    await Task.Delay(50);
                    retries++;
                }

                if (PlayerHwndHost.Hwnd != IntPtr.Zero)
                {
                    NocturneLogger.Info("MainWindow", $"Player HWND confirmed: {PlayerHwndHost.Hwnd}. Starting {item.Name}");
                    await _viewModel.ExecuteEmbeddedPlaybackAsync(PlayerHwndHost.Hwnd, item, startTicks);
                    GetOrCreatePlayerOverlay().SyncWithParent();
                }
                else
                {
                    NocturneLogger.Error("MainWindow", "PlayerHwndHost.Hwnd was not created after waiting!");
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("MainWindow", "Embedded playback launch failed", ex);
            }
        };

        // Window keyboard shortcuts during player and browsing
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SearchBox?.Focus();
                e.Handled = true;
                return;
            }

            if (_viewModel.IsPlayerActive)
            {
                if (e.Key == Key.Escape)
                {
                    ExitFullscreen();
                    _ = _viewModel.StopPlaybackAsync();
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && _viewModel.IsSkipPromptVisible)
                {
                    _viewModel.ExecuteSkipPrompt();
                    e.Handled = true;
                }
                else if (e.Key == Key.OemPeriod && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) // '>'
                {
                    _ = _viewModel.PlayNextEpisodeAsync();
                    e.Handled = true;
                }
                else if (e.Key == Key.OemComma && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) // '<'
                {
                    _ = _viewModel.PlayPrevEpisodeAsync();
                    e.Handled = true;
                }
            }
        };

        Loaded += async (s, e) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("MainWindow", "Init error", ex);
            }
        };
    }

    private void OnHomeTabClick(object sender, RoutedEventArgs e) => _viewModel.NavigateToHome();
    private void OnLibraryTabClick(object sender, RoutedEventArgs e) => _viewModel.NavigateToLibrary();
    private void OnSettingsMenuClick(object sender, RoutedEventArgs e) => _viewModel.NavigateToSettings();
    private void OnAnalyticsMenuClick(object sender, RoutedEventArgs e) => _viewModel.NavigateToAnalytics();
    private void OnSwitchServerMenuClick(object sender, RoutedEventArgs e) => _viewModel.SwitchServer();
    private void OnLogoutMenuClick(object sender, RoutedEventArgs e) => _viewModel.Logout();

    private void OnUserProfileClick(object sender, RoutedEventArgs e)
    {
        if (UserProfileButton.ContextMenu != null)
        {
            UserProfileButton.ContextMenu.PlacementTarget = UserProfileButton;
            UserProfileButton.ContextMenu.IsOpen = true;
        }
    }

    private void OnLogoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _viewModel.NavigateToHome();
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
