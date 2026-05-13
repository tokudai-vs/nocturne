export interface TraktTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: string; // ISO 8601
}

export interface TraktDeviceCodeResponse {
  device_code: string;
  user_code: string;
  verification_url: string;
  expires_in: number;
  interval: number;
}

export interface TraktTokenResponse {
  access_token: string;
  token_type: string;
  expires_in: number;
  refresh_token: string;
  scope: string;
  created_at: number;
}

export interface TraktUserMe {
  username: string;
  private: boolean;
  name: string;
  vip: boolean;
  vip_ep: boolean;
  ids: { slug: string; uuid: string };
}

export type ScrobbleAction = 'start' | 'pause' | 'stop';
export type QueueAction = ScrobbleAction | 'history-add' | 'history-remove';
export type DeviceFlowState = 'pending' | 'success' | 'denied' | 'expired' | 'slow_down';

export interface TraktMovieIds {
  trakt?: number;
  slug?: string;
  imdb?: string;
  tmdb?: number;
}

export interface TraktShowIds {
  trakt?: number;
  slug?: string;
  imdb?: string;
  tmdb?: number;
  tvdb?: number;
}

export interface ScrobbleMoviePayload {
  movie: { ids: TraktMovieIds };
  progress: number;
  app_version?: string;
}

export interface ScrobbleEpisodePayload {
  show: { ids: TraktShowIds };
  episode: { season: number; number: number };
  progress: number;
  app_version?: string;
}

export type ScrobblePayload = ScrobbleMoviePayload | ScrobbleEpisodePayload;

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

export interface TraktAdvancedConfig {
  clientIdOverride: string;
  clientSecretOverride: string;
  bundledIdPresent: boolean;
}

// ── Phase 2: watched-state sync ─────────────────────

export interface TraktWatchedMovie {
  plays: number;
  last_watched_at: string;
  last_updated_at: string;
  movie: {
    title: string;
    year: number | null;
    ids: TraktMovieIds;
  };
}

export interface TraktWatchedShowEpisode {
  number: number;
  plays: number;
  last_watched_at: string;
}

export interface TraktWatchedShowSeason {
  number: number;
  episodes: TraktWatchedShowEpisode[];
}

export interface TraktWatchedShow {
  plays: number;
  last_watched_at: string;
  last_updated_at: string;
  show: {
    title: string;
    year: number | null;
    ids: TraktShowIds;
  };
  seasons: TraktWatchedShowSeason[];
}

export interface TraktHistoryItem {
  id: number;
  watched_at: string;
  action: 'scrobble' | 'checkin' | 'watch';
  type: 'movie' | 'episode';
  movie?: { ids: TraktMovieIds; title: string; year: number | null };
  episode?: { season: number; number: number; ids?: { trakt?: number; tmdb?: number; imdb?: string } };
  show?: { ids: TraktShowIds; title: string; year: number | null };
}

/** Preview payload returned to renderer for the post-connect modal. */
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

export interface TraktMatchedMovie {
  tmdbId: string | null;
  imdbId: string | null;
  title: string;
  year: number | null;
  watchedAt: string;
  embyIds: string[];   // candidate emby_ids in local cache (across servers)
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

// ── Phase 3: watchlist ──────────────────────────────

export interface TraktWatchlistMovie {
  rank: number;
  listed_at: string;
  type: 'movie';
  movie: {
    title: string;
    year: number | null;
    overview?: string;
    ids: TraktMovieIds;
  };
}

export interface TraktWatchlistShow {
  rank: number;
  listed_at: string;
  type: 'show';
  show: {
    title: string;
    year: number | null;
    overview?: string;
    ids: TraktShowIds;
  };
}

// ── Phase 4: ratings ────────────────────────────────

export interface TraktRatingResult {
  rating: number | null;
  votes: number | null;
}

export interface TraktItemDetails {
  rating?: number;
  votes?: number;
  ids: TraktMovieIds | TraktShowIds;
}

// ── Analytics (lifetime stats + paginated history) ──

export interface TraktUserStats {
  movies: { plays: number; watched: number; minutes: number; collected: number; ratings: number; comments: number };
  shows: { watched: number; collected: number; ratings: number; comments: number };
  seasons?: { ratings: number; comments: number };
  episodes: { plays: number; watched: number; minutes: number; ratings: number; comments: number };
  network?: { friends: number; followers: number; following: number };
  ratings?: { total: number; distribution: Record<string, number> };
}

export interface TraktHistoryPage {
  items: TraktHistoryItem[];
  page: number;
  pageCount: number;
  itemCount: number;
}

export interface TraktHistoryQuery {
  type?: 'movies' | 'episodes';
  startAt?: string; // ISO 8601
  endAt?: string;   // ISO 8601
  page?: number;
  limit?: number;
}
