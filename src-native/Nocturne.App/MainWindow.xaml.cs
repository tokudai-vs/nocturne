using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Nocturne.App.ViewModels;
using Nocturne.Core.Utils;

namespace Nocturne.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

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
                    PlayerHwndHost.Focus();
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

    private void OnExitPlayerClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.StopPlaybackAsync();
    }

    private void OnSkipPromptClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ExecuteSkipPrompt();
    }

    private void OnNextEpisodeClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.PlayNextEpisodeAsync();
    }

    private void OnPrevEpisodeClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.PlayPrevEpisodeAsync();
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
