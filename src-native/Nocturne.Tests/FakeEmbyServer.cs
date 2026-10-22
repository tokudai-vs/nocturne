using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using Nocturne.Core.Models;

namespace Nocturne.Tests;

// One "TV Shows" library per host, listed in the order given (as Emby's SortName paging returned the live library).
// Honours IncludeItemTypes, StartIndex/Limit, MinDateLastSavedForUser (against Saved; unset means long ago) and Ids
// (unpaged), and records how many items each request returned. Unlisted items are found by Ids but not by paging (a
// listing shifted by a concurrent change); a request matching Fail gets a 500.
internal sealed class FakeEmbyServer : HttpMessageHandler
{
    public readonly Dictionary<string, List<EmbyItem>> Libraries = new();
    public readonly Dictionary<string, DateTime> Saved = new();
    public readonly List<(string Host, string Query, int Returned)> Requests = new();
    public readonly HashSet<string> Unlisted = new();
    public Func<Uri, bool>? Fail;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (Fail?.Invoke(uri) == true) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var items = Libraries[uri.Host];
        if (uri.AbsolutePath.EndsWith("/Views"))
            return Json(new { Items = new[] { new { Id = "lib", Name = "TV Shows", CollectionType = "tvshows", Type = "CollectionFolder" } }, TotalRecordCount = 1 });
        if (uri.AbsolutePath.EndsWith("/Items"))
        {
            var q = HttpUtility.ParseQueryString(uri.Query);
            IEnumerable<EmbyItem> listed = items;
            if (q["IncludeItemTypes"] is { } types) listed = listed.Where(i => types.Split(',').Contains(i.Type));
            if (q["MinDateLastSavedForUser"] is { } since)
            {
                var from = DateTime.Parse(since, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
                listed = listed.Where(i => Saved.GetValueOrDefault(i.Id, DateTime.UnixEpoch) >= from);
            }
            if (q["Ids"] is { } ids) listed = listed.Where(i => ids.Split(',').Contains(i.Id));
            else listed = listed.Where(i => !Unlisted.Contains(i.Id));
            var all = listed.ToList();
            var page = q["Ids"] != null ? all : all.Skip(int.Parse(q["StartIndex"] ?? "0")).Take(int.Parse(q["Limit"] ?? "50")).ToList();
            lock (Requests) Requests.Add((uri.Host, uri.Query, page.Count));
            return Json(new { Items = page, TotalRecordCount = all.Count });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(object body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") });
}
