using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class IntroSegment
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "intro", "recap", "outro"

    [JsonPropertyName("start_sec")]
    public double StartSec { get; set; }

    [JsonPropertyName("end_sec")]
    public double EndSec { get; set; }

    [JsonIgnore]
    public bool HasTriggered { get; set; }
}

public class IntroSegmentsResponse
{
    [JsonPropertyName("segments")]
    public List<IntroSegment> Segments { get; set; } = new();
}

public class SkipPromptEvent
{
    public IntroSegment Segment { get; set; } = null!;
    public string ActionLabel { get; set; } = "Skip Intro";
    public bool IsNextEpisode { get; set; }
    public double TargetSeekSec { get; set; }
}

public class IntroDbService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, List<IntroSegment>> _cache = new();
    private List<IntroSegment> _currentSegments = new();
    private double _lastPosition = 0;

    public event Action<SkipPromptEvent?>? PromptChanged;

    public IntroDbService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.introdb.app/"),
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public async Task LoadSegmentsForEpisodeAsync(string imdbId, int seasonNumber, int episodeNumber)
    {
        _currentSegments.Clear();
        PromptChanged?.Invoke(null);
        if (string.IsNullOrWhiteSpace(imdbId)) return;

        var cacheKey = $"{imdbId}_s{seasonNumber}e{episodeNumber}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _currentSegments = cached.Select(s => new IntroSegment
            {
                Type = s.Type,
                StartSec = s.StartSec,
                EndSec = s.EndSec,
                HasTriggered = false
            }).ToList();
            return;
        }

        try
        {
            var url = $"segments?imdb_id={imdbId}&type=show&season={seasonNumber}&episode={episodeNumber}";
            var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var data = await resp.Content.ReadFromJsonAsync<IntroSegmentsResponse>();
                if (data?.Segments != null && data.Segments.Count > 0)
                {
                    _cache[cacheKey] = data.Segments;
                    _currentSegments = data.Segments.Select(s => new IntroSegment
                    {
                        Type = s.Type,
                        StartSec = s.StartSec,
                        EndSec = s.EndSec,
                        HasTriggered = false
                    }).ToList();
                    NocturneLogger.Info("TheIntroDB", $"Loaded {_currentSegments.Count} skip segments for {imdbId} S{seasonNumber}E{episodeNumber}");
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("TheIntroDB", $"Failed to fetch segments: {ex.Message}");
        }
    }

    public void OnPositionChanged(double currentSec, MpvController mpv)
    {
        if (_currentSegments.Count == 0) return;

        // Check for backward seek to re-arm segments
        if (currentSec < _lastPosition - 2.0)
        {
            foreach (var seg in _currentSegments)
            {
                if (currentSec < seg.StartSec)
                {
                    seg.HasTriggered = false;
                }
            }
        }
        _lastPosition = currentSec;

        // Find active segment
        var activeSegment = _currentSegments.FirstOrDefault(s => currentSec >= s.StartSec && currentSec < s.EndSec);

        if (activeSegment != null)
        {
            var mode = GetModeForSegment(activeSegment.Type);
            if (mode == "off") return;

            if (mode == "auto" && !activeSegment.HasTriggered)
            {
                activeSegment.HasTriggered = true;
                _ = mpv.SeekTo(activeSegment.EndSec);
                NocturneLogger.Info("TheIntroDB", $"Auto-skipped {activeSegment.Type} to {activeSegment.EndSec:F1}s");
                PromptChanged?.Invoke(null);
                return;
            }

            if (mode == "button" && !activeSegment.HasTriggered)
            {
                var isOutro = activeSegment.Type == "outro";
                var label = activeSegment.Type switch
                {
                    "recap" => "Skip Recap",
                    "outro" => "Next Episode",
                    _ => "Skip Intro"
                };

                PromptChanged?.Invoke(new SkipPromptEvent
                {
                    Segment = activeSegment,
                    ActionLabel = $"{label} (Enter)",
                    IsNextEpisode = isOutro,
                    TargetSeekSec = activeSegment.EndSec
                });
            }
        }
        else
        {
            PromptChanged?.Invoke(null);
        }
    }

    private string GetModeForSegment(string type)
    {
        return type switch
        {
            "intro" => _settings.Current.SkipIntroMode,
            "recap" => _settings.Current.SkipRecapMode,
            "outro" => _settings.Current.SkipCreditsMode,
            _ => "button"
        };
    }

    public void Clear()
    {
        _currentSegments.Clear();
        PromptChanged?.Invoke(null);
    }
}
