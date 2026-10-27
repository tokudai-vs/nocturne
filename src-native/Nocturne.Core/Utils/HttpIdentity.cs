namespace Nocturne.Core.Utils;

// Cloudflare, in front of api.trakt.tv, answers a request with no User-Agent with a 403 HTML page, and HttpClient
// sends none by default: Connect failed at the device-code request (#33). Every third-party API client identifies itself.
public static class HttpIdentity
{
    public static readonly string UserAgent =
        $"Nocturne/{typeof(HttpIdentity).Assembly.GetName().Version?.ToString(3) ?? "4.0.0"} (+https://github.com/tokudai-vs/nocturne)";

    public static HttpClient Identify(this HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0) http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}
