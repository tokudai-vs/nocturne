namespace Nocturne.Core.Services;

// Spec D7 (T5): every user-data change records where it came from (user_data.origin), and is never propagated back to
// the peer named there. The one rule every outbound queue asks (DatabaseService.EnqueueSiblingPlaybackWrite and
// EnqueueTraktEvent): call sites state a change's origin, never whether to write.
public static class Origin
{
    public const string Local = "local";   // playback or an action in Nocturne
    public const string Trakt = "trakt";   // the watched-state pull
    public static string Emby(string serverId) => $"emby:{serverId}"; // read from that server by a sync

    public static bool WritesTo(string origin, string serverId) => origin != Emby(serverId);
    public static bool WritesToTrakt(string origin) => origin != Trakt;
}
