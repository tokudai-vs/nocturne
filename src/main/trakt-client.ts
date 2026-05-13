import axios, { AxiosError, AxiosInstance } from 'axios';
import { EventEmitter } from 'events';
import { APP_VERSION } from '../shared/constants';
import {
  TRAKT_API_BASE,
  TRAKT_API_VERSION,
  TRAKT_BUNDLED_CLIENT_ID,
  TRAKT_BUNDLED_CLIENT_SECRET,
  TRAKT_OOB_REDIRECT,
} from '../shared/trakt-config';
import { getSettingValue, setSetting } from './settings';
import {
  loadTokens,
  saveTokens,
  clearTokens,
  hasTokens,
  isEncryptionAvailable,
} from './trakt-store';
import type {
  DeviceFlowState,
  ScrobbleAction,
  ScrobblePayload,
  TraktDeviceCodeResponse,
  TraktHistoryItem,
  TraktHistoryPage,
  TraktHistoryQuery,
  TraktStatus,
  TraktTokenResponse,
  TraktTokens,
  TraktUserMe,
  TraktUserStats,
  TraktWatchedMovie,
  TraktWatchedShow,
  TraktWatchlistMovie,
  TraktWatchlistShow,
  TraktItemDetails,
} from './trakt-types';

class TraktClient extends EventEmitter {
  private http: AxiosInstance;
  private refreshPromise: Promise<TraktTokens | null> | null = null;

  constructor() {
    super();
    this.http = axios.create({ baseURL: TRAKT_API_BASE, timeout: 15000 });
  }

  // ── Credentials ────────────────────────────────────

  private getClientId(): string {
    const override = (getSettingValue('traktClientIdOverride') as string) || '';
    return override || TRAKT_BUNDLED_CLIENT_ID;
  }

  private getClientSecret(): string {
    const override = (getSettingValue('traktClientSecretOverride') as string) || '';
    return override || TRAKT_BUNDLED_CLIENT_SECRET;
  }

  isConfigured(): boolean {
    return Boolean(this.getClientId() && this.getClientSecret());
  }

  isConnected(): boolean {
    return this.isConfigured() && hasTokens();
  }

  hasBundledId(): boolean {
    return Boolean(TRAKT_BUNDLED_CLIENT_ID);
  }

  // ── Status ─────────────────────────────────────────

  getStatus(queueCount: number): TraktStatus {
    const tokens = loadTokens();
    return {
      connected: this.isConfigured() && tokens !== null,
      configured: this.isConfigured(),
      encryptionAvailable: isEncryptionAvailable(),
      username: (getSettingValue('traktUsername') as string | null) || null,
      slug: (getSettingValue('traktUserSlug') as string | null) || null,
      expiresAt: tokens?.expiresAt ?? null,
      connectedAt: (getSettingValue('traktConnectedAt') as string | null) || null,
      queueCount,
    };
  }

  // ── OAuth Device Flow ──────────────────────────────

  async startDeviceFlow(): Promise<TraktDeviceCodeResponse> {
    if (!this.isConfigured()) {
      throw new Error(
        'Trakt is not configured. Add a client_id and client_secret in Settings → Trakt → Advanced.',
      );
    }
    const { data } = await this.http.post<TraktDeviceCodeResponse>(
      '/oauth/device/code',
      { client_id: this.getClientId() },
      { headers: { 'Content-Type': 'application/json' } },
    );
    return data;
  }

  async pollDeviceFlow(deviceCode: string): Promise<DeviceFlowState> {
    try {
      const { data } = await this.http.post<TraktTokenResponse>(
        '/oauth/device/token',
        {
          code: deviceCode,
          client_id: this.getClientId(),
          client_secret: this.getClientSecret(),
        },
        { headers: { 'Content-Type': 'application/json' } },
      );
      const tokens: TraktTokens = {
        accessToken: data.access_token,
        refreshToken: data.refresh_token,
        expiresAt: new Date((data.created_at + data.expires_in) * 1000).toISOString(),
      };
      saveTokens(tokens);
      try {
        const user = await this.getCurrentUser();
        setSetting('traktUsername', user.username);
        setSetting('traktUserSlug', user.ids.slug);
        setSetting('traktConnectedAt', new Date().toISOString());
      } catch (err) {
        console.warn('[trakt-client] auth ok but /users/me failed:', err);
      }
      this.emit('auth-success');
      return 'success';
    } catch (err) {
      const status = (err as AxiosError).response?.status;
      if (status === 400) return 'pending';
      if (status === 404) return 'denied';
      // Same code reused — treat as success: token already issued earlier
      if (status === 409) return 'success';
      if (status === 410) return 'expired';
      if (status === 418) return 'denied';
      if (status === 429) return 'slow_down';
      throw err;
    }
  }

  // ── Token refresh ─────────────────────────────────

  private async refreshTokensIfNeeded(): Promise<TraktTokens | null> {
    const tokens = loadTokens();
    if (!tokens) return null;
    const expiresAt = new Date(tokens.expiresAt).getTime();
    // Refresh 5 minutes before expiry
    if (Date.now() < expiresAt - 5 * 60 * 1000) return tokens;
    return this.refreshTokens();
  }

  private async refreshTokens(): Promise<TraktTokens | null> {
    if (this.refreshPromise) return this.refreshPromise;
    const tokens = loadTokens();
    if (!tokens) return null;

    this.refreshPromise = (async () => {
      try {
        const { data } = await this.http.post<TraktTokenResponse>(
          '/oauth/token',
          {
            refresh_token: tokens.refreshToken,
            client_id: this.getClientId(),
            client_secret: this.getClientSecret(),
            redirect_uri: TRAKT_OOB_REDIRECT,
            grant_type: 'refresh_token',
          },
          { headers: { 'Content-Type': 'application/json' } },
        );
        const fresh: TraktTokens = {
          accessToken: data.access_token,
          refreshToken: data.refresh_token,
          expiresAt: new Date((data.created_at + data.expires_in) * 1000).toISOString(),
        };
        saveTokens(fresh);
        return fresh;
      } catch (err) {
        console.warn('[trakt-client] token refresh failed — clearing credentials');
        clearTokens();
        setSetting('traktUsername', null);
        setSetting('traktUserSlug', null);
        setSetting('traktConnectedAt', null);
        this.emit('token-refresh-failed');
        return null;
      } finally {
        this.refreshPromise = null;
      }
    })();
    return this.refreshPromise;
  }

  private async authedHeaders(): Promise<Record<string, string> | null> {
    const tokens = await this.refreshTokensIfNeeded();
    if (!tokens) return null;
    return {
      'Content-Type': 'application/json',
      'trakt-api-version': TRAKT_API_VERSION,
      'trakt-api-key': this.getClientId(),
      Authorization: `Bearer ${tokens.accessToken}`,
    };
  }

  // ── User ───────────────────────────────────────────

  async getCurrentUser(): Promise<TraktUserMe> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktUserMe>('/users/me', { headers });
    return data;
  }

  // ── Scrobble ───────────────────────────────────────

  /**
   * POST /scrobble/{action}. Returns ok=true on 2xx; ok=false (with status) on
   * 404/409 which are non-retryable. Throws on transient failures so the
   * caller can queue for retry.
   */
  async scrobble(
    action: ScrobbleAction,
    payload: ScrobblePayload,
  ): Promise<{ ok: boolean; status?: number }> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const body = { ...payload, app_version: APP_VERSION };
    try {
      const res = await this.http.post(`/scrobble/${action}`, body, { headers });
      return { ok: true, status: res.status };
    } catch (err) {
      const axErr = err as AxiosError;
      const status = axErr.response?.status;
      // [DEBUG] Scrobble failures (esp. 422 unprocessable entity) are easier
      // to triage with the request body + Trakt's response body in logs.
      console.warn(
        `[trakt-client] scrobble/${action} FAILED status=${status} body=${JSON.stringify(body)} response=${JSON.stringify(axErr.response?.data)}`,
      );
      // 404: nothing to scrobble (no current play). 409: already scrobbled in last 30s.
      if (status === 404 || status === 409) {
        return { ok: false, status };
      }
      throw err;
    }
  }

  // ── Phase 2: Watched-state sync ────────────────────

  async getWatchedMovies(): Promise<TraktWatchedMovie[]> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktWatchedMovie[]>('/sync/watched/movies', { headers });
    return data;
  }

  async getWatchedShows(): Promise<TraktWatchedShow[]> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktWatchedShow[]>('/sync/watched/shows', { headers });
    return data;
  }

  /**
   * POST /sync/history. Caller assembles { movies?: [...], episodes?: [...] }.
   * Returns Trakt's added/not_found summary so callers can detect partial fails.
   */
  async addHistory(body: Record<string, unknown>): Promise<unknown> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.post('/sync/history', body, { headers });
    return data;
  }

  async removeHistory(body: Record<string, unknown>): Promise<unknown> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.post('/sync/history/remove', body, { headers });
    return data;
  }

  // ── Phase 3: Watchlist ─────────────────────────────

  async getWatchlistMovies(): Promise<TraktWatchlistMovie[]> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktWatchlistMovie[]>(
      '/users/me/watchlist/movies',
      { headers, params: { extended: 'full' } },
    );
    return data;
  }

  async getWatchlistShows(): Promise<TraktWatchlistShow[]> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktWatchlistShow[]>(
      '/users/me/watchlist/shows',
      { headers, params: { extended: 'full' } },
    );
    return data;
  }

  async addToWatchlist(body: Record<string, unknown>): Promise<unknown> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.post('/sync/watchlist', body, { headers });
    return data;
  }

  async removeFromWatchlist(body: Record<string, unknown>): Promise<unknown> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.post('/sync/watchlist/remove', body, { headers });
    return data;
  }

  // ── Phase 4: Item details / ratings ────────────────

  /**
   * Look up a movie or show by TMDB id and return its Trakt details
   * (rating + votes). Uses /search/tmdb/{id}?extended=full so we get
   * everything we need in a single round-trip.
   */
  async getItemDetailsByTmdb(
    tmdbId: string,
    type: 'movie' | 'show',
  ): Promise<TraktItemDetails | null> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    try {
      const { data } = await this.http.get<unknown[]>(`/search/tmdb/${encodeURIComponent(tmdbId)}`, {
        headers,
        params: { type, extended: 'full' },
      });
      if (!Array.isArray(data) || data.length === 0) return null;
      const first = data[0] as Record<string, unknown>;
      const item = (first[type] as Record<string, unknown>) ?? null;
      if (!item) return null;
      return {
        rating: typeof item.rating === 'number' ? item.rating : undefined,
        votes: typeof item.votes === 'number' ? item.votes : undefined,
        ids: (item.ids as TraktItemDetails['ids']) ?? {},
      };
    } catch (err) {
      const status = (err as AxiosError).response?.status;
      if (status === 404) return null;
      throw err;
    }
  }

  // ── Analytics ──────────────────────────────────────

  async getUserStats(): Promise<TraktUserStats> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const { data } = await this.http.get<TraktUserStats>('/users/me/stats', { headers });
    return data;
  }

  /**
   * Paginated GET /sync/history. Trakt returns at most `limit` events per
   * page and exposes total counts via X-Pagination-* headers. Caller is
   * responsible for iterating until exhaustion or hitting a cap.
   */
  async getHistory(query: TraktHistoryQuery = {}): Promise<TraktHistoryPage> {
    const headers = await this.authedHeaders();
    if (!headers) throw new Error('Not connected to Trakt');
    const params: Record<string, string | number> = {};
    if (query.startAt) params.start_at = query.startAt;
    if (query.endAt) params.end_at = query.endAt;
    if (query.page) params.page = query.page;
    if (query.limit) params.limit = query.limit;
    const path = query.type ? `/sync/history/${query.type}` : '/sync/history';
    const res = await this.http.get<TraktHistoryItem[]>(path, { headers, params });
    const page = Number(res.headers['x-pagination-page'] ?? query.page ?? 1);
    const pageCount = Number(res.headers['x-pagination-page-count'] ?? 1);
    const itemCount = Number(res.headers['x-pagination-item-count'] ?? res.data.length);
    return { items: res.data, page, pageCount, itemCount };
  }

  // ── Disconnect ─────────────────────────────────────

  disconnect(): void {
    clearTokens();
    setSetting('traktUsername', null);
    setSetting('traktUserSlug', null);
    setSetting('traktConnectedAt', null);
    this.emit('disconnected');
  }
}

export const traktClient = new TraktClient();
