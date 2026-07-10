import type { EncoderResult, WatchPartySource } from '../../shared/watchparty-types';

// Mirror of the main-process session manager's public state. Kept in
// renderer/api/types.ts so it's reachable from both pre-flight modal +
// host page without a separate shared module.
export type WatchPartySessionState = 'IDLE' | 'INITIALIZING' | 'WAITING' | 'LIVE' | 'ENDED';

export interface WatchPartyPublicState {
  state: WatchPartySessionState;
  sessionId: string | null;
  title: string | null;
  tunnelUrl: string | null;
  localUrl: string | null;
  durationSec: number | null;
  transcodedSeconds: number;
  canStart: boolean;
  guestCount: number;
  maxGuests: number | 'unlimited' | null;
  errorMessage: string | null;
  startedAt: number | null;
  /** Movie-time offset of the transcode's t=0. Resume = positive; else 0. */
  startOffsetSec: number;
  /** Whether progress is being reported to Emby + Trakt for this session. */
  trackHistory: boolean;
  /** Resume transcode shows no progress — server likely refuses HTTP range. */
  slowSeekWarning: boolean;
}

// Server
export interface EmbyServerInfo {
  ServerName: string;
  Version: string;
  Id: string;
}

// Auth
export interface AuthResult {
  AccessToken: string;
  User: EmbyUser;
  ServerId: string;
}

export interface EmbyUser {
  Id: string;
  Name: string;
  PrimaryImageTag?: string;
  HasPassword: boolean;
}

export interface PublicUser {
  Name: string;
  Id: string;
  HasPassword: boolean;
  PrimaryImageTag?: string;
}

// Library
export interface BaseItemDto {
  Id: string;
  Name: string;
  serverId?: string;
  Type:
    | 'Movie'
    | 'Series'
    | 'Season'
    | 'Episode'
    | 'BoxSet'
    | 'MusicAlbum'
    | 'Audio'
    | 'Folder'
    | 'CollectionFolder';
  Overview?: string;
  RunTimeTicks?: number;
  CommunityRating?: number;
  OfficialRating?: string;
  ProductionYear?: number;
  PremiereDate?: string;
  Genres?: string[];
  Studios?: { Name: string }[];
  People?: {
    Id: string;
    Name: string;
    Role?: string;
    Type: string;
    PrimaryImageTag?: string;
  }[];
  ImageTags?: Record<string, string>;
  BackdropImageTags?: string[];
  // Dedup-sibling URLs cycled when the primary fails to load. Attached
  // server-side via attachFallbacksToItems(); passed through cache-adapter.
  ImageFallbacks?: string[];
  BackdropFallbacks?: string[];
  UserData?: UserItemData;
  MediaSources?: MediaSource[];
  RemoteTrailers?: { Url: string; Name: string }[];
  SeriesId?: string;
  SeriesName?: string;
  SeasonId?: string;
  SeasonName?: string;
  IndexNumber?: number;
  ParentIndexNumber?: number;
  ParentThumbItemId?: string;
  ParentThumbImageTag?: string;
  ParentBackdropItemId?: string;
  ParentBackdropImageTags?: string[];
  CriticRating?: number;
  ChildCount?: number;
  RecursiveItemCount?: number;
  Status?: string;
}

export interface UserItemData {
  PlaybackPositionTicks: number;
  PlayCount: number;
  IsFavorite: boolean;
  Played: boolean;
  PlayedPercentage?: number;
}

export interface MediaSource {
  Id: string;
  Name: string;
  Path: string;
  Container: string;
  Size: number;
  Bitrate: number;
  MediaStreams: MediaStream[];
  SupportsDirectPlay: boolean;
  SupportsDirectStream: boolean;
}

export interface MediaStream {
  Type: 'Video' | 'Audio' | 'Subtitle';
  Index: number;
  Codec: string;
  DisplayTitle: string;
  Language?: string;
  IsDefault: boolean;
  IsExternal?: boolean;
}

export interface ItemsResult {
  Items: BaseItemDto[];
  TotalRecordCount: number;
}

export type ImageType = 'Primary' | 'Backdrop' | 'Logo' | 'Thumb' | 'Banner';

// Sync types
export interface SyncProgress {
  phase: string;
  current: number;
  total: number;
  detail: string;
  librariesDone?: number;
  librariesTotal?: number;
  percent: number;
}

export interface SyncStatus {
  running: boolean;
  phase: string | null;
  progress: SyncProgress | null;
  lastFullSync: string | null;
  hasCachedData: boolean;
  syncStatus: 'never' | 'in-progress' | 'partial' | 'complete';
  dedupStatus: 'never' | 'in-progress' | 'complete' | 'failed';
  lastDedupBuild: string | null;
  dedupRunning: boolean;
}

// Cache types
export interface CachedItem {
  emby_id: string;
  server_id: string;
  library_id: string;
  library_name: string | null;
  type: string;
  name: string;
  sort_name: string | null;
  overview: string | null;
  tmdb_id: string | null;
  imdb_id: string | null;
  tvdb_id: string | null;
  production_year: number | null;
  premiere_date: string | null;
  community_rating: number | null;
  official_rating: string | null;
  runtime_ticks: number | null;
  genres: string | null;
  studios: string | null;
  image_tags: string | null;
  backdrop_tags: string | null;
  series_id: string | null;
  series_name: string | null;
  season_id: string | null;
  season_number: number | null;
  episode_number: number | null;
  media_sources: string | null;
  played: number;
  play_count: number;
  is_favorite: number;
  playback_position_ticks: number;
  played_percentage: number;
  date_created: string | null;
  date_modified: string | null;
  cached_at: string | null;
  dedup_group_id: string | null;
  version_count?: number;
  // Dedup-sibling fallback URLs attached at IPC return time; cycled by
  // MediaCard / HeroBackdrop when the primary image fails to load.
  image_fallbacks?: string[];
  backdrop_fallbacks?: string[];
  // Phase 3: Trakt watchlist sentinel rows.
  is_external?: boolean;
  trakt_key?: string;
  trakt_type?: 'movie' | 'show';
}

export interface CacheFilters {
  type?: string;
  libraryId?: string;
  limit?: number;
  offset?: number;
  sortBy?: string;
  sortOrder?: 'ASC' | 'DESC';
  search?: string;
  isFavorite?: boolean;
  isPlayed?: boolean;
}

export interface DbStats {
  totalItems: number;
  itemsByLibrary: { library_id: string; library_name: string | null; count: number }[];
  lastSyncTime: string | null;
}

// Dedup types
export interface DedupStats {
  groupCount: number;
  mergedItems: number;
}

export interface EpisodeVersionGroup {
  season_number: number;
  episode_number: number;
  items: CachedItem[];
}

// Settings types
export interface LibraryMapping {
  name: string;
  icon: string;
  libraryIds: string[];
}

export interface ServerConfig {
  id: string;
  name: string;
  url: string;
  userId: string;
  username: string;
  accessToken: string;
  version: string;
  addedAt: string;
  lastConnected: string;
}

export interface VirtualLibrary {
  id: string;
  name: string;
  icon: string;
  libraryIds: string[];
  isVirtual: boolean;
  totalItems: number;
}

export interface CombinedLibraryRef {
  serverId: string;
  serverName: string;
  libraryId: string;
  libraryName: string;
}

export interface CombinedMapping {
  name: string;
  icon: string;
  libraries: CombinedLibraryRef[];
}

export interface NocturneSettings {
  servers: ServerConfig[];
  activeServerId: string | null;
  libraryMappings: Record<string, Record<string, LibraryMapping>>;
  libraryMode: 'separate' | 'combined';
  combinedMappings: Record<string, CombinedMapping>;
  combinedMappingsInitialized: boolean;
  showUnmappedLibraries: boolean;
  preferredQuality: 'highest' | 'lowest';
  defaultSubtitleLanguage: string;
  defaultAudioLanguage: string;
  autoPlayNextEpisode: boolean;
  subtitleFont: string;
  subtitleSize: number;
  subtitleColor: string;
  subtitleBorderSize: number;
  subtitleBackground: 'none' | 'semi' | 'opaque';
  subtitlePosition: number;
  autoDownloadSubtitles: boolean;
  preferredSubtitleLanguage: string;
  powerMode: 'performance' | 'balanced' | 'efficiency';
  startFullscreen: boolean;
  startPage: 'home' | 'last-visited';
  imageCacheMaxMB: number;
  syncOnStartup: boolean;
  firstLaunchComplete: boolean;
  lastServerUrl: string;
  skipIntroMode: 'button' | 'auto' | 'off';
  skipRecapMode: 'button' | 'auto' | 'off';
  skipCreditsMode: 'button' | 'auto' | 'off';
  traktAutoScrobble: boolean;
  traktSyncWatchedState: boolean;
  traktShowWatchlistInSidebar: boolean;
  traktUsername: string | null;
  traktUserSlug: string | null;
  traktConnectedAt: string | null;
  traktLastSyncAt: string | null;
  traktLastWatchlistSyncAt: string | null;
  traktClientIdOverride: string;
  traktClientSecretOverride: string;
  traktHistoryBackfillCap: 'two-years' | 'full';
  watchPartyMaxGuestsUnlocked: boolean;
  watchPartyPrefer4kSource: boolean;
  watchPartyAllow4kOutput: boolean;
  watchPartyAllowCpuEncoder: boolean;
}

// Updater types
export interface UpdateInfo {
  version: string;
  releaseNotes?: string;
}

export interface UpdateStatus {
  state: 'idle' | 'checking' | 'available' | 'downloading' | 'downloaded' | 'error';
  info: UpdateInfo | null;
  progress: number;
  error: string | null;
}

// IPC response wrapper
export interface IpcResponse<T = unknown> {
  success: boolean;
  data?: T;
  error?: string;
}

// ── Trakt ──
export interface TraktStatus {
  connected: boolean;
  configured: boolean;
  encryptionAvailable: boolean;
  username: string | null;
  slug: string | null;
  expiresAt: string | null;
  connectedAt: string | null;
  queueCount: number;
}

export interface TraktDeviceCode {
  device_code: string;
  user_code: string;
  verification_url: string;
  expires_in: number;
  interval: number;
}

export type TraktDeviceFlowState =
  | 'pending'
  | 'success'
  | 'denied'
  | 'expired'
  | 'slow_down';

export interface TraktAdvancedConfig {
  clientIdOverride: string;
  clientSecretOverride: string;
  bundledIdPresent: boolean;
}

export interface TraktScrobbleError {
  action: string;
  itemId: string;
  message: string;
}

export interface TraktMatchedMovie {
  tmdbId: string | null;
  imdbId: string | null;
  title: string;
  year: number | null;
  watchedAt: string;
  embyIds: string[];
  alreadyPlayed: boolean;
}

export interface TraktMatchedEpisode {
  showTmdbId: string;
  showTitle: string;
  season: number;
  episode: number;
  watchedAt: string;
  embyIds: string[];
  alreadyPlayed: boolean;
}

export interface TraktHistoryPreview {
  movies: {
    totalOnTrakt: number;
    matchedInLibrary: number;
    items: TraktMatchedMovie[];
  };
  episodes: {
    totalOnTrakt: number;
    matchedInLibrary: number;
    items: TraktMatchedEpisode[];
  };
}

export interface TraktSyncStats {
  watched: { movies: number; episodes: number };
  watchlist: number;
  lastHistorySync: string | null;
  lastWatchlistSync: string | null;
}

export interface TraktRating {
  rating: number | null;
  votes: number | null;
}

// ── Analytics ──
export type AnalyticsSource = 'local' | 'trakt' | 'combined';

export interface AnalyticsRangeRequest {
  rangeStart: string;
  rangeEnd: string;
  source?: AnalyticsSource;
}

export interface AnalyticsLifetimeBlock {
  movies: number;
  episodes: number;
  watchTimeMinutes: number;
  distinctShows: number;
}

export interface AnalyticsStats {
  source: AnalyticsSource;
  range: { rangeStart: string; rangeEnd: string };
  totalWatched: { movies: number; episodes: number };
  totalWatchTimeSeconds: number;
  inProgressSeriesCount: number;
  avgPerWeekSeconds: number;
  activityByDay: Array<{ date: string; count: number; watchTimeSeconds: number }>;
  topSeries: Array<{ id: string; name: string; imageUrl: string | null; episodeCount: number }>;
  topMovies: Array<{ id: string; name: string; imageUrl: string | null; lastPlayed: string | null }>;
  genreBreakdown: Array<{ genre: string; watchTimeSeconds: number; pct: number }>;
  lifetime: AnalyticsLifetimeBlock | null;
  unmatchedTraktCount?: number;
}

// Window API type declaration
declare global {
  interface Window {
    api: {
      auth: {
        connectToServer: (url: string) => Promise<IpcResponse<EmbyServerInfo>>;
        login: (username: string, password: string) => Promise<IpcResponse<AuthResult>>;
        logout: () => Promise<IpcResponse<void>>;
        getPublicUsers: () => Promise<IpcResponse<PublicUser[]>>;
        restore: (serverUrl: string, token: string, userId: string) => Promise<IpcResponse<EmbyUser>>;
        connectToServerStandalone: (url: string) => Promise<IpcResponse<EmbyServerInfo>>;
        getPublicUsersForServer: (url: string) => Promise<IpcResponse<PublicUser[]>>;
        loginToServer: (url: string, username: string, password: string) => Promise<IpcResponse<AuthResult>>;
        checkServer: (url: string) => Promise<IpcResponse<boolean>>;
      };
      library: {
        getViews: () => Promise<IpcResponse<ItemsResult>>;
        getItems: (parentId: string, params?: Record<string, unknown>) => Promise<IpcResponse<ItemsResult>>;
        getItem: (itemId: string, serverId?: string) => Promise<IpcResponse<BaseItemDto>>;
        getLatest: (parentId: string, limit?: number) => Promise<IpcResponse<BaseItemDto[]>>;
        getResume: () => Promise<IpcResponse<ItemsResult>>;
        getNextUp: () => Promise<IpcResponse<ItemsResult>>;
        getSimilar: (itemId: string, serverId?: string) => Promise<IpcResponse<ItemsResult>>;
        getSeasons: (seriesId: string, serverId?: string) => Promise<IpcResponse<ItemsResult>>;
        getEpisodes: (seriesId: string, seasonId: string, serverId?: string) => Promise<IpcResponse<ItemsResult>>;
        getAllServersViews: () => Promise<IpcResponse<{
          views: Array<{ Id: string; Name: string; Type: string; serverId: string; serverName: string }>;
          errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }>;
        }>>;
        getAllServersLatest: (limit?: number) => Promise<IpcResponse<{
          libraries: Array<{
            libraryId: string;
            libraryName: string;
            serverId: string;
            serverName: string;
            items: BaseItemDto[];
          }>;
          errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }>;
        }>>;
        getAllServersResume: () => Promise<IpcResponse<{
          items: BaseItemDto[];
          errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }>;
        }>>;
      };
      media: {
        getPlaybackInfo: (itemId: string, serverId?: string) => Promise<IpcResponse<{ MediaSources: MediaSource[] }>>;
        getStreamUrl: (itemId: string, mediaSourceId: string) => Promise<IpcResponse<string>>;
        reportStart: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
        reportProgress: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
        reportStop: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
      };
      user: {
        getCurrentUser: () => Promise<IpcResponse<EmbyUser>>;
        markPlayed: (itemId: string) => Promise<IpcResponse<void>>;
        markUnplayed: (itemId: string) => Promise<IpcResponse<void>>;
        updateFavorite: (itemId: string, isFavorite: boolean) => Promise<IpcResponse<void>>;
      };
      item: {
        markPlayed: (args: { itemId: string; serverId?: string }) => Promise<IpcResponse<void>>;
        markUnplayed: (args: { itemId: string; serverId?: string }) => Promise<IpcResponse<void>>;
        toggleFavorite: (args: { itemId: string; serverId?: string; isFavorite: boolean }) => Promise<IpcResponse<void>>;
        removeFromContinue: (args: { itemId: string; serverId?: string }) => Promise<IpcResponse<void>>;
      };
      search: {
        query: (term: string, filters?: Record<string, unknown>) => Promise<IpcResponse<ItemsResult>>;
      };
      image: {
        getUrl: (itemId: string, imageType: string, params?: Record<string, unknown>) => Promise<IpcResponse<string>>;
      };
      player: {
        play: (args: { itemId: string; mediaSourceId: string; startPositionTicks?: number; itemName?: string; serverId?: string }) => Promise<IpcResponse<void>>;
        stop: () => Promise<IpcResponse<void>>;
        onExited: (cb: () => void) => () => void;
        onStarting: (cb: () => void) => () => void;
        onStartFailed: (cb: () => void) => () => void;
        onMpvUnavailable: (cb: () => void) => () => void;
      };
      sync: {
        startFull: () => Promise<IpcResponse<void>>;
        startIncremental: () => Promise<IpcResponse<void>>;
        autoStart: () => Promise<IpcResponse<void>>;
        cancel: () => Promise<IpcResponse<void>>;
        getStatus: () => Promise<IpcResponse<SyncStatus>>;
        onProgress: (cb: (data: SyncProgress) => void) => () => void;
        onComplete: (cb: () => void) => () => void;
        onError: (cb: (err: { message: string }) => void) => () => void;
      };
      cache: {
        getItem: (itemId: string) => Promise<IpcResponse<CachedItem>>;
        getLibraryItems: (filters: CacheFilters) => Promise<IpcResponse<{ items: CachedItem[]; total: number }>>;
        getResumeItems: () => Promise<IpcResponse<CachedItem[]>>;
        getLatestItems: (libraryId: string, limit?: number) => Promise<IpcResponse<CachedItem[]>>;
        search: (query: string) => Promise<IpcResponse<CachedItem[]>>;
        resolveDedupGroups: (ids: string[]) => Promise<IpcResponse<Record<string, string>>>;
        getStats: () => Promise<IpcResponse<DbStats>>;
        clear: () => Promise<IpcResponse<void>>;
        hasData: () => Promise<IpcResponse<boolean>>;
      };
      imageCache: {
        getCachedUrl: (url: string) => Promise<IpcResponse<string>>;
        precache: (urls: string[]) => Promise<IpcResponse<void>>;
      };
      settings: {
        get: () => Promise<IpcResponse<NocturneSettings>>;
        getValue: (key: string) => Promise<IpcResponse<unknown>>;
        set: (key: string, value: unknown) => Promise<IpcResponse<void>>;
        setMultiple: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
        reset: () => Promise<IpcResponse<void>>;
      };
      libraries: {
        suggestMapping: () => Promise<IpcResponse<Record<string, LibraryMapping>>>;
      };
      vlib: {
        getAll: () => Promise<IpcResponse<VirtualLibrary[]>>;
        getItems: (vlibId: string, opts?: Record<string, unknown>) => Promise<IpcResponse<{ items: CachedItem[]; total: number }>>;
        getLatest: (vlibId: string, limit?: number) => Promise<IpcResponse<CachedItem[]>>;
        getHeroes: (vlibId?: string, limit?: number) => Promise<IpcResponse<CachedItem[]>>;
      };
      dedup: {
        getVersions: (itemId: string) => Promise<IpcResponse<CachedItem[]>>;
        getEpisodes: (seriesItemId: string, seasonNumber: number) => Promise<IpcResponse<EpisodeVersionGroup[]>>;
        getAdjacentEpisodes: (episodeId: string) => Promise<IpcResponse<{ prev: CachedItem | null; next: CachedItem | null }>>;
        getStats: () => Promise<IpcResponse<DedupStats>>;
        rebuild: () => Promise<IpcResponse<{ success: boolean; groupsCreated?: number; itemsMerged?: number; error?: string }>>;
        onComplete: (cb: (data: { groupsCreated: number; itemsMerged: number }) => void) => () => void;
        onError: (cb: (err: { message: string }) => void) => () => void;
      };
      servers: {
        getAll: () => Promise<IpcResponse<ServerConfig[]>>;
        getActive: () => Promise<IpcResponse<ServerConfig | null>>;
        add: (config: Record<string, unknown>) => Promise<IpcResponse<ServerConfig>>;
        remove: (serverId: string) => Promise<IpcResponse<void>>;
        switch: (serverId: string) => Promise<IpcResponse<boolean>>;
        getMappings: () => Promise<IpcResponse<Record<string, LibraryMapping>>>;
        setMappings: (mappings: Record<string, unknown>) => Promise<IpcResponse<void>>;
        getLibraryMode: () => Promise<IpcResponse<'separate' | 'combined'>>;
        getCombinedMappings: () => Promise<IpcResponse<Record<string, CombinedMapping>>>;
        setCombinedMappings: (mappings: Record<string, unknown>) => Promise<IpcResponse<void>>;
        getAllLibraries: () => Promise<IpcResponse<CombinedLibraryRef[]>>;
      };
      updater: {
        check: () => Promise<IpcResponse<void>>;
        download: () => Promise<IpcResponse<void>>;
        install: () => Promise<IpcResponse<void>>;
        getStatus: () => Promise<IpcResponse<UpdateStatus>>;
        onStatus: (cb: (status: UpdateStatus) => void) => () => void;
      };
      session: {
        onExpired: (cb: () => void) => () => void;
      };
      trakt: {
        getStatus: () => Promise<IpcResponse<TraktStatus>>;
        authStart: () => Promise<IpcResponse<TraktDeviceCode>>;
        authPoll: (deviceCode: string) => Promise<IpcResponse<TraktDeviceFlowState>>;
        disconnect: () => Promise<IpcResponse<void>>;
        drainQueue: () => Promise<IpcResponse<{ remaining: number }>>;
        getQueueCount: () => Promise<IpcResponse<number>>;
        getFailedQueueCount: () => Promise<IpcResponse<number>>;
        clearFailedQueue: () => Promise<IpcResponse<{ cleared: number }>>;
        getAdvancedConfig: () => Promise<IpcResponse<TraktAdvancedConfig>>;
        setAdvancedConfig: (cfg: { clientId: string; clientSecret: string }) => Promise<IpcResponse<void>>;
        openVerification: (url: string) => Promise<IpcResponse<void>>;
        onAuthSuccess: (cb: () => void) => () => void;
        onDisconnected: (cb: () => void) => () => void;
        onScrobbleError: (cb: (err: TraktScrobbleError) => void) => () => void;
        onTokenRefreshFailed: (cb: () => void) => () => void;
        fetchPreview: () => Promise<IpcResponse<TraktHistoryPreview>>;
        applyWatchedState: (embyIds: string[]) => Promise<IpcResponse<{ applied: number; failed: number; cancelled: boolean }>>;
        cancelApply: () => Promise<IpcResponse<void>>;
        onApplyProgress: (cb: (data: { current: number; total: number }) => void) => () => void;
        syncNow: () => Promise<IpcResponse<{
          history: { newlyWatched: number; failed: number };
          watchlist: { count: number };
        }>>;
        getStats: () => Promise<IpcResponse<TraktSyncStats>>;
        getWatchlist: () => Promise<IpcResponse<CachedItem[]>>;
        refreshWatchlist: () => Promise<IpcResponse<{ count: number }>>;
        addToWatchlist: (itemId: string) => Promise<IpcResponse<{ ok: boolean; error?: string }>>;
        removeFromWatchlist: (
          args: { itemId?: string; traktType?: 'movie' | 'show'; tmdbId?: string; key?: string },
        ) => Promise<IpcResponse<{ ok: boolean; error?: string }>>;
        inWatchlist: (itemId: string) => Promise<IpcResponse<boolean>>;
        getRating: (tmdbId: string, type: 'movie' | 'show') => Promise<IpcResponse<TraktRating | null>>;
        checkWatched: (
          args: { tmdbId: string; type: 'movie' | 'episode'; season?: number; episode?: number },
        ) => Promise<IpcResponse<boolean>>;
        onSyncComplete: (cb: (data: { newlyWatched: number; failed: number }) => void) => () => void;
        onWatchlistUpdated: (cb: (data: { count: number }) => void) => () => void;
      };
      analytics: {
        getStats: (args: AnalyticsRangeRequest) => Promise<IpcResponse<AnalyticsStats>>;
        getBackfillStatus: () => Promise<IpcResponse<{ backfilled: boolean; cap: string; eventCount: number }>>;
        triggerBackfill: () => Promise<IpcResponse<{ inserted: number; total: number }>>;
        onBackfillProgress: (cb: (data: { current: number; total: number }) => void) => () => void;
        onBackfillComplete: (cb: (data: { inserted: number; total: number }) => void) => () => void;
        onBackfillFailed: (cb: (err: { message: string }) => void) => () => void;
      };
      watchparty: {
        binariesReady: () => Promise<IpcResponse<boolean>>;
        setupBinaries: () => Promise<IpcResponse<{ ffmpegPath: string; cloudflaredPath: string }>>;
        probeEncoder: () => Promise<IpcResponse<EncoderResult>>;
        startSession: (payload: {
          source: WatchPartySource;
          durationSec?: number;
          maxGuests?: number | 'unlimited';
          qualityHeight?: 720 | 1080 | 2160;
          startOffsetSec?: number;
          trackHistory?: boolean;
        }) => Promise<IpcResponse<WatchPartyPublicState>>;
        startShow: () => Promise<IpcResponse<WatchPartyPublicState>>;
        endSession: () => Promise<IpcResponse<void>>;
        getState: () => Promise<IpcResponse<WatchPartyPublicState>>;
        hostEvent: (payload: { type: 'play' | 'pause' | 'seek' | 'time-update'; position: number }) => Promise<IpcResponse<void>>;
        onState: (cb: (state: WatchPartyPublicState) => void) => () => void;
        onSetupProgress: (
          cb: (data: { phase: 'ffmpeg' | 'cloudflared' | 'unzip'; percent: number }) => void,
        ) => () => void;
        onSetupError: (cb: (err: { phase: string; message: string }) => void) => () => void;
      };
      app: {
        onVisibilityChange: (cb: (data: { visible: boolean }) => void) => () => void;
        onFocusChange: (cb: (data: { focused: boolean }) => void) => () => void;
        resetFull: () => Promise<IpcResponse<void>>;
      };
      window: {
        minimize: () => Promise<void>;
        maximize: () => Promise<void>;
        close: () => Promise<void>;
        isMaximized: () => Promise<boolean>;
        toggleFullscreen: () => Promise<void>;
        isFullscreen: () => Promise<boolean>;
        openExternal: (url: string) => Promise<void>;
        onMaximizeChange: (cb: (maximized: boolean) => void) => () => void;
        onFullscreenChange: (cb: (fullscreen: boolean) => void) => () => void;
      };
    };
  }
}

export {};
