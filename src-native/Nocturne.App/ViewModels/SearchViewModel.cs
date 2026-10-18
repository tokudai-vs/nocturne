using System.Collections.ObjectModel;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class SearchViewModel : BaseViewModel
{
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private string _query = string.Empty;
    private bool _isSearching;

    public ObservableCollection<EmbyItem> Results { get; } = new();

    public event Action<EmbyItem>? NavigateToDetail;

    public SearchViewModel(EmbyClient embyClient, DatabaseService db)
    {
        _embyClient = embyClient;
        _db = db;
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                _ = SearchAsync(value);
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    public async Task SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            Results.Clear();
            return;
        }

        IsSearching = true;
        try
        {
            // 1. Instant local search from SQLite cache
            var localMatches = _db.GetCachedItems(limit: 50)
                .Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            (i.SeriesName != null && i.SeriesName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Results.Clear();
            foreach (var item in localMatches) Results.Add(item);

            // 2. Query Emby server for full catalog results
            if (_embyClient.IsAuthenticated)
            {
                var serverItems = await _embyClient.SearchAsync(term, 30);
                foreach (var sItem in serverItems)
                {
                    if (!Results.Any(r => r.Id == sItem.Id))
                    {
                        Results.Add(sItem);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("SearchViewModel", $"Search failed for '{term}'", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void OpenItemDetail(EmbyItem item)
    {
        NavigateToDetail?.Invoke(item);
    }
}
