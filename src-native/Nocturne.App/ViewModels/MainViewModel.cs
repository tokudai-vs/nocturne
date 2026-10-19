using Wpf.Ui.Controls;
using Nocturne.Core.Models;
using Nocturne.Core.Services;

namespace Nocturne.App.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly SettingsService _settings;
    private readonly EmbyClient _embyClient;
    private BaseViewModel _currentViewModel;
    private WindowBackdropType _backdropType = WindowBackdropType.Mica;
    private string _searchQuery = string.Empty;
    private bool _isSplashVisible = true;
    private string _splashStatusText = "Initializing Nocturne...";
    private bool _isNavigationVisible = true;

    public HomeViewModel HomeVM { get; }
    public LibraryViewModel LibraryVM { get; }
    public DetailViewModel DetailVM { get; }
    public SearchViewModel SearchVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AnalyticsViewModel AnalyticsVM { get; }
    public OnboardingViewModel OnboardingVM { get; }

    public MainViewModel(
        SettingsService settings,
        EmbyClient embyClient,
        HomeViewModel homeVM,
        LibraryViewModel libraryVM,
        DetailViewModel detailVM,
        SearchViewModel searchVM,
        SettingsViewModel settingsVM,
        AnalyticsViewModel analyticsVM,
        OnboardingViewModel onboardingVM)
    {
        _settings = settings;
        _embyClient = embyClient;

        HomeVM = homeVM;
        LibraryVM = libraryVM;
        DetailVM = detailVM;
        SearchVM = searchVM;
        SettingsVM = settingsVM;
        AnalyticsVM = analyticsVM;
        OnboardingVM = onboardingVM;

        _currentViewModel = HomeVM;

        // Wire navigation events
        HomeVM.NavigateToDetail += item => NavigateToDetail(item);
        LibraryVM.NavigateToDetail += item => NavigateToDetail(item);
        SearchVM.NavigateToDetail += item => NavigateToDetail(item);
        DetailVM.NavigateBack += () => CurrentViewModel = HomeVM;

        // Wire onboarding completion
        OnboardingVM.Authenticated += async () =>
        {
            IsNavigationVisible = true;
            CurrentViewModel = HomeVM;
            await HomeVM.LoadAsync();
            await LibraryVM.LoadViewsAsync();
        };

        UpdateBackdropFromSettings();
        _settings.SettingsChanged += _ => UpdateBackdropFromSettings();
    }

    public BaseViewModel CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                IsNavigationVisible = value is not OnboardingViewModel;
            }
        }
    }

    public WindowBackdropType BackdropType
    {
        get => _backdropType;
        set => SetProperty(ref _backdropType, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value) && !string.IsNullOrWhiteSpace(value))
            {
                SearchVM.Query = value;
                CurrentViewModel = SearchVM;
            }
        }
    }

    public bool IsSplashVisible
    {
        get => _isSplashVisible;
        set => SetProperty(ref _isSplashVisible, value);
    }

    public string SplashStatusText
    {
        get => _splashStatusText;
        set => SetProperty(ref _splashStatusText, value);
    }

    public bool IsNavigationVisible
    {
        get => _isNavigationVisible;
        set => SetProperty(ref _isNavigationVisible, value);
    }

    public void NavigateToHome()
    {
        CurrentViewModel = HomeVM;
        if (HomeVM.ContinueWatching.Count == 0 && HomeVM.LatestMovies.Count == 0)
        {
            _ = HomeVM.LoadAsync();
        }
    }

    public void NavigateToLibrary()
    {
        CurrentViewModel = LibraryVM;
        if (LibraryVM.Views.Count == 0)
        {
            _ = LibraryVM.LoadViewsAsync();
        }
    }

    public void NavigateToSettings() => CurrentViewModel = SettingsVM;
    public void NavigateToAnalytics() => CurrentViewModel = AnalyticsVM;

    public void NavigateToDetail(EmbyItem item)
    {
        _ = DetailVM.LoadItemAsync(item.Id);
        CurrentViewModel = DetailVM;
    }

    private void UpdateBackdropFromSettings()
    {
        BackdropType = _settings.Current.BackdropMaterial switch
        {
            "Acrylic" => WindowBackdropType.Acrylic,
            "Tabbed" => WindowBackdropType.Tabbed,
            "None" => WindowBackdropType.None,
            _ => WindowBackdropType.Mica
        };
    }

    public async Task InitializeAsync()
    {
        SplashStatusText = "Connecting to database & checking servers...";

        var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId)
                           ?? _settings.Current.Servers.FirstOrDefault();

        if (activeServer != null && !string.IsNullOrEmpty(activeServer.AccessToken))
        {
            SplashStatusText = $"Connecting to {activeServer.Name}...";
            _embyClient.Configure(activeServer.Url, activeServer.AccessToken, activeServer.UserId);

            // Pre-load data while splash is displaying
            var loadHomeTask = HomeVM.LoadAsync();
            var loadLibTask = LibraryVM.LoadViewsAsync();

            // Give user the slick splash experience (min 1.2s)
            await Task.Delay(1200);
            await Task.WhenAll(loadHomeTask, loadLibTask);

            IsNavigationVisible = true;
            CurrentViewModel = HomeVM;
        }
        else
        {
            await Task.Delay(800);
            IsNavigationVisible = false;
            OnboardingVM.LoadSavedServers();
            CurrentViewModel = OnboardingVM;
        }

        IsSplashVisible = false;
    }
}
