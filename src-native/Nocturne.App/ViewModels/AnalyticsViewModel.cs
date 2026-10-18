using System.Collections.ObjectModel;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class AnalyticsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly TraktService _trakt;
    private AnalyticsStats? _stats;
    private int _selectedDays = 30;
    private string _selectedSource = "combined";
    private string _formattedWatchTime = "0h 0m";
    private string _formattedAverageWeekly = "0.0h / week";
    private bool _isLoading;
    private bool _hasLifetimeStats;
    private TraktUserStats? _lifetimeStats;

    public ObservableCollection<GenreCount> TopGenres { get; } = new();
    public ObservableCollection<TopMediaItem> TopSeries { get; } = new();
    public ObservableCollection<TopMediaItem> TopMovies { get; } = new();
    public ObservableCollection<DayActivity> HeatmapCells { get; } = new();
    public ObservableCollection<FrequencyBarItem> FrequencyBars { get; } = new();

    public AnalyticsViewModel(DatabaseService db, TraktService trakt)
    {
        _db = db;
        _trakt = trakt;
    }

    public AnalyticsStats? Stats
    {
        get => _stats;
        set => SetProperty(ref _stats, value);
    }

    public int SelectedDays
    {
        get => _selectedDays;
        set
        {
            if (SetProperty(ref _selectedDays, value))
            {
                _ = LoadStatsAsync();
            }
        }
    }

    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                _ = LoadStatsAsync();
            }
        }
    }

    public string FormattedWatchTime
    {
        get => _formattedWatchTime;
        set => SetProperty(ref _formattedWatchTime, value);
    }

    public string FormattedAverageWeekly
    {
        get => _formattedAverageWeekly;
        set => SetProperty(ref _formattedAverageWeekly, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool HasLifetimeStats
    {
        get => _hasLifetimeStats;
        set => SetProperty(ref _hasLifetimeStats, value);
    }

    public TraktUserStats? LifetimeStats
    {
        get => _lifetimeStats;
        set => SetProperty(ref _lifetimeStats, value);
    }

    public async Task LoadStatsAsync()
    {
        IsLoading = true;
        try
        {
            await Task.Run(() =>
            {
                var stats = _db.GetAnalytics(SelectedDays, SelectedSource);
                Stats = stats;

                var hours = stats.TotalWatchTimeSeconds / 3600;
                var mins = (stats.TotalWatchTimeSeconds % 3600) / 60;
                FormattedWatchTime = $"{hours}h {mins}m";
                FormattedAverageWeekly = $"{stats.AverageHoursPerWeek:F1}h / week";
            });

            if (Stats != null)
            {
                TopGenres.Clear();
                foreach (var g in Stats.TopGenres) TopGenres.Add(g);

                TopSeries.Clear();
                foreach (var s in Stats.TopSeries) TopSeries.Add(s);

                TopMovies.Clear();
                foreach (var m in Stats.TopMovies) TopMovies.Add(m);

                HeatmapCells.Clear();
                foreach (var c in Stats.ActivityHeatmap) HeatmapCells.Add(c);

                FrequencyBars.Clear();
                foreach (var b in Stats.FrequencyBars) FrequencyBars.Add(b);
            }

            // If Trakt is connected and user selected Trakt or Combined, fetch Lifetime stats
            if (_trakt.IsConnected && SelectedSource != "local")
            {
                var lifetime = await _trakt.GetUserStatsAsync();
                if (lifetime != null)
                {
                    LifetimeStats = lifetime;
                    HasLifetimeStats = true;
                }
            }
            else
            {
                HasLifetimeStats = false;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("Analytics", "Failed to load watch analytics", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
