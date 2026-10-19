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

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

        _playerOverlay = new PlayerOverlayWindow(_viewModel, this);

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlayerActive))
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel.IsPlayerActive)
                    {
                        _playerOverlay?.SyncWithParent();
                    }
                    else
                    {
                        _playerOverlay?.Hide();
                        Mouse.OverrideCursor = null;
                    }
                });
            }
        };

        LocationChanged += (s, e) => _playerOverlay?.SyncWithParent();
        SizeChanged += (s, e) => _playerOverlay?.SyncWithParent();
        StateChanged += (s, e) => _playerOverlay?.SyncWithParent();
        Closing += (s, e) => _playerOverlay?.Close();

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
                    _playerOverlay?.SyncWithParent();
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
            if (_viewModel.IsPlayerActive)
            {
                if (e.Key == Key.Escape)
                {
                    _ = _viewModel.StopPlaybackAsync();
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

    private void OnNavItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToTag(tag);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToTag(tag);
        }
    }

    private void NavigateToTag(string tag)
    {
        NocturneLogger.Info("MainWindow", $"Navigating to {tag}");
        switch (tag)
        {
            case "home":
                _viewModel.NavigateToHome();
                break;
            case "library":
                _viewModel.NavigateToLibrary();
                break;
            case "analytics":
                _viewModel.NavigateToAnalytics();
                break;
            case "settings":
                _viewModel.NavigateToSettings();
                break;
        }
    }
}
