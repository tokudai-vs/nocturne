import axios, { AxiosInstance } from 'axios';
import { app, BrowserWindow } from 'electron';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { join } from 'path';
import { randomUUID } from 'crypto';
import { APP_VERSION, CLIENT_NAME, DEVICE_NAME } from '../shared/constants';

const ITEM_FIELDS = 'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags,RemoteTrailers';
const ITEM_FIELDS_LIGHT = 'Overview,UserData,ImageTags,BackdropImageTags';

function getDeviceId(): string {
  const userDataPath = app.getPath('userData');
  const deviceFilePath = join(userDataPath, 'device.json');

  if (existsSync(deviceFilePath)) {
    const data = JSON.parse(readFileSync(deviceFilePath, 'utf-8'));
    if (data.deviceId) return data.deviceId;
  }

  const deviceId = randomUUID();
  if (!existsSync(userDataPath)) {
    mkdirSync(userDataPath, { recursive: true });
  }
  writeFileSync(deviceFilePath, JSON.stringify({ deviceId }), 'utf-8');
  return deviceId;
}

class EmbyClient {
  /** Axios instance for active-server requests — has auth interceptors */
  private client: AxiosInstance;
  /** Separate axios instance for standalone/cross-server requests — NO interceptors */
  private standalone: AxiosInstance;
  private serverUrl: string | null = null;
  private accessToken: string | null = null;
  private userId: string | null = null;
  private deviceId: string;

  constructor() {
    this.deviceId = getDeviceId();
    this.client = axios.create({ timeout: 10000 });
    this.standalone = axios.create({ timeout: 10000 });

    // Active-server request interceptor — injects current token
    this.client.interceptors.request.use((config) => {
      const tokenPart = this.accessToken ? `, Token="${this.accessToken}"` : '';
      config.headers['X-Emby-Authorization'] =
        `MediaBrowser Client="${CLIENT_NAME}", Device="${DEVICE_NAME}", DeviceId="${this.deviceId}", Version="${APP_VERSION}"${tokenPart}`;
      return config;
    });

    // Active-server 401 interceptor — fires session-expired (debounced)
    let lastSessionExpiredAt = 0;
    this.client.interceptors.response.use(undefined, (error) => {
      if (error?.response?.status === 401 && this.accessToken) {
        const now = Date.now();
        if (now - lastSessionExpiredAt > 5000) {
          lastSessionExpiredAt = now;
          for (const win of BrowserWindow.getAllWindows()) {
            win.webContents.send('auth:session-expired');
          }
        }
      }
      return Promise.reject(error);
    });
  }

  get baseUrl(): string | null {
    return this.serverUrl;
  }

  get token(): string | null {
    return this.accessToken;
  }

  get currentUserId(): string | null {
    return this.userId;
  }

  private contextStack: Array<{ url: string | null; token: string | null; userId: string | null }> = [];

  setServer(url: string): void {
    const trimmed = url.replace(/\/+$/, '');
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      throw new Error('Server URL must use http or https');
    }
    this.serverUrl = trimmed;
  }

  setAuth(token: string, userId: string): void {
    this.accessToken = token;
    this.userId = userId;
  }

  clearAuth(): void {
    this.accessToken = null;
    this.userId = null;
  }

  /** Save the current server context onto the stack so it can be restored after sync operations. */
  pushContext(): void {
    this.contextStack.push({
      url: this.serverUrl,
      token: this.accessToken,
      userId: this.userId,
    });
  }

  /** Restore the most recently saved server context from the stack. */
  popContext(): void {
    const ctx = this.contextStack.pop();
    if (ctx) {
      this.serverUrl = ctx.url;
      this.accessToken = ctx.token;
      this.userId = ctx.userId;
    }
  }

  private url(path: string): string {
    return `${this.serverUrl}${path}`;
  }

  // Auth
  async getPublicInfo() {
    const { data } = await this.client.get(this.url('/emby/System/Info/Public'));
    return data;
  }

  async getPublicUsers() {
    const { data } = await this.client.get(this.url('/emby/Users/Public'));
    return data;
  }

  async login(username: string, password: string) {
    const { data } = await this.client.post(this.url('/emby/Users/AuthenticateByName'), {
      Username: username,
      Pw: password,
    });
    this.setAuth(data.AccessToken, data.User.Id);
    return data;
  }

  async logout() {
    if (this.accessToken) {
      await this.client.post(this.url('/emby/Sessions/Logout'));
      this.clearAuth();
    }
  }

  // Library
  async getViews() {
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Views`),
    );
    return data;
  }

  async getItems(parentId: string, params: Record<string, unknown> = {}) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items`),
      {
        params: { ParentId: parentId, Fields: fields, ...params },
      },
    );
    return data;
  }

  async getItem(itemId: string) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/${itemId}`),
      { params: { Fields: fields } },
    );
    return data;
  }

  async getLatestItems(parentId: string, limit = 16) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/Latest`),
      { params: { ParentId: parentId, Limit: limit, Fields: fields } },
    );
    return data;
  }

  async getResumeItems(limit = 12) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/Resume`),
      {
        params: { Limit: limit, Recursive: true, Fields: fields, MediaTypes: 'Video' },
      },
    );
    return data;
  }

  async getNextUp() {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(this.url('/emby/Shows/NextUp'), {
      params: { UserId: this.userId, Limit: 12, Fields: fields },
    });
    return data;
  }

  async getSimilar(itemId: string) {
    const { data } = await this.client.get(
      this.url(`/emby/Items/${itemId}/Similar`),
      { params: { UserId: this.userId, Limit: 12 } },
    );
    return data;
  }

  async getSeasons(seriesId: string) {
    const fields = ITEM_FIELDS_LIGHT;
    const { data } = await this.client.get(
      this.url(`/emby/Shows/${seriesId}/Seasons`),
      { params: { UserId: this.userId, Fields: fields } },
    );
    return data;
  }

  async getEpisodes(seriesId: string, seasonId: string) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Shows/${seriesId}/Episodes`),
      { params: { UserId: this.userId, SeasonId: seasonId, Fields: fields } },
    );
    return data;
  }

  // Media
  async getPlaybackInfo(itemId: string) {
    const { data } = await this.client.post(
      this.url(`/emby/Items/${itemId}/PlaybackInfo`),
      { DeviceProfile: {} },
      { params: { UserId: this.userId } },
    );
    return data;
  }

  getStreamUrl(itemId: string, mediaSourceId: string): string {
    return `${this.serverUrl}/emby/Videos/${itemId}/stream?Static=true&MediaSourceId=${mediaSourceId}&api_key=${this.accessToken}`;
  }

  async reportPlaybackStart(info: Record<string, unknown>) {
    await this.client.post(this.url('/emby/Sessions/Playing'), info);
  }

  async reportPlaybackProgress(info: Record<string, unknown>) {
    await this.client.post(this.url('/emby/Sessions/Playing/Progress'), info);
  }

  async reportPlaybackStopped(info: Record<string, unknown>) {
    await this.client.post(this.url('/emby/Sessions/Playing/Stopped'), info);
  }

  // User
  async getCurrentUser() {
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}`),
    );
    return data;
  }

  async markPlayed(itemId: string) {
    await this.client.post(
      this.url(`/emby/Users/${this.userId}/PlayedItems/${itemId}`),
    );
  }

  async markUnplayed(itemId: string) {
    await this.client.delete(
      this.url(`/emby/Users/${this.userId}/PlayedItems/${itemId}`),
    );
  }

  async updateFavorite(itemId: string, isFavorite: boolean) {
    if (isFavorite) {
      await this.client.post(
        this.url(`/emby/Users/${this.userId}/FavoriteItems/${itemId}`),
      );
    } else {
      await this.client.delete(
        this.url(`/emby/Users/${this.userId}/FavoriteItems/${itemId}`),
      );
    }
  }

  // Search
  async search(term: string, filters: Record<string, unknown> = {}) {
    const fields = ITEM_FIELDS;
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items`),
      {
        params: {
          Recursive: true,
          SearchTerm: term,
          IncludeItemTypes: 'Movie,Series,Episode',
          Fields: fields,
          Limit: 24,
          ...filters,
        },
      },
    );
    return data;
  }

  // ── Standalone server methods ───────────────────────
  // These operate on a specific server URL without changing the active client state.

  private authHeader(token?: string): string {
    const tokenPart = token ? `, Token="${token}"` : '';
    return `MediaBrowser Client="${CLIENT_NAME}", Device="${DEVICE_NAME}", DeviceId="${this.deviceId}", Version="${APP_VERSION}"${tokenPart}`;
  }

  /** Build axios config for standalone requests */
  private standaloneHeaders(token?: string) {
    return { 'X-Emby-Authorization': this.authHeader(token) };
  }

  async getPublicInfoForServer(serverUrl: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/System/Info/Public`;
    const { data } = await this.standalone.get(url, { headers: this.standaloneHeaders() });
    return data;
  }

  async getPublicUsersForServer(serverUrl: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/Public`;
    const { data } = await this.standalone.get(url, { headers: this.standaloneHeaders() });
    return data;
  }

  async loginToServer(serverUrl: string, username: string, password: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/AuthenticateByName`;
    const { data } = await this.standalone.post(url, { Username: username, Pw: password }, { headers: this.standaloneHeaders() });
    return data; // { AccessToken, User, ServerId }
  }

  async getViewsForServer(serverUrl: string, token: string, userId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}/Views`;
    const { data } = await this.standalone.get(url, { headers: this.standaloneHeaders(token) });
    return data;
  }

  // ── Standalone data-fetch methods (for sync engine — don't touch active-server state) ──

  async getCurrentUserForServer(serverUrl: string, token: string, userId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}`;
    const { data } = await this.standalone.get(url, { headers: this.standaloneHeaders(token) });
    return data;
  }

  async getItemsForServer(
    serverUrl: string, token: string, userId: string,
    parentId: string, params: Record<string, unknown> = {},
  ) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}/Items`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { ParentId: parentId, Fields: ITEM_FIELDS, ...params },
    });
    return data;
  }

  async getItemForServer(serverUrl: string, token: string, userId: string, itemId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}/Items/${itemId}`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { Fields: ITEM_FIELDS },
    });
    return data;
  }

  async getSimilarForServer(serverUrl: string, token: string, userId: string, itemId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Items/${itemId}/Similar`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { UserId: userId, Limit: 12 },
    });
    return data;
  }

  async getSeasonsForServer(serverUrl: string, token: string, userId: string, seriesId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Shows/${seriesId}/Seasons`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { UserId: userId, Fields: ITEM_FIELDS_LIGHT },
    });
    return data;
  }

  async getEpisodesForServer(serverUrl: string, token: string, userId: string, seriesId: string, seasonId: string) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Shows/${seriesId}/Episodes`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { UserId: userId, SeasonId: seasonId, Fields: ITEM_FIELDS },
    });
    return data;
  }

  async getLatestItemsForServer(serverUrl: string, token: string, userId: string, parentId: string, limit = 20) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}/Items/Latest`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { ParentId: parentId, Limit: limit, Fields: ITEM_FIELDS_LIGHT },
    });
    return data;
  }

  async getResumeItemsForServer(serverUrl: string, token: string, userId: string, limit = 12) {
    const url = `${serverUrl.replace(/\/+$/, '')}/emby/Users/${userId}/Items/Resume`;
    const { data } = await this.standalone.get(url, {
      headers: this.standaloneHeaders(token),
      params: { Limit: limit, Fields: ITEM_FIELDS_LIGHT, MediaTypes: 'Video' },
    });
    return data;
  }

  async checkServerReachable(serverUrl: string): Promise<boolean> {
    try {
      await this.standalone.get(
        `${serverUrl.replace(/\/+$/, '')}/emby/System/Info/Public`,
        { headers: this.standaloneHeaders(), timeout: 5000 },
      );
      return true;
    } catch {
      return false;
    }
  }

  // ── Cross-server methods ────────────────────────────
  // These make requests to a specific server, not the active one.
  // All use this.standalone to avoid touching active-server state.

  async markPlayedOnServer(
    serverUrl: string,
    token: string,
    userId: string,
    itemId: string,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Users/${userId}/PlayedItems/${itemId}`;
    await this.standalone.post(url, null, { headers: this.standaloneHeaders(token) });
  }

  async markUnplayedOnServer(
    serverUrl: string,
    token: string,
    userId: string,
    itemId: string,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Users/${userId}/PlayedItems/${itemId}`;
    await this.standalone.delete(url, { headers: this.standaloneHeaders(token) });
  }

  async updateFavoriteOnServer(
    serverUrl: string,
    token: string,
    userId: string,
    itemId: string,
    isFavorite: boolean,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Users/${userId}/FavoriteItems/${itemId}`;
    if (isFavorite) {
      await this.standalone.post(url, null, { headers: this.standaloneHeaders(token) });
    } else {
      await this.standalone.delete(url, { headers: this.standaloneHeaders(token) });
    }
  }

  async reportPlaybackStoppedToServer(
    serverUrl: string,
    token: string,
    data: Record<string, unknown>,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Sessions/Playing/Stopped`;
    await this.standalone.post(url, data, { headers: this.standaloneHeaders(token) });
  }

  async reportPlaybackStartToServer(
    serverUrl: string,
    token: string,
    data: Record<string, unknown>,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Sessions/Playing`;
    await this.standalone.post(url, data, { headers: this.standaloneHeaders(token) });
  }

  async getPlaybackInfoForServer(
    serverUrl: string,
    token: string,
    userId: string,
    itemId: string,
  ) {
    const url = `${serverUrl}/emby/Items/${itemId}/PlaybackInfo`;
    const { data } = await this.standalone.post(
      url,
      { DeviceProfile: {} },
      { params: { UserId: userId }, headers: this.standaloneHeaders(token) },
    );
    return data;
  }

  async reportPlaybackProgressToServer(
    serverUrl: string,
    token: string,
    data: Record<string, unknown>,
  ): Promise<void> {
    const url = `${serverUrl}/emby/Sessions/Playing/Progress`;
    await this.standalone.post(url, data, { headers: this.standaloneHeaders(token) });
  }

  getStreamUrlForServer(
    serverUrl: string,
    token: string,
    itemId: string,
    mediaSourceId: string,
  ): string {
    return `${serverUrl}/emby/Videos/${itemId}/stream?Static=true&MediaSourceId=${mediaSourceId}&api_key=${token}`;
  }

  // Images
  getImageUrl(
    itemId: string,
    imageType: string,
    params: Record<string, unknown> = {},
  ): string {
    const query = new URLSearchParams();
    if (params.maxWidth) query.set('maxWidth', String(params.maxWidth));
    if (params.maxHeight) query.set('maxHeight', String(params.maxHeight));
    if (params.tag) query.set('tag', String(params.tag));
    query.set('quality', String(params.quality ?? 90));
    const qs = query.toString();
    return `${this.serverUrl}/emby/Items/${itemId}/Images/${imageType}${qs ? `?${qs}` : ''}`;
  }
}

export const embyClient = new EmbyClient();
