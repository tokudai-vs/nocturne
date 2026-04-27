import { readFileSync, writeFileSync, existsSync } from 'fs';
import { join } from 'path';
import { app } from 'electron';

// Using a simple JSON store instead of electron-store to avoid ESM/CJS compatibility issues.
// Same API surface, same persistence, zero dependencies.

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
  powerMode: 'performance' | 'balanced' | 'efficiency';
  startFullscreen: boolean;
  startPage: 'home' | 'last-visited';
  imageCacheMaxMB: number;
  syncOnStartup: boolean;
  firstLaunchComplete: boolean;
  lastServerUrl: string;
  // ── Trakt ──
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
}

const DEFAULTS: NocturneSettings = {
  servers: [],
  activeServerId: null,
  libraryMappings: {},
  libraryMode: 'separate',
  combinedMappings: {},
  combinedMappingsInitialized: false,
  showUnmappedLibraries: true,
  preferredQuality: 'highest',
  defaultSubtitleLanguage: 'none',
  defaultAudioLanguage: 'eng',
  autoPlayNextEpisode: true,
  subtitleFont: 'Segoe UI Semibold',
  subtitleSize: 46,
  subtitleColor: '#FFFFFF',
  subtitleBorderSize: 2.5,
  subtitleBackground: 'none',
  subtitlePosition: 95,
  powerMode: 'balanced',
  startFullscreen: true,
  startPage: 'home',
  imageCacheMaxMB: 500,
  syncOnStartup: true,
  firstLaunchComplete: false,
  lastServerUrl: '',
  traktAutoScrobble: true,
  traktSyncWatchedState: true,
  traktShowWatchlistInSidebar: true,
  traktUsername: null,
  traktUserSlug: null,
  traktConnectedAt: null,
  traktLastSyncAt: null,
  traktLastWatchlistSyncAt: null,
  traktClientIdOverride: '',
  traktClientSecretOverride: '',
};

let settings: NocturneSettings = { ...DEFAULTS };
let settingsPath = '';

function load(): void {
  try {
    if (existsSync(settingsPath)) {
      const raw = readFileSync(settingsPath, 'utf-8');
      const parsed = JSON.parse(raw);
      settings = { ...DEFAULTS, ...parsed };
    }
  } catch {
    settings = { ...DEFAULTS };
  }
}

function save(): void {
  writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
}

export function initSettings(): void {
  settingsPath = join(app.getPath('userData'), 'nocturne-settings.json');
  load();
}

export function getSettings(): NocturneSettings {
  return { ...settings };
}

export function getSettingValue<K extends keyof NocturneSettings>(key: K): NocturneSettings[K] {
  return settings[key];
}

export function setSetting<K extends keyof NocturneSettings>(key: K, value: NocturneSettings[K]): void {
  settings[key] = value;
  save();
}

export function setMultipleSettings(data: Partial<NocturneSettings>): void {
  Object.assign(settings, data);
  save();
}

export function resetSettings(): void {
  settings = { ...DEFAULTS };
  save();
}
