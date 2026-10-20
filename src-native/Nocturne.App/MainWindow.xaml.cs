using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Nocturne.App.Services;
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
    private double _previousLeft;
    private double _previousTop;
    private double _previousWidth;
    private double _previousHeight;

    public bool IsFullscreen => _isFullscreen;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    public void ApplyBorderlessDwm(bool isPlayer = false)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int borderColor = _isFullscreen ? 0x00000000 : DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                // Only disable rounded corners when in fullscreen mode; windowed media playback preserves Windows 11 rounded corners
                int cornerPref = _isFullscreen ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            }
        }
        catch { }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE || msg == WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var themeColorService = (Application.Current as App)?.Services.GetService<ThemeColorService>();
                themeColorService?.RefreshSystemAccentIfNeeded();
            });
        }
        return IntPtr.Zero;
    }

    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousState = WindowState;
            _previousStyle = WindowStyle;
            _previousResize = ResizeMode;
            _previousLeft = Left;
            _previousTop = Top;
            _previousWidth = Width;
            _previousHeight = Height;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            BorderThickness = new Thickness(0);
            BorderBrush = System.Windows.Media.Brushes.Transparent;
            Background = System.Windows.Media.Brushes.Black;
            WindowBackdropType = WindowBackdropType.None;

            var interop = new WindowInteropHelper(this);
            var hMonitor = MonitorFromWindow(interop.Handle, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                WindowState = WindowState.Normal;
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                Left = mi.rcMonitor.Left / dpiX;
                Top = mi.rcMonitor.Top / dpiY;
                Width = (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpiX;
                Height = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpiY;
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            _isFullscreen = true;
            ApplyBorderlessDwm(true);
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
            Topmost = false;
            WindowStyle = _previousStyle;
            ResizeMode = _previousResize;
            Left = _previousLeft;
            Top = _previousTop;
            Width = _previousWidth;
            Height = _previousHeight;
            WindowState = _previousState;
            _isFullscreen = false;
            Mouse.OverrideCursor = null;

            if (_viewModel.IsPlayerActive)
            {
                Background = System.Windows.Media.Brushes.Black;
                WindowBackdropType = WindowBackdropType.None;
                ApplyBorderlessDwm(true);
            }
            else
            {
                Background = (Brush)FindResource("ApplicationBackgroundBrush");
                WindowBackdropType = _viewModel.BackdropType;
                ApplyBorderlessDwm(false);
            }

            _playerOverlay?.SyncWithParent();
            RestoreForeground();
        }
    }

    public void RestoreForeground()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Momentarily toggle Topmost to force Windows DWM to bring this HWND to the top of the desktop Z-order
                bool wasTopmost = Topmost;
                Topmost = true;
                Topmost = wasTopmost;
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            Activate();
            Focus();
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("MainWindow", $"RestoreForeground exception: {ex.Message}");
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

        SourceInitialized += (s, e) =>
        {
            ApplyBorderlessDwm();
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    var source = HwndSource.FromHwnd(handle);
                    source?.AddHook(WndProc);
                }
            }
            catch { }
        };

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
                    e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        var themeColorService = (Application.Current as App)?.Services.GetService<ThemeColorService>();
                        themeColorService?.RefreshSystemAccentIfNeeded();
                    });
                }
            };
        }
        catch { }
        try
        {
            SystemThemeWatcher.Watch(this);
        }
        catch { }

        Activated += (s, e) =>
        {
            if (!_viewModel.IsPlayerActive)
            {
                Mouse.OverrideCursor = null;
            }
        };

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlayerActive))
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel.IsPlayerActive)
                    {
                        Background = System.Windows.Media.Brushes.Black;
                        WindowBackdropType = WindowBackdropType.None;
                        ApplyBorderlessDwm(true);
                        GetOrCreatePlayerOverlay().SyncWithParent();
                    }
                    else
                    {
                        ExitFullscreen();
                        _playerOverlay?.Hide();
                        Mouse.OverrideCursor = null;
                        Background = (Brush)FindResource("ApplicationBackgroundBrush");
                        WindowBackdropType = _viewModel.BackdropType;
                        ApplyBorderlessDwm(false);
                        RestoreForeground();
                    }
                });
            }
        };

        LocationChanged += (s, e) => { if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent(); };
        SizeChanged += (s, e) => { if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent(); };
        StateChanged += OnWindowStateChanged;
        Closing += (s, e) =>
        {
            try { _playerOverlay?.Close(); } catch { }
            try
            {
                if (_viewModel.IsPlayerActive)
                {
                    _ = _viewModel.StopPlaybackAsync();
                }
            }
            catch { }
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
                    Mouse.OverrideCursor = null;
                    ExitFullscreen();
                    _ = _viewModel.StopPlaybackAsync();
                    RestoreForeground();
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

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_viewModel.IsPlayerActive) _playerOverlay?.SyncWithParent();
        if (MaxIcon != null)
        {
            MaxIcon.Symbol = WindowState == WindowState.Maximized
                ? Wpf.Ui.Controls.SymbolRegular.SquareMultiple24
                : Wpf.Ui.Controls.SymbolRegular.Square24;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                if (FindVisualParent<System.Windows.Controls.Button>(dep) != null) return;
                if (FindVisualParent<System.Windows.Controls.TextBox>(dep) != null) return;
            }

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

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }
}
