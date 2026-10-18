namespace Nocturne.Core.Models;

public class ServerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConnected { get; set; } = DateTime.UtcNow;
}

public class CombinedLibraryRef
{
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
}

public class CombinedMapping
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "Folder";
    public List<CombinedLibraryRef> Libraries { get; set; } = new();
}

public class LibraryMapping
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "Folder";
    public List<string> LibraryIds { get; set; } = new();
}

public class NocturneSettings
{
    public List<ServerConfig> Servers { get; set; } = new();
    public string? ActiveServerId { get; set; }
    public bool FirstLaunchComplete { get; set; } = false;

    // Visual appearance (Mica / Acrylic / Themes)
    public string Theme { get; set; } = "auto"; // "auto" | "dark" | "light"
    public string BackdropMaterial { get; set; } = "Mica"; // "Mica" | "Acrylic" | "Tabbed" | "None"
    public string AccentColor { get; set; } = "#E5A00D";

    // Playback and window settings
    public bool StartFullscreen { get; set; } = false;
    public string PreferredQuality { get; set; } = "highest"; // "highest" | "lowest"
    public string DefaultSubtitleLanguage { get; set; } = "none";
    public string DefaultAudioLanguage { get; set; } = "eng";
    public bool AutoPlayNextEpisode { get; set; } = true;
    public string SubtitleFont { get; set; } = "Segoe UI Variable Text";
    public int SubtitleSize { get; set; } = 46;
    public string SubtitleColor { get; set; } = "#FFFFFF";
    public string SubtitleBackground { get; set; } = "none"; // "none" | "semi" | "opaque"

    // Skip segments (TheIntroDB)
    public string SkipIntroMode { get; set; } = "button"; // "button" | "auto" | "off"
    public string SkipRecapMode { get; set; } = "auto";   // "auto" | "button" | "off"
    public string SkipCreditsMode { get; set; } = "button"; // "button" | "auto" | "off"

    // Multi-server, Virtual Libraries & Deduplication
    public string LibraryMode { get; set; } = "separate"; // "separate" | "combined"
    public bool ShowUnmappedLibraries { get; set; } = true;
    public bool DedupEnabled { get; set; } = true;
    public List<string> PreferredServerPriority { get; set; } = new();
    public Dictionary<string, CombinedMapping> CombinedMappings { get; set; } = new();
    public Dictionary<string, Dictionary<string, LibraryMapping>> LibraryMappings { get; set; } = new();

    // Cache & Synchronization
    public bool SyncOnStartup { get; set; } = true;
    public int ImageCacheMaxMB { get; set; } = 500;

    // Trakt.tv integration
    public bool TraktAutoScrobble { get; set; } = true;
    public bool TraktSyncWatchedState { get; set; } = true;
    public bool TraktShowWatchlistInSidebar { get; set; } = true;
    public string? TraktUsername { get; set; }
    public string? TraktAccessToken { get; set; }
    public string? TraktRefreshToken { get; set; }
    public DateTime? TraktTokenExpiresAt { get; set; }
    public string TraktClientIdOverride { get; set; } = string.Empty;
    public string TraktClientSecretOverride { get; set; } = string.Empty;
}
