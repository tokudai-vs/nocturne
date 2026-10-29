using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Nocturne.App.Services;
using Nocturne.App.ViewModels;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public IServiceProvider Services => _serviceProvider!;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject?.ToString());
        };

        DispatcherUnhandledException += (s, e) =>
        {
            LogCrash("DispatcherUnhandledException", e.Exception.ToString());

            // Benign teardown exceptions during exit or process shutdown should never trigger an error pop-up
            if (e.Exception is IOException ioEx && ioEx.Message.Contains("Pipe", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                return;
            }

            MessageBox.Show(
                $"Nocturne encountered an unexpected error and needs to close:\n\n{e.Exception.Message}\n\nDetails saved to %APPDATA%\\Nocturne\\crash.log",
                "Nocturne Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception.ToString());
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, string? details)
    {
        try
        {
            var dir = AppPaths.DataDirectory;
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [{source}]\n{details}\n\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Startup timing: one log line per step, so a slow launch can be attributed from nocturne.log.
            var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            NocturneLogger.Info("Startup", BuildInfo.Describe); // which build wrote what follows (audit open question 12)
            NocturneLogger.Info("Startup", $"mpv: {MpvController.FindMpvExecutable() ?? "NOT FOUND (playback will fail)"}"); // #40
            NocturneLogger.Info("Startup", $"runtime + WPF ready: {(DateTime.Now - processStart).TotalMilliseconds:F0} ms after process start");
            var step = System.Diagnostics.Stopwatch.StartNew();
            void Timed(string name) { NocturneLogger.Info("Startup", $"{name}: {step.ElapsedMilliseconds} ms"); step.Restart(); }

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Apply theme and accent color before mainWindow is instantiated and shown
            var settings = _serviceProvider.GetRequiredService<SettingsService>();
            if (settings.LoadFailed)
            {
                var choice = MessageBox.Show(
                    $"Nocturne could not read its settings file:\n\n{settings.SettingsPath}\n\n{settings.LoadError}\n\n" +
                    "Yes: start with default settings. The unreadable file is kept next to it under a new name.\n" +
                    "No: exit without changing anything.",
                    "Nocturne Settings Error",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error);
                if (choice != MessageBoxResult.Yes)
                {
                    Shutdown(2);
                    return;
                }
                settings.AcceptDefaults();
            }
            Timed("settings load");
            _serviceProvider.GetRequiredService<DatabaseService>();
            Timed("DatabaseService.Initialize (schema, migrations, self-check)");

            // ApplyAppTheme also constructs the view model graph (via MainViewModel.UpdateTopBarColors).
            ApplyAppTheme(settings.Current.Theme);
            var themeColors = _serviceProvider.GetRequiredService<ThemeColorService>();
            themeColors.ApplyColor(settings.Current.AccentColor);
            Timed("theme + view model graph construction");

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            Timed("MainWindow construction");
            mainWindow.ContentRendered += (_, _) =>
                NocturneLogger.Info("Startup", $"first render: {(DateTime.Now - processStart).TotalMilliseconds:F0} ms after process start");
            mainWindow.Show();
            Timed("MainWindow.Show");

            // Live real-time system theme watcher across Windows theme toggle
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, ev) =>
            {
                if (ev.Category == Microsoft.Win32.UserPreferenceCategory.General ||
                    ev.Category == Microsoft.Win32.UserPreferenceCategory.VisualStyle)
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var srv = _serviceProvider?.GetService<SettingsService>();
                            var currentTheme = srv?.Current?.Theme ?? "system";
                            if (currentTheme.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                                currentTheme.Equals("auto", StringComparison.OrdinalIgnoreCase))
                            {
                                ApplicationThemeManager.ApplySystemTheme();
                                SettingsViewModel.UpdateThemeGradients();
                            }
                            var themeService = _serviceProvider?.GetService<ThemeColorService>();
                            themeService?.RefreshSystemAccentIfNeeded();
                            themeService?.UpdateAdaptiveTextColors();
                        }
                        catch { }
                    });
                }
            };
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex.ToString());
            MessageBox.Show(
                $"Failed to start Nocturne:\n\n{ex.Message}\n\nCheck %APPDATA%\\Nocturne\\crash.log for details.",
                "Nocturne Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<EmbyClient>();
        services.AddSingleton<DedupEngine>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<MpvController>();
        services.AddSingleton<TraktService>();
        services.AddSingleton<ITraktAuthService, TraktAuthService>();
        services.AddSingleton<ITraktScrobbleService, TraktScrobbleService>();
        services.AddSingleton<ITraktSyncService, TraktSyncService>();
        services.AddSingleton<IWatchlistService, WatchlistService>();
        services.AddSingleton<ITmdbEnrichmentService, TmdbEnrichmentService>();
        services.AddSingleton<EnrichmentGate>();
        services.AddSingleton<TraktQueueDrainWorker>();
        services.AddSingleton<IntroDbService>();
        services.AddSingleton<ThemeColorService>();
        services.AddSingleton<TrailerService>();
        services.AddSingleton<EmbyConnectService>();
        services.AddSingleton<WatchPartyBinaryManager>();
        services.AddSingleton<WatchPartyService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<WatchlistViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<DetailViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AnalyticsViewModel>();
        services.AddSingleton<OnboardingViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

    private void ApplyAppTheme(string theme)
    {
        try
        {
            switch (theme.ToLowerInvariant())
            {
                case "dark":
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    break;
                case "light":
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    break;
                default:
                    ApplicationThemeManager.ApplySystemTheme();
                    break;
            }
            SettingsViewModel.UpdateThemeGradients();
            _serviceProvider?.GetService<ThemeColorService>()?.UpdateAdaptiveTextColors();
            _serviceProvider?.GetService<MainViewModel>()?.UpdateTopBarColors();
        }
        catch (Exception ex)
        {
            LogCrash("ApplyAppTheme", ex.ToString());
            try { ApplicationThemeManager.Apply(ApplicationTheme.Dark); } catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _serviceProvider?.GetService<WatchPartyService>()?.Dispose(); } catch { }
        try { _serviceProvider?.GetService<MpvController>()?.Dispose(); } catch { }
        try { _serviceProvider?.GetService<DatabaseService>()?.Dispose(); } catch { }
        try { _serviceProvider?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
