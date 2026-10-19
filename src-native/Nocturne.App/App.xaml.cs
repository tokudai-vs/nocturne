using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Nocturne.App.Services;
using Nocturne.App.ViewModels;
using Nocturne.Core.Services;

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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Nocturne");
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
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Apply theme after mainWindow is instantiated and shown
            var settings = _serviceProvider.GetRequiredService<SettingsService>();
            ApplyAppTheme(settings.Current.Theme);

            var themeColors = _serviceProvider.GetRequiredService<ThemeColorService>();
            themeColors.ApplyColor(settings.Current.AccentColor);
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
        services.AddSingleton<IntroDbService>();
        services.AddSingleton<ThemeColorService>();
        services.AddSingleton<EmbyConnectService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
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
        }
        catch (Exception ex)
        {
            LogCrash("ApplyAppTheme", ex.ToString());
            try { ApplicationThemeManager.Apply(ApplicationTheme.Dark); } catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<MpvController>()?.Dispose();
        _serviceProvider?.GetService<DatabaseService>()?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
