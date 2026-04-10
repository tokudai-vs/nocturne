import axios, { AxiosInstance } from 'axios';
import { app } from 'electron';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { join } from 'path';
import { randomUUID } from 'crypto';
import { APP_VERSION, CLIENT_NAME, DEVICE_NAME } from '../shared/constants';

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
  private client: AxiosInstance;
  private serverUrl: string | null = null;
  private accessToken: string | null = null;
  private userId: string | null = null;
  private deviceId: string;

  constructor() {
    this.deviceId = getDeviceId();
    this.client = axios.create({ timeout: 15000 });

    this.client.interceptors.request.use((config) => {
      const tokenPart = this.accessToken ? `, Token="${this.accessToken}"` : '';
      config.headers['X-Emby-Authorization'] =
        `MediaBrowser Client="${CLIENT_NAME}", Device="${DEVICE_NAME}", DeviceId="${this.deviceId}", Version="${APP_VERSION}"${tokenPart}`;
      return config;
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
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items`),
      {
        params: { ParentId: parentId, Fields: fields, ...params },
      },
    );
    return data;
  }

  async getItem(itemId: string) {
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/${itemId}`),
      { params: { Fields: fields } },
    );
    return data;
  }

  async getLatestItems(parentId: string, limit = 16) {
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/Latest`),
      { params: { ParentId: parentId, Limit: limit, Fields: fields } },
    );
    return data;
  }

  async getResumeItems() {
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
    const { data } = await this.client.get(
      this.url(`/emby/Users/${this.userId}/Items/Resume`),
      {
        params: { Limit: 12, Recursive: true, Fields: fields, MediaTypes: 'Video' },
      },
    );
    return data;
  }

  async getNextUp() {
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
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
    const fields = 'Overview,UserData,ImageTags,BackdropImageTags';
    const { data } = await this.client.get(
      this.url(`/emby/Shows/${seriesId}/Seasons`),
      { params: { UserId: this.userId, Fields: fields } },
    );
    return data;
  }

  async getEpisodes(seriesId: string, seasonId: string) {
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
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
    const fields =
      'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags';
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
