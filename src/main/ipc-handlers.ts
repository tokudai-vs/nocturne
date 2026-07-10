import { app, ipcMain, shell } from 'electron';
import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { embyClient } from './emby-client';
import { mpvManager } from './mpv-manager';
import { getMainWindow } from './window';
import { syncEngine } from './sync-engine';
import { getCachedUrl, precacheImages } from './image-cache';
import {
  getSettings,
  getSettingValue,
  setSetting,
  setMultipleSettings,
  resetSettings,
  type NocturneSettings,
} from './settings';
import { suggestLibraryMapping } from './library-mapper';
import {
  getVirtualLibraries,
  getVirtualLibraryItems,
  getVirtualLibraryLatest,
  getVirtualLibraryHeroes,
  getItemVersions,
  getSeriesEpisodeVersions,
  getVirtualLibraryDedupStats,
} from './virtual-library';
import { serverManager } from './server-manager';
import type { ServerConfig } from './settings';
import {
  getItem as dbGetItem,
  getItems as dbGetItems,
  getResumeItems as dbGetResumeItems,
  getResumeItemsDeduped as dbGetResumeItemsDeduped,
  getLatestItems as dbGetLatestItems,
  searchItems as dbSearchItems,
  searchItemsDeduped as dbSearchItemsDeduped,
  getStats as dbGetStats,
  clearItemsAndDedup as dbClearItemsAndDedup,
  clearAll as dbClearAll,
  clearEpisodeSyncMarkers as dbClearEpisodeSyncMarkers,
  closeDatabase as dbCloseDatabase,
  hasAnyCachedItems,
  getSyncState,
  setSyncState,
  deleteSyncState,
  getGroupVersions as dbGetGroupVersions,
  getResumeClearTargets as dbGetResumeClearTargets,
  getDedupGroupsForItemIds as dbGetDedupGroupsForItemIds,
  getAdjacentEpisodes as dbGetAdjacentEpisodes,
  setWatchlistMutationHook,
  updateItemUserData,
  checkpoint,
  clearTraktQueue,
  clearTraktWatched,
  clearTraktWatchlist,
  clearTraktRatings,
  countTraktWatched,
  isMovieWatchedOnTrakt,
  isEpisodeWatchedOnTrakt,
  type ItemFilters,
} from './database';
import { checkForUpdates, downloadUpdate, installUpdate, getUpdateStatus } from './updater';
import { traktClient } from './trakt-client';
import { traktScrobbler } from './trakt-scrobbler';
import { traktSync } from './trakt-sync';
import { attachFallbacksToItems } from './image-fallbacks';
import { computeAnalytics, type AnalyticsLifetimeBlock } from './analytics';
import { fetchSegments } from './introdb-client';
import { watchPartyBinaryManager } from './watchparty-binary-manager';
import { watchPartyEncoderProbe } from './watchparty-encoder-probe';
import { watchPartySessionManager } from './watchparty-session';
import { watchPartyLogger } from './watchparty-logger';
import { toIso6391 } from '../shared/languages';
import { TRAKT_BUNDLED_CLIENT_ID } from '../shared/trakt-config';
import type { WatchPartySource } from '../shared/watchparty-types';
import {
  getTraktWatchlistAsCachedItems,
  invalidateTraktWatchlistCache,
  TRAKT_WATCHLIST_VLIB_ID,
} from './virtual-library';

type IpcResult<T = unknown> = { success: true; data: T } | { success: false; error: string };

function ok<T>(data: T): IpcResult<T> {
  return { success: true, data };
}

function fail(err: unknown): IpcResult<never> {
  const message =
    err instanceof Error ? err.message : typeof err === 'string' ? err : 'Unknown error';
  return { success: false, error: message };
}

// ── Input validation helpers ────────────────────────────
function isNonEmptyString(v: unknown): v is string {
  return typeof v === 'string' && v.length > 0;
}

function isValidUrl(v: unknown): v is string {
  if (typeof v !== 'string') return false;
  try {
    const u = new URL(v);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

const ALLOWED_SETTING_KEYS = new Set<string>([
  'servers', 'activeServerId', 'libraryMappings', 'libraryMode', 'combinedMappings',
  'combinedMappingsInitialized', 'showUnmappedLibraries', 'preferredQuality',
  'defaultSubtitleLanguage', 'defaultAudioLanguage', 'autoPlayNextEpisode', 'subtitleFont',
  'subtitleSize', 'subtitleColor', 'subtitleBorderSize', 'subtitleBackground',
  'subtitlePosition', 'powerMode', 'startFullscreen', 'startPage', 'imageCacheMaxMB',
  'syncOnStartup', 'firstLaunchComplete', 'lastServerUrl',
  // Skip segments (TheIntroDB) — per-type modes: 'button' | 'auto' | 'off'.
  'skipIntroMode', 'skipRecapMode', 'skipCreditsMode',
  // Auto-download subtitles (OpenSubtitles).
  'autoDownloadSubtitles', 'preferredSubtitleLanguage',
  // Trakt — only the renderer-settable subset. Username/slug/connectedAt and
  // last-sync timestamps are written by the main process during sync work.
  'traktAutoScrobble', 'traktSyncWatchedState', 'traktShowWatchlistInSidebar',
  'traktClientIdOverride', 'traktClientSecretOverride',
  'traktHistoryBackfillCap',
  // Watch Party — Danger Zone unlocks: guest-count limit, 4K source input,
  // 4K output ceiling, CPU-only override.
  'watchPartyMaxGuestsUnlocked',
  'watchPartyPrefer4kSource',
  'watchPartyAllow4kOutput',
  'watchPartyAllowCpuEncoder',
]);

export function registerIpcHandlers(): void {
  // Bridge the watchlist mutation hook from database.ts to the cache
  // invalidator in virtual-library.ts. database.ts can't import
  // virtual-library at the top level (cycle) and a runtime require() doesn't
  // survive the electron-vite bundle, so the hook is wired here at startup.
  setWatchlistMutationHook(() => invalidateTraktWatchlistCache());

  // ── Auth ──────────────────────────────────────────────
  ipcMain.handle('emby:auth:connect', async (_, { url }: { url: string }) => {
    if (!isValidUrl(url)) return fail('Invalid server URL');
    try {
      embyClient.setServer(url);
      const info = await embyClient.getPublicInfo();
      return ok(info);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:auth:public-users', async () => {
    try {
      const users = await embyClient.getPublicUsers();
      return ok(users);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:auth:login',
    async (_, { username, password }: { username: string; password: string }) => {
      try {
        const result = await embyClient.login(username, password);
        return ok(result);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('emby:auth:logout', async () => {
    try {
      await embyClient.logout();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Standalone server auth (does NOT change active client) ──
  ipcMain.handle('emby:auth:connect-to-server', async (_, { url }: { url: string }) => {
    if (!isValidUrl(url)) return fail('Invalid server URL');
    try {
      const info = await embyClient.getPublicInfoForServer(url);
      return ok(info);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:auth:public-users-for-server', async (_, { url }: { url: string }) => {
    if (!isValidUrl(url)) return fail('Invalid server URL');
    try {
      const users = await embyClient.getPublicUsersForServer(url);
      return ok(users);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:auth:login-to-server',
    async (_, { url, username, password }: { url: string; username: string; password: string }) => {
      if (!isValidUrl(url)) return fail('Invalid server URL');
      if (!isNonEmptyString(username)) return fail('Username is required');
      try {
        const result = await embyClient.loginToServer(url, username, password);
        return ok(result);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('emby:auth:check-server', async (_, { url }: { url: string }) => {
    if (!isValidUrl(url)) return ok(false);
    try {
      const reachable = await embyClient.checkServerReachable(url);
      return ok(reachable);
    } catch {
      return ok(false);
    }
  });

  ipcMain.handle('libraries:get-all-servers-views', async () => {
    try {
      const servers = serverManager.getServers();
      const allViews: Array<{ Id: string; Name: string; Type: string; serverId: string; serverName: string }> = [];
      const errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }> = [];

      for (const server of servers) {
        try {
          const views = await embyClient.getViewsForServer(server.url, server.accessToken, server.userId);
          for (const view of (views.Items || [])) {
            allViews.push({
              Id: view.Id,
              Name: view.Name,
              Type: view.Type || 'CollectionFolder',
              serverId: server.id,
              serverName: server.name,
            });
          }
        } catch (err: unknown) {
          const status = (err as { response?: { status?: number } })?.response?.status;
          errors.push({
            serverId: server.id,
            serverName: server.name,
            reason: status === 401 ? 'auth-expired' : 'offline',
          });
        }
      }
      return ok({ views: allViews, errors });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('libraries:get-all-servers-latest', async (_, { limit }: { limit?: number } = {}) => {
    try {
      const servers = serverManager.getServers();
      const errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }> = [];

      const perServer = await Promise.all(
        servers.map(async (server) => {
          try {
            const viewsResp = await embyClient.getViewsForServer(
              server.url, server.accessToken, server.userId,
            );
            const views = (viewsResp.Items || []) as Array<{ Id: string; Name: string }>;
            const libs = await Promise.all(
              views.map(async (view) => {
                try {
                  const items = (await embyClient.getLatestItemsForServer(
                    server.url, server.accessToken, server.userId, view.Id, limit || 20,
                  )) as Record<string, unknown>[];
                  return {
                    libraryId: view.Id,
                    libraryName: view.Name,
                    serverId: server.id,
                    serverName: server.name,
                    items: items.map((it) => ({ ...it, serverId: server.id })),
                  };
                } catch {
                  return {
                    libraryId: view.Id,
                    libraryName: view.Name,
                    serverId: server.id,
                    serverName: server.name,
                    items: [] as Record<string, unknown>[],
                  };
                }
              }),
            );
            return { libs, error: null as null | { serverId: string; serverName: string; reason: 'offline' | 'auth-expired' } };
          } catch (err: unknown) {
            const status = (err as { response?: { status?: number } })?.response?.status;
            return {
              libs: [] as Array<{ libraryId: string; libraryName: string; serverId: string; serverName: string; items: Record<string, unknown>[] }>,
              error: {
                serverId: server.id,
                serverName: server.name,
                reason: status === 401 ? ('auth-expired' as const) : ('offline' as const),
              },
            };
          }
        }),
      );

      const libraries: Array<{ libraryId: string; libraryName: string; serverId: string; serverName: string; items: Record<string, unknown>[] }> = [];
      for (const r of perServer) {
        if (r.error) errors.push(r.error);
        libraries.push(...r.libs);
      }
      return ok({ libraries, errors });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('libraries:get-all-servers-resume', async () => {
    try {
      const servers = serverManager.getServers();
      const errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }> = [];

      const perServer = await Promise.all(
        servers.map(async (server) => {
          try {
            const resp = await embyClient.getResumeItemsForServer(
              server.url, server.accessToken, server.userId,
            );
            const items = ((resp.Items || []) as Record<string, unknown>[]).map(
              (it) => ({ ...it, serverId: server.id }),
            );
            return { items, error: null as null | { serverId: string; serverName: string; reason: 'offline' | 'auth-expired' } };
          } catch (err: unknown) {
            const status = (err as { response?: { status?: number } })?.response?.status;
            return {
              items: [] as Record<string, unknown>[],
              error: {
                serverId: server.id,
                serverName: server.name,
                reason: status === 401 ? ('auth-expired' as const) : ('offline' as const),
              },
            };
          }
        }),
      );

      const items: Record<string, unknown>[] = [];
      for (const r of perServer) {
        if (r.error) errors.push(r.error);
        items.push(...r.items);
      }
      return ok({ items, errors });
    } catch (e) {
      return fail(e);
    }
  });

  // ── Library ───────────────────────────────────────────
  ipcMain.handle('emby:library:get-views', async () => {
    try {
      const views = await embyClient.getViews();
      return ok(views);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:library:get-items',
    async (_, { parentId, params }: { parentId: string; params?: Record<string, unknown> }) => {
      try {
        const items = await embyClient.getItems(parentId, params);
        return ok(items);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('emby:library:get-item', async (_, { itemId }: { itemId: string }) => {
    try {
      const item = await embyClient.getItem(itemId);
      return ok(item);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:library:get-latest',
    async (_, { parentId, limit }: { parentId: string; limit?: number }) => {
      try {
        const items = await embyClient.getLatestItems(parentId, limit);
        return ok(items);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('emby:library:get-resume', async () => {
    try {
      const items = await embyClient.getResumeItems();
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:library:get-nextup', async () => {
    try {
      const items = await embyClient.getNextUp();
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:library:get-similar', async (_, { itemId }: { itemId: string }) => {
    try {
      const items = await embyClient.getSimilar(itemId);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:library:get-seasons', async (_, { seriesId }: { seriesId: string }) => {
    try {
      const seasons = await embyClient.getSeasons(seriesId);
      return ok(seasons);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:library:get-episodes',
    async (_, { seriesId, seasonId }: { seriesId: string; seasonId: string }) => {
      try {
        const episodes = await embyClient.getEpisodes(seriesId, seasonId);
        return ok(episodes);
      } catch (e) {
        return fail(e);
      }
    },
  );

  // ── Media ─────────────────────────────────────────────
  ipcMain.handle(
    'emby:media:playback-info',
    async (_, { itemId, serverId }: { itemId: string; serverId?: string }) => {
      try {
        // Cross-server: dedup version picks can target an item on a server
        // other than the active one. Route through that server's config —
        // the active client would 404/500 on a foreign item id.
        const activeServer = serverManager.getActiveServer();
        if (serverId && serverId !== activeServer?.id) {
          const server = serverManager.getServer(serverId);
          if (!server) return fail(`Unknown server ${serverId}`);
          const info = await embyClient.getPlaybackInfoForServer(
            server.url,
            server.accessToken,
            server.userId,
            itemId,
          );
          return ok(info);
        }
        const info = await embyClient.getPlaybackInfo(itemId);
        return ok(info);
      } catch (e) {
        console.error(`[media:playback-info] failed for item ${itemId} (serverId=${serverId ?? 'active'}):`, e instanceof Error ? e.message : e);
        return fail(e);
      }
    },
  );

  ipcMain.handle(
    'emby:media:stream-url',
    async (_, { itemId, mediaSourceId }: { itemId: string; mediaSourceId: string }) => {
      try {
        const url = embyClient.getStreamUrl(itemId, mediaSourceId);
        return ok(url);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('emby:media:report-start', async (_, data) => {
    try {
      await embyClient.reportPlaybackStart(data);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:media:report-progress', async (_, data) => {
    try {
      await embyClient.reportPlaybackProgress(data);
      const itemId = typeof data?.ItemId === 'string' ? data.ItemId : null;
      if (itemId) updateItemUserData(itemId, { last_played_date: new Date().toISOString() });
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:media:report-stop', async (_, data) => {
    try {
      await embyClient.reportPlaybackStopped(data);
      const itemId = typeof data?.ItemId === 'string' ? data.ItemId : null;
      if (itemId) updateItemUserData(itemId, { last_played_date: new Date().toISOString() });
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── User ──────────────────────────────────────────────
  ipcMain.handle('emby:user:current', async () => {
    try {
      const user = await embyClient.getCurrentUser();
      return ok(user);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:user:mark-played', async (_, { itemId }: { itemId: string }) => {
    console.log(`[emby:user:mark-played] handler entry, itemId=${itemId}`);
    try {
      await embyClient.markPlayed(itemId);
      // Mirror the cross-server-aware handler so any path that ends here
      // (legacy callers, useEmby hook, etc.) still pushes to Trakt and
      // updates local cache. Without this, a non-`item:mark-played` caller
      // would silently bypass Trakt sync.
      updateItemUserData(itemId, { played: 1, playback_position_ticks: 0, played_percentage: 0, last_played_date: new Date().toISOString() });
      console.log(`[trakt-history-push] handler dispatching pushHistoryAdd for ${itemId} (via emby:user:mark-played)`);
      void traktSync.pushHistoryAdd(itemId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:user:mark-unplayed', async (_, { itemId }: { itemId: string }) => {
    console.log(`[emby:user:mark-unplayed] handler entry, itemId=${itemId}`);
    try {
      await embyClient.markUnplayed(itemId);
      updateItemUserData(itemId, { played: 0, play_count: 0 });
      void traktSync.pushHistoryRemove(itemId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'emby:user:favorite',
    async (_, { itemId, isFavorite }: { itemId: string; isFavorite: boolean }) => {
      try {
        await embyClient.updateFavorite(itemId, isFavorite);
        return ok(undefined);
      } catch (e) {
        return fail(e);
      }
    },
  );

  // ── Item actions (cross-server aware) ─────────────────
  ipcMain.handle('item:mark-played', async (_, { itemId, serverId }: { itemId: string; serverId?: string }) => {
    console.log(`[item:mark-played] handler entry, itemId=${itemId}, serverId=${serverId ?? '-'}`);
    try {
      const activeServer = serverManager.getActiveServer();
      if (!serverId || serverId === activeServer?.id) {
        await embyClient.markPlayed(itemId);
      } else {
        const server = serverManager.getServer(serverId);
        if (server) {
          await embyClient.markPlayedOnServer(server.url, server.accessToken, server.userId, itemId);
        }
      }
      updateItemUserData(itemId, { played: 1, playback_position_ticks: 0, played_percentage: 0, last_played_date: new Date().toISOString() });
      // Push to Trakt (silent no-op if not connected / sync disabled).
      // Cross-server cascade is naturally deduped on Trakt's side via tmdb id.
      // Fire-and-forget so the IPC response isn't gated on Trakt's RTT.
      console.log(`[trakt-history-push] handler dispatching pushHistoryAdd for ${itemId}`);
      void traktSync.pushHistoryAdd(itemId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('item:mark-unplayed', async (_, { itemId, serverId }: { itemId: string; serverId?: string }) => {
    try {
      const activeServer = serverManager.getActiveServer();
      if (!serverId || serverId === activeServer?.id) {
        await embyClient.markUnplayed(itemId);
      } else {
        const server = serverManager.getServer(serverId);
        if (server) {
          await embyClient.markUnplayedOnServer(server.url, server.accessToken, server.userId, itemId);
        }
      }
      updateItemUserData(itemId, { played: 0, play_count: 0 });
      void traktSync.pushHistoryRemove(itemId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'item:toggle-favorite',
    async (_, { itemId, serverId, isFavorite }: { itemId: string; serverId?: string; isFavorite: boolean }) => {
      try {
        const activeServer = serverManager.getActiveServer();
        if (!serverId || serverId === activeServer?.id) {
          await embyClient.updateFavorite(itemId, isFavorite);
        } else {
          const server = serverManager.getServer(serverId);
          if (server) {
            await embyClient.updateFavoriteOnServer(server.url, server.accessToken, server.userId, itemId, isFavorite);
          }
        }
        updateItemUserData(itemId, { is_favorite: isFavorite ? 1 : 0 });
        return ok(undefined);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('item:remove-from-continue', async (_, { itemId }: { itemId: string; serverId?: string }) => {
    try {
      const targets = dbGetResumeClearTargets(itemId);
      if (targets.length === 0) return ok(undefined);

      const activeServer = serverManager.getActiveServer();
      await Promise.all(targets.map(async (t) => {
        try {
          if (t.server_id === activeServer?.id) {
            await embyClient.markPlayed(t.emby_id);
          } else {
            const server = serverManager.getServer(t.server_id);
            if (server) {
              await embyClient.markPlayedOnServer(server.url, server.accessToken, server.userId, t.emby_id);
            }
          }
        } catch (err) {
          console.warn(`[remove-from-continue] ${t.server_id}/${t.emby_id} failed:`, err);
        }
        updateItemUserData(t.emby_id, { played: 1, playback_position_ticks: 0, played_percentage: 0, last_played_date: new Date().toISOString() });
      }));
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Search ────────────────────────────────────────────
  ipcMain.handle(
    'emby:search:query',
    async (_, { term, filters }: { term: string; filters?: Record<string, unknown> }) => {
      try {
        const results = await embyClient.search(term, filters);
        return ok(results);
      } catch (e) {
        return fail(e);
      }
    },
  );

  // ── Image ─────────────────────────────────────────────
  ipcMain.handle(
    'emby:image:get-url',
    async (
      _,
      {
        itemId,
        imageType,
        params,
      }: { itemId: string; imageType: string; params?: Record<string, unknown> },
    ) => {
      try {
        const url = embyClient.getImageUrl(itemId, imageType, params);
        return ok(url);
      } catch (e) {
        return fail(e);
      }
    },
  );

  // ── Player (mpv idle mode) ─────────────────────────────
  type PlaybackSessionInfo = {
    itemId: string;
    mediaSourceId: string;
    playSessionId: string;
    serverId: string | null;
    itemType: string | null;
    seriesId: string | null;
    seasonId: string | null;
    seasonNumber: number | null;
    episodeNumber: number | null;
  };

  // Resolve which server a playback session should report to. Null means
  // the item lives on the active server — use the default embyClient paths.
  function foreignServerFor(serverId: string | null | undefined) {
    if (!serverId) return null;
    const active = serverManager.getActiveServer();
    if (!active || serverId === active.id) return null;
    return serverManager.getServer(serverId) ?? null;
  }

  class PlaybackSession {
    private interval: ReturnType<typeof setInterval> | null = null;
    private session: PlaybackSessionInfo | null = null;
    private _lastPosition = 0;
    private _durationSec = 0;
    private _lastPaused: boolean | null = null;
    private _transitioning = false;
    private _advanceInProgress = false;
    private _sessionSeq = 0;

    get current() { return this.session; }
    get lastPosition() { return this._lastPosition; }
    get durationSec() { return this._durationSec; }
    get transitioning() { return this._transitioning; }
    set transitioning(v: boolean) { this._transitioning = v; }
    get advanceInProgress() { return this._advanceInProgress; }
    set advanceInProgress(v: boolean) { this._advanceInProgress = v; }

    start(
      itemId: string,
      mediaSourceId: string,
      playSessionId: string,
      startPosSec: number,
      durationSec: number,
    ): void {
      this.stopReporting();
      this._sessionSeq++;
      const cached = dbGetItem(itemId);
      this.session = {
        itemId,
        mediaSourceId,
        playSessionId,
        serverId: cached?.server_id ?? null,
        itemType: cached?.type ?? null,
        seriesId: cached?.series_id ?? null,
        seasonId: cached?.season_id ?? null,
        seasonNumber: cached?.season_number ?? null,
        episodeNumber: cached?.episode_number ?? null,
      };
      this._lastPosition = startPosSec;
      this._durationSec = durationSec;
      this._lastPaused = false;
      const seq = this._sessionSeq;
      this.interval = setInterval(async () => {
        if (seq !== this._sessionSeq) { this.stopReporting(); return; }
        if (!mpvManager.running()) { this.stopReporting(); return; }
        try {
          const pos = (await mpvManager.getProperty('time-pos')) as number | null;
          const paused = (await mpvManager.getProperty('pause')) as boolean;
          if (pos != null) this._lastPosition = pos;
          // Backfill duration from mpv if cache lookup returned 0
          if (!this._durationSec) {
            const dur = (await mpvManager.getProperty('duration')) as number | null;
            if (dur != null && dur > 0) this._durationSec = dur;
          }
          const progressPayload = {
            ItemId: itemId,
            MediaSourceId: mediaSourceId,
            PlaySessionId: playSessionId,
            PositionTicks: Math.floor((pos || 0) * 10_000_000),
            IsPaused: paused,
            CanSeek: true,
            PlayMethod: 'DirectPlay',
          };
          const progressServer = foreignServerFor(this.session?.serverId);
          if (progressServer) {
            await embyClient.reportPlaybackProgressToServer(progressServer.url, progressServer.accessToken, progressPayload);
          } else {
            await embyClient.reportPlaybackProgress(progressPayload);
          }
          // Trakt scrobble: detect pause / resume edges
          if (this._lastPaused !== null && paused !== this._lastPaused) {
            const action = paused ? 'pause' : 'start';
            void traktScrobbler.scrobble(action, itemId, this._lastPosition, this._durationSec);
          }
          this._lastPaused = paused;
        } catch { /* ignore */ }
      }, 10_000);
    }

    take(): typeof this.session {
      const s = this.session;
      this.session = null;
      return s;
    }

    stopReporting(): void {
      if (this.interval) { clearInterval(this.interval); this.interval = null; }
    }

    clear(): void {
      this.stopReporting();
      this.session = null;
    }
  }
  const playback = new PlaybackSession();

  // ── Episode nav helpers ─────────────────────────────────
  // Parse the first MediaSource id from the cached items.media_sources JSON.
  function firstMediaSourceId(item: { media_sources: string | null } | null | undefined): string | null {
    if (!item?.media_sources) return null;
    try {
      const arr = JSON.parse(item.media_sources) as Array<{ Id?: string }>;
      return arr?.[0]?.Id ?? null;
    } catch {
      return null;
    }
  }

  // Push episode-nav context to the Lua scripts so nocturne_nav.lua knows the
  // current adjacents (for keybinds + OSD) and modernz.lua can flip its
  // playlist-prev/next buttons into "episode prev/next" mode.
  function pushNavContextToMpv(itemId: string | null): void {
    // [DEBUG]
    console.log('[push-nav-context] itemId:', itemId, '| mpv.running:', mpvManager.running());
    if (!mpvManager.running()) return;
    let hasPrev = false;
    let hasNext = false;
    let prevTitle = '';
    let nextTitle = '';
    if (itemId) {
      const item = dbGetItem(itemId);
      console.log('[push-nav-context] cached item type:', item?.type);
      if (item?.type === 'Episode') {
        const adj = dbGetAdjacentEpisodes(itemId);
        if (adj.prev) {
          hasPrev = true;
          prevTitle = formatEpisodeLabel(adj.prev);
        }
        if (adj.next) {
          hasNext = true;
          nextTitle = formatEpisodeLabel(adj.next);
        }
      }
    }
    console.log('[push-nav-context] sending hasPrev=', hasPrev, 'hasNext=', hasNext);
    const ctx = JSON.stringify({ hasPrev, hasNext, prevTitle, nextTitle });
    mpvManager
      .command(['script-message-to', 'nocturne_nav', 'nocturne-set-context', ctx])
      .catch(() => { /* nocturne_nav may not be loaded yet — best effort */ });
    mpvManager
      .command([
        'script-message-to',
        'modernz',
        'nocturne-episode-nav',
        hasPrev ? 'true' : 'false',
        hasNext ? 'true' : 'false',
      ])
      .catch(() => { /* modernz may not be loaded — best effort */ });
  }

  function formatEpisodeLabel(item: import('./database').ItemRow): string {
    const sn = item.season_number ?? 0;
    const en = item.episode_number ?? 0;
    const code = `S${String(sn).padStart(2, '0')}E${String(en).padStart(2, '0')}`;
    return item.name ? `${code} — ${item.name}` : code;
  }

  // Fetch intro/recap/credits/preview segments from TheIntroDB and push them
  // to the nocturne_skip Lua script. Fire-and-forget — never blocks playback
  // start. For episodes the SHOW tmdb id is needed (items.tmdb_id on an
  // Episode row is the *episode* tmdb id), so we walk back through series_id.
  async function pushSegmentsToMpv(
    itemId: string,
    durationSec: number,
    hasNext: boolean,
  ): Promise<void> {
    console.log('[introdb] pushSegmentsToMpv called for itemId:', itemId);
    try {
      const item = dbGetItem(itemId);
      if (!item) {
        console.log('[introdb] skip: item not in cache for', itemId);
        return;
      }
      console.log('[introdb] item type:', item.type, 'series_id:', item.series_id, 'imdb_id:', item.imdb_id);

      let imdbId: string | null = null;
      let tmdbId: number | undefined;
      let kind: 'movie' | 'show' = 'movie';
      let seasonNum: number | undefined;
      let episodeNum: number | undefined;

      if (item.type === 'Episode') {
        if (!item.series_id) {
          console.log('[introdb] skip: episode has no series_id');
          return;
        }
        const series = dbGetItem(item.series_id);
        console.log('[introdb] series row:', series ? `name=${series.name} imdb_id=${series.imdb_id} tmdb_id=${series.tmdb_id}` : 'not found');
        if (!series?.imdb_id) {
          console.log('[introdb] skip: series imdb_id missing for series_id', item.series_id);
          return;
        }
        imdbId = series.imdb_id;
        tmdbId = series.tmdb_id ? parseInt(series.tmdb_id, 10) : undefined;
        kind = 'show';
        seasonNum = item.season_number ?? undefined;
        episodeNum = item.episode_number ?? undefined;
      } else if (item.type === 'Movie') {
        if (!item.imdb_id) {
          console.log('[introdb] skip: movie imdb_id missing');
          return;
        }
        imdbId = item.imdb_id;
        tmdbId = item.tmdb_id ? parseInt(item.tmdb_id, 10) : undefined;
        kind = 'movie';
      } else {
        console.log('[introdb] skip: unsupported type', item.type);
        return;
      }
      if (!imdbId) return;

      console.log('[introdb] fetching segments:', { imdbId, kind, seasonNum, episodeNum, tmdbId });
      const data = await fetchSegments(imdbId, kind, seasonNum, episodeNum, tmdbId);
      console.log('[introdb] fetch result:', data ? 'data received' : 'null', 'mpv running:', mpvManager.running());
      if (!data || !mpvManager.running()) return;

      const settings = getSettings();

      // The API returns a single object per type (or null), and uses "outro"
      // for what the Lua/settings call "credits". Wrap into the segments
      // array shape the Lua script expects.
      const toSegments = (
        seg: { start_sec: number | null; end_sec: number | null; start_ms?: number | null; end_ms?: number | null } | null | undefined,
      ): Array<{ start_sec: number; end_sec: number }> => {
        if (!seg) return [];
        const startSec = seg.start_sec ?? (seg.start_ms != null ? seg.start_ms / 1000 : null);
        const endSec = seg.end_sec ?? (seg.end_ms != null ? seg.end_ms / 1000 : null);
        if (startSec == null || endSec == null || endSec <= startSec) return [];
        return [{ start_sec: startSec, end_sec: endSec }];
      };

      const payload: Record<string, unknown> = {
        has_next: hasNext,
        intro: { segments: toSegments(data.intro), mode: settings.skipIntroMode || 'off' },
        recap: { segments: toSegments(data.recap), mode: settings.skipRecapMode || 'off' },
        credits: { segments: toSegments(data.outro), mode: settings.skipCreditsMode || 'off' },
      };

      console.log('[introdb] pushing payload to mpv:', JSON.stringify(payload));
      mpvManager
        .command(['script-message-to', 'nocturne_skip', 'nocturne-segments', JSON.stringify(payload)])
        .catch(() => { /* nocturne_skip may not be loaded yet — best effort */ });
    } catch (e) {
      console.error('[introdb] pushSegmentsToMpv failed:', e);
    }
  }

  // Per-session memo of items we've already auto-searched subtitles for.
  // Prevents retrying on failure (and on rewinds / pause / resume) within
  // the same Nocturne process.
  const autoSubAttempted = new Set<string>();

  // Auto-download a subtitle from OpenSubtitles when the user has enabled it
  // and the playing file lacks a track in their preferred language. The
  // open_subtitles.lua script handles the actual API call + sub-add; we just
  // gate, resolve the language code, and dispatch.
  async function maybeAutoDownloadSubtitles(itemId: string): Promise<void> {
    const settings = getSettings();
    if (!settings.autoDownloadSubtitles) return;
    if (autoSubAttempted.has(itemId)) return;
    autoSubAttempted.add(itemId);

    const lang639_2B = (settings.preferredSubtitleLanguage || 'eng').toLowerCase();
    if (!lang639_2B || lang639_2B === 'none') return;
    const lang639_1 = toIso6391(lang639_2B);
    if (!lang639_1) return;

    // mpv needs a beat to parse tracks before track-list is populated.
    await new Promise((r) => setTimeout(r, 4000));
    if (!mpvManager.running()) return;

    try {
      const tracks = (await mpvManager.getProperty('track-list')) as
        | Array<{ type?: string; lang?: string }>
        | null;
      if (Array.isArray(tracks)) {
        for (const t of tracks) {
          if (t?.type !== 'sub') continue;
          const tl = (t.lang || '').toLowerCase();
          if (tl === lang639_1 || tl === lang639_2B) return;
        }
      }
    } catch {
      /* fall through and try anyway */
    }

    mpvManager
      .command(['script-message-to', 'open_subtitles', 'nocturne-auto-search', lang639_1])
      .catch(() => { /* best effort */ });
  }

  // Shared advance logic: load adjacent episode in the existing mpv process
  // without going through the show-main-window / hide-mpv cleanup. Returns
  // true if it advanced, false if no adjacent episode in that direction.
  async function advanceToEpisode(direction: 'next' | 'prev'): Promise<boolean> {
    // [DEBUG]
    console.log('[episode-nav] advanceToEpisode called, direction:', direction, '| cur:', JSON.stringify(playback.current));
    const cur = playback.current;
    if (!cur || cur.itemType !== 'Episode') {
      console.log('[episode-nav] abort — no current session or itemType is not Episode');
      return false;
    }
    const adj = dbGetAdjacentEpisodes(cur.itemId);
    console.log('[episode-nav] adjacents:', JSON.stringify({ prev: adj.prev?.emby_id, next: adj.next?.emby_id }));
    const target = direction === 'next' ? adj.next : adj.prev;
    if (!target) {
      console.log('[episode-nav] abort — no adjacent episode in direction', direction);
      return false;
    }

    const targetServer = target.server_id ? serverManager.getServer(target.server_id) : null;
    if (!targetServer) {
      console.warn(`[player:advance] no server config for ${target.server_id} — abort`);
      return false;
    }
    const mediaSourceId = firstMediaSourceId(target);
    if (!mediaSourceId) {
      console.warn(`[player:advance] no media source for ${target.emby_id} — abort`);
      return false;
    }

    const finalPositionTicks = Math.floor((playback.lastPosition || 0) * 10_000_000);
    const finalPositionSec = playback.lastPosition || 0;
    const finalDurationSec = playback.durationSec;
    const oldSession = playback.current;

    // Set BEFORE any loadfile-replace so the synthetic end-file{reason:"stop"}
    // that mpv emits for the old file is suppressed by the end-file handler.
    playback.advanceInProgress = true;

    // Hold advanceInProgress=true until mpv emits file-loaded for the new
    // file. mpv emits events in order on a loadfile-replace:
    //   end-file{stop} (old) → start-file → file-loaded (new).
    // The end-file handler's suppression check is synchronous, so once
    // file-loaded fires the synthetic stop is guaranteed to have been
    // observed and suppressed. Without this gate, the flag clears the
    // moment loadFile() resolves (mpv only ACKs the queued command, not the
    // teardown), the queued end-file{stop} arrives later, and the cleanup
    // path runs — showing the main window and de-fullscreening mpv.
    const oldFileSuppressed = new Promise<void>((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        mpvManager.off('file-loaded', onLoaded);
        clearTimeout(timer);
        resolve();
      };
      const onLoaded = () => finish();
      const timer = setTimeout(() => {
        console.warn('[episode-nav] file-loaded timeout — clearing advance flag defensively');
        finish();
      }, 5000);
      mpvManager.on('file-loaded', onLoaded);
    });

    try {
      // 1. Tell Emby + Trakt the current episode stopped (best-effort, non-blocking).
      if (oldSession) {
        embyClient
          .reportPlaybackStopped({
            ItemId: oldSession.itemId,
            MediaSourceId: oldSession.mediaSourceId,
            PlaySessionId: oldSession.playSessionId,
            PositionTicks: finalPositionTicks,
          })
          .catch(() => { /* ignore */ });
        void traktScrobbler.scrobble('stop', oldSession.itemId, finalPositionSec, finalDurationSec);
      }

      // 2. Build target stream URL for the (possibly different) server.
      const url = embyClient.getStreamUrlForServer(
        targetServer.url,
        targetServer.accessToken,
        target.emby_id,
        mediaSourceId,
      );
      const playSessionId = `nocturne-${Date.now()}`;
      const titleParts: string[] = [];
      if (target.series_name) titleParts.push(target.series_name);
      titleParts.push(formatEpisodeLabel(target));
      const itemName = titleParts.join(' — ');

      // 3. Hot-load into the existing mpv process. mpv emits end-file{stop}
      //    for the old file during this — suppressed by the guard.
      await mpvManager.loadFile(url, { startPositionTicks: 0, title: itemName });

      // 4. Tell Emby + Trakt the new episode is starting.
      embyClient
        .reportPlaybackStart({
          ItemId: target.emby_id,
          MediaSourceId: mediaSourceId,
          PlaySessionId: playSessionId,
          PositionTicks: 0,
          CanSeek: true,
          PlayMethod: 'DirectPlay',
        })
        .catch(() => { /* ignore */ });

      const durationSec =
        target.runtime_ticks && target.runtime_ticks > 0 ? target.runtime_ticks / 10_000_000 : 0;
      playback.start(target.emby_id, mediaSourceId, playSessionId, 0, durationSec);
      void traktScrobbler.scrobble('start', target.emby_id, 0, durationSec);

      // 5. Push refreshed adjacency to mpv so the OSC buttons update.
      pushNavContextToMpv(target.emby_id);

      // 5b. Refresh skip-segment data for the new episode. Fire-and-forget;
      //     mpv keeps playing while we wait on TheIntroDB.
      console.log('[introdb] CALL SITE REACHED (advanceToEpisode) itemId:', target.emby_id);
      let targetAdj: { next: unknown; prev: unknown } = { next: null, prev: null };
      try {
        targetAdj = dbGetAdjacentEpisodes(target.emby_id);
      } catch (adjErr) {
        console.error('[introdb] dbGetAdjacentEpisodes threw (advanceToEpisode):', adjErr);
      }
      void pushSegmentsToMpv(target.emby_id, durationSec, targetAdj.next != null);

      // 5c. Auto-download subtitles if enabled and the new file lacks a
      //     preferred-language track.
      void maybeAutoDownloadSubtitles(target.emby_id);

      // 6. Notify renderer so any in-progress player UI / store can sync.
      const w = getMainWindow();
      if (w && !w.isDestroyed()) {
        w.webContents.send('player:now-playing', {
          itemId: target.emby_id,
          serverId: targetServer.id,
          seriesId: target.series_id,
          seasonNumber: target.season_number,
          episodeNumber: target.episode_number,
          name: target.name,
          seriesName: target.series_name,
        });
      }

      // 7. Wait for mpv to confirm the new file is loaded. By the time
      //    file-loaded fires, the synthetic end-file{stop} for the old file
      //    has already been observed (and suppressed) by the end-file
      //    handler — so it's safe to clear the advance flag.
      await oldFileSuppressed;

      return true;
    } finally {
      playback.advanceInProgress = false;
    }
  }

  // Persistent listener: when mpv file ends (ESC/q = stop, or file finishes)
  mpvManager.on('end-file', async (data: unknown) => {
    // [DEBUG]
    console.log('[end-file] raw payload:', JSON.stringify(data));
    const reason = (data as { reason?: string })?.reason;
    console.log(
      '[end-file] reason resolved to:', reason,
      '| current itemType:', playback.current?.itemType,
      '| advanceInProgress:', playback.advanceInProgress,
    );
    // Redirect is handled internally by mpv — ignore
    if (reason === 'redirect') return;
    // Suppress the synthetic stop event mpv emits when advanceToEpisode does
    // a loadfile-replace. Without this guard, the cleanup path runs in
    // parallel with the advance flow and undoes its window state changes.
    if (reason === 'stop' && playback.advanceInProgress) {
      console.log('[end-file] suppressed during episode advance');
      return;
    }

    // Playback error — notify renderer
    if (reason === 'error') {
      const w = getMainWindow();
      if (w && !w.isDestroyed()) {
        w.webContents.send('player:playback-error', {
          message: 'Playback failed — file may be corrupt or codec unsupported',
        });
      }
    }

    // Natural EOF on an episode → try to auto-advance without leaving mpv. If
    // there's no next episode (last of season/series, or current isn't an
    // episode) advanceToEpisode returns false and we fall through to the
    // standard cleanup path. Errors during advance also fall through.
    const isNaturalEnd = reason === 'eof';
    if (isNaturalEnd && playback.current?.itemType === 'Episode' && getSettingValue('autoPlayNextEpisode') !== false) {
      try {
        if (await advanceToEpisode('next')) return;
      } catch (err) {
        console.warn('[player:advance] auto-advance failed, falling through to cleanup:', err);
      }
    }

    // 1. Show main window FIRST (it has black overlay from player:exited)
    const w = getMainWindow();
    if (w && !w.isDestroyed()) {
      w.webContents.send('player:exited');
      w.show();
      w.focus();
    }

    // 2. Let main window paint on screen
    await new Promise((r) => setTimeout(r, 100));

    // 3. THEN hide mpv behind the main window
    try {
      await mpvManager.setProperty('fullscreen', false);
      await mpvManager.setProperty('force-window', 'no');
    } catch {
      /* ignore */
    }

    // 4. Emby reporting (async, don't block)
    const finalPositionTicks = Math.floor((playback.lastPosition || 0) * 10_000_000);
    const finalPositionSec = playback.lastPosition || 0;
    const finalDurationSec = playback.durationSec;
    const session = playback.take();
    playback.stopReporting();
    if (session) {
      updateItemUserData(session.itemId, { last_played_date: new Date().toISOString() });
      {
        const stopPayload = {
          ItemId: session.itemId,
          MediaSourceId: session.mediaSourceId,
          PlaySessionId: session.playSessionId,
          PositionTicks: finalPositionTicks,
        };
        const stopServer = foreignServerFor(session.serverId);
        (stopServer
          ? embyClient.reportPlaybackStoppedToServer(stopServer.url, stopServer.accessToken, stopPayload)
          : embyClient.reportPlaybackStopped(stopPayload)
        ).catch(() => {
          /* ignore */
        });
      }

      // Trakt scrobble:stop fires once per playback (regardless of dedup cascade).
      // Trakt auto-marks watched at >= 80% on stop; below that it's discarded.
      void traktScrobbler.scrobble('stop', session.itemId, finalPositionSec, finalDurationSec);

      // Cross-server: mark played on other servers that have this item
      if (serverManager.isCombinedMode()) {
        try {
          const item = dbGetItem(session.itemId);
          if (item?.dedup_group_id) {
            const versions = dbGetGroupVersions(item.dedup_group_id, item);
            for (const version of versions) {
              if (version.server_id !== item.server_id) {
                const otherServer = serverManager.getServer(version.server_id);
                if (otherServer) {
                  embyClient
                    .markPlayedOnServer(
                      otherServer.url,
                      otherServer.accessToken,
                      otherServer.userId,
                      version.emby_id,
                    )
                    .catch(() => { /* best effort */ });
                }
              }
            }
          }
        } catch {
          /* best effort */
        }
      }
    }
  });

  // If mpv process dies unexpectedly, show main window
  mpvManager.on('process-exit', () => {
    playback.clear();
    const w = getMainWindow();
    if (w && !w.isDestroyed()) {
      w.show();
      w.focus();
      w.webContents.send('player:exited');
    }
  });

  // Natural end-of-file. Under keep-open=yes, mpv does NOT emit end-file at
  // EOF (it pauses on the last frame instead). Instead we observe the
  // `eof-reached` property which mpv flips to true at natural end. This is
  // the only signal we get for "the episode finished playing on its own."
  mpvManager.on('eof-reached', async () => {
    console.log('[eof-reached] fired | current itemType:', playback.current?.itemType);
    if (playback.advanceInProgress) {
      console.log('[eof-reached] ignored — advance already in progress');
      return;
    }
    const isEpisode = playback.current?.itemType === 'Episode';
    const autoplay = getSettingValue('autoPlayNextEpisode') !== false;
    if (isEpisode && autoplay) {
      try {
        if (await advanceToEpisode('next')) {
          // Reset eof-reached so the next episode's natural end fires the
          // observer again.
          await mpvManager.setProperty('eof-reached', 'no').catch(() => { /* ignore */ });
          return;
        }
      } catch (err) {
        console.warn('[eof-reached] auto-advance failed, falling through to cleanup:', err);
      }
    }

    // No next episode (last of season/series), not an episode, or advance
    // failed → run the same cleanup path the user-quit end-file handler does.
    const w = getMainWindow();
    if (w && !w.isDestroyed()) {
      w.webContents.send('player:exited');
      w.show();
      w.focus();
    }
    await new Promise((r) => setTimeout(r, 100));
    try {
      await mpvManager.setProperty('fullscreen', false);
      await mpvManager.setProperty('force-window', 'no');
      await mpvManager.setProperty('eof-reached', 'no');
    } catch {
      /* ignore */
    }

    const finalPositionTicks = Math.floor((playback.lastPosition || 0) * 10_000_000);
    const finalPositionSec = playback.lastPosition || 0;
    const finalDurationSec = playback.durationSec;
    const session = playback.take();
    playback.stopReporting();
    if (session) {
      updateItemUserData(session.itemId, { last_played_date: new Date().toISOString() });
      {
        const stopPayload = {
          ItemId: session.itemId,
          MediaSourceId: session.mediaSourceId,
          PlaySessionId: session.playSessionId,
          PositionTicks: finalPositionTicks,
        };
        const stopServer = foreignServerFor(session.serverId);
        (stopServer
          ? embyClient.reportPlaybackStoppedToServer(stopServer.url, stopServer.accessToken, stopPayload)
          : embyClient.reportPlaybackStopped(stopPayload)
        ).catch(() => { /* ignore */ });
      }
      void traktScrobbler.scrobble('stop', session.itemId, finalPositionSec, finalDurationSec);

      if (serverManager.isCombinedMode()) {
        try {
          const item = dbGetItem(session.itemId);
          if (item?.dedup_group_id) {
            const versions = dbGetGroupVersions(item.dedup_group_id, item);
            for (const version of versions) {
              if (version.server_id !== item.server_id) {
                const otherServer = serverManager.getServer(version.server_id);
                if (otherServer) {
                  embyClient
                    .markPlayedOnServer(
                      otherServer.url,
                      otherServer.accessToken,
                      otherServer.userId,
                      version.emby_id,
                    )
                    .catch(() => { /* best effort */ });
                }
              }
            }
          }
        } catch {
          /* best effort */
        }
      }
    }
  });

  // Manual prev/next from mpv-side Lua scripts (> / < keybind, OSC buttons).
  // Lua sends `mp.commandv("script-message", "nocturne-next")` which mpv
  // emits as `client-message` on the JSON-IPC socket.
  mpvManager.on('client-message', async (data: unknown) => {
    // [DEBUG]
    console.log('[client-message] raw payload:', JSON.stringify(data));
    const args = ((data as { args?: unknown[] })?.args || []).map((a) => String(a));
    const name = args[0];
    console.log('[client-message] parsed args:', args, '| dispatching name=', name);
    if (name === 'nocturne-next' || name === 'nocturne-prev') {
      const direction = name === 'nocturne-next' ? 'next' : 'prev';
      try {
        const advanced = await advanceToEpisode(direction);
        if (!advanced) {
          await mpvManager
            .command(['show-text', direction === 'next' ? 'No next episode' : 'No previous episode', '2000'])
            .catch(() => { /* ignore */ });
        }
      } catch (err) {
        console.warn(`[player:advance] manual ${direction} failed:`, err);
      }
    } else if (name === 'nocturne-context-request') {
      // Lua script just (re)loaded and is asking for current adjacency state.
      pushNavContextToMpv(playback.current?.itemId ?? null);
    } else if (name === 'nocturne-skip-debug') {
      // mpv runs with --really-quiet so mp.msg.warn from Lua is invisible.
      // Lua scripts route debug strings through script-message so they reach
      // the main-process console.
      console.log('[skip]', ...args.slice(1));
    } else if (name === 'nocturne-nav-debug') {
      console.log('[nav]', ...args.slice(1));
    }
  });

  ipcMain.handle(
    'player:play',
    async (
      _,
      {
        itemId,
        mediaSourceId,
        startPositionTicks,
        itemName,
        serverId,
      }: {
        itemId: string;
        mediaSourceId: string;
        startPositionTicks?: number;
        itemName?: string;
        serverId?: string;
      },
    ) => {
      if (!isNonEmptyString(itemId) || !isNonEmptyString(mediaSourceId)) return fail('Missing itemId or mediaSourceId');
      if (playback.transitioning) return fail('Playback transition already in progress');
      playback.transitioning = true;
      try {
        const win = getMainWindow();
        if (!win) return fail('No window');

        // Cross-server: a dedup version pick can live on a non-active
        // server. Resolve the stream + reporting against that server's
        // config; null means the active-server fast path.
        const activeServer = serverManager.getActiveServer();
        const foreignServer =
          serverId && serverId !== activeServer?.id ? serverManager.getServer(serverId) : null;
        if (serverId && serverId !== activeServer?.id && !foreignServer) {
          return fail(`Unknown server ${serverId}`);
        }

        // Trigger fade to black
        win.webContents.send('player:starting');

        // Wait for fade-in animation to complete (350ms transition + buffer)
        await new Promise((r) => setTimeout(r, 450));

        const url = foreignServer
          ? embyClient.getStreamUrlForServer(foreignServer.url, foreignServer.accessToken, itemId, mediaSourceId)
          : embyClient.getStreamUrl(itemId, mediaSourceId);
        const playSessionId = `nocturne-${Date.now()}`;

        // Load file into hot mpv — instant via IPC
        await mpvManager.loadFile(url, {
          startPositionTicks,
          title: itemName,
        });

        // Let mpv window appear, then hide main window behind it
        await new Promise((r) => setTimeout(r, 200));
        win.hide();

        // Force focus on mpv window by PID (no user-controlled strings in commands)
        const mpvPid = mpvManager.pid();
        if (mpvPid) {
          const psScript = `Start-Sleep -Milliseconds 100; $p = Get-Process -Id ${Number(mpvPid)}; if ($p) { (New-Object -ComObject WScript.Shell).AppActivate($p.Id) }`;
          const encoded = Buffer.from(psScript, 'utf16le').toString('base64');
          execFile('powershell', ['-NoProfile', '-EncodedCommand', encoded], (err) => {
            if (err) console.log('[mpv] AppActivate failed, user may need to click mpv window');
          });
        }

        // Report playback start to Emby (to the server that owns the item)
        const startPayload = {
          ItemId: itemId,
          MediaSourceId: mediaSourceId,
          PlaySessionId: playSessionId,
          PositionTicks: startPositionTicks || 0,
          CanSeek: true,
          PlayMethod: 'DirectPlay',
        };
        if (foreignServer) {
          await embyClient.reportPlaybackStartToServer(foreignServer.url, foreignServer.accessToken, startPayload);
        } else {
          await embyClient.reportPlaybackStart(startPayload);
        }

        // Resolve duration for Trakt progress percentage. Cache is the fast
        // path; mpv's `duration` property is the fallback inside the polling
        // loop once playback is rolling.
        const cached = dbGetItem(itemId);
        const durationSec =
          cached?.runtime_ticks && cached.runtime_ticks > 0
            ? cached.runtime_ticks / 10_000_000
            : 0;
        const startPosSec = (startPositionTicks || 0) / 10_000_000;

        playback.start(itemId, mediaSourceId, playSessionId, startPosSec, durationSec);

        // Trakt scrobble:start (fires once per play). Silent no-op if not connected.
        void traktScrobbler.scrobble('start', itemId, startPosSec, durationSec);

        // Tell mpv-side Lua scripts what the adjacent episodes are. Pushes
        // hasNext=hasPrev=false for movies, which hides the OSC buttons.
        pushNavContextToMpv(itemId);

        // Push skip-segment data (intro / recap / credits / preview) to the
        // nocturne_skip Lua script. Fire-and-forget — TheIntroDB lookup must
        // never block playback start. Movies pass hasNext=false; episodes
        // resolve adjacency from the cache.
        console.log('[introdb] CALL SITE REACHED (player:play) itemId:', itemId, 'cached type:', cached?.type);
        const isEpisode = cached?.type === 'Episode';
        let adj: { next: unknown; prev: unknown } | null = null;
        try {
          adj = isEpisode ? dbGetAdjacentEpisodes(itemId) : null;
        } catch (adjErr) {
          console.error('[introdb] dbGetAdjacentEpisodes threw:', adjErr);
        }
        console.log('[introdb] about to call pushSegmentsToMpv, adj.next:', adj?.next != null);
        void pushSegmentsToMpv(itemId, durationSec, adj?.next != null);

        // Auto-download subtitles via OpenSubtitles if enabled and the file
        // lacks a track in the user's preferred language.
        void maybeAutoDownloadSubtitles(itemId);

        return ok(undefined);
      } catch (e) {
        console.error('[player:play] handler threw:', e);
        const win = getMainWindow();
        if (win && !win.isDestroyed()) {
          win.show();
          win.focus();
          win.webContents.send('player:start-failed');
        }
        return fail(e);
      } finally {
        playback.transitioning = false;
      }
    },
  );

  ipcMain.handle('player:stop', async () => {
    try {
      playback.stopReporting();
      await mpvManager.stopPlayback();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Session restore helper ────────────────────────────
  ipcMain.handle(
    'emby:auth:restore',
    async (
      _,
      { serverUrl, token, userId }: { serverUrl: string; token: string; userId: string },
    ) => {
      if (!isValidUrl(serverUrl)) return fail('Invalid server URL');
      if (!isNonEmptyString(token) || !isNonEmptyString(userId)) return fail('Missing token or userId');
      try {
        embyClient.setServer(serverUrl);
        embyClient.setAuth(token, userId);
        const user = await embyClient.getCurrentUser();
        return ok(user);
      } catch (e) {
        embyClient.clearAuth();
        return fail(e);
      }
    },
  );

  // ── Sync ─────────────────────────────────────────────
  ipcMain.handle('sync:start-full', async () => {
    try {
      syncEngine.startFullSync();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('sync:start-incremental', async () => {
    try {
      syncEngine.startIncrementalSync();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('sync:cancel', () => {
    syncEngine.cancel();
    return ok(undefined);
  });

  ipcMain.handle('sync:get-status', () => {
    try {
      return ok(syncEngine.getStatus());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('sync:auto-start', () => {
    const syncStatus = getSyncState('syncStatus') || 'never';
    if (syncStatus === 'complete') {
      console.log('[sync] Previous sync complete, starting incremental sync');
      syncEngine.startIncrementalSync();
    } else {
      console.log(`[sync] syncStatus=${syncStatus}, starting full sync`);
      syncEngine.startFullSync();
    }
    return ok(undefined);
  });

  // Forward sync events to renderer
  syncEngine.on('progress', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) {
      w.webContents.send('sync:progress', data);
    }
  });
  syncEngine.on('complete', () => {
    checkpoint();
    invalidateTraktWatchlistCache();
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('sync:complete');
  });
  syncEngine.on('error', (err: Error) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('sync:error', { message: err.message });
  });
  syncEngine.on('dedup-complete', (result: { groupsCreated: number; itemsMerged: number }) => {
    invalidateTraktWatchlistCache();
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('dedup:complete', result);
  });
  syncEngine.on('dedup-failed', (err: { message: string }) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('dedup:error', err);
  });

  // ── Cache ────────────────────────────────────────────
  ipcMain.handle('cache:get-item', async (_, { itemId }: { itemId: string }) => {
    try {
      const cached = dbGetItem(itemId);
      if (cached) {
        attachFallbacksToItems([cached]);
        return ok(cached);
      }
      // Fall back to API
      const item = await embyClient.getItem(itemId);
      return ok(item);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-library-items', (_, { filters }: { filters: ItemFilters }) => {
    try {
      const result = dbGetItems(filters);
      attachFallbacksToItems(result.items);
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-resume-items', () => {
    try {
      const items = dbGetResumeItemsDeduped();
      attachFallbacksToItems(items);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-latest-items', (_, { libraryId, limit }: { libraryId: string; limit?: number }) => {
    try {
      const items = dbGetLatestItems(libraryId, limit);
      attachFallbacksToItems(items);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:search', (_, { query }: { query: string }) => {
    try {
      const items = dbSearchItemsDeduped(query);
      attachFallbacksToItems(items);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:resolve-dedup-groups', (_, { ids }: { ids: string[] }) => {
    try {
      return ok(dbGetDedupGroupsForItemIds(ids));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-stats', () => {
    try {
      return ok(dbGetStats());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:clear', () => {
    try {
      dbClearItemsAndDedup();
      const cleared = dbClearEpisodeSyncMarkers();
      console.log(`[sync] cache:clear — removed ${cleared} episodes_synced_* markers`);
      setSyncState('syncStatus', 'never');
      deleteSyncState('syncCheckpoint');
      deleteSyncState('syncCheckpoint_servers');
      deleteSyncState('dedupStatus');
      deleteSyncState('lastDedupBuild');
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('app:reset-full', async () => {
    console.log('[reset] Full app reset requested');
    try {
      // 1. Cancel any running sync
      try { syncEngine.cancel(); } catch (err) { console.warn('[reset] syncEngine.cancel failed:', err); }

      // 2. Quit mpv
      try { await mpvManager.quit(); } catch (err) { console.warn('[reset] mpvManager.quit failed:', err); }

      // 3. Close DB so Windows releases its file locks
      try { dbCloseDatabase(); } catch (err) { console.warn('[reset] closeDatabase failed:', err); }

      // 4. Delete user data files/dirs
      const userData = app.getPath('userData');
      const targets = [
        path.join(userData, 'nocturne.db'),
        path.join(userData, 'nocturne.db-wal'),
        path.join(userData, 'nocturne.db-shm'),
        path.join(userData, 'nocturne-settings.json'),
        path.join(userData, 'image-cache'),
      ];
      for (const t of targets) {
        try {
          fs.rmSync(t, { recursive: true, force: true });
          console.log(`[reset] removed ${t}`);
        } catch (err) {
          console.warn(`[reset] failed to remove ${t}:`, err);
        }
      }

      // 5. Relaunch
      app.relaunch();
      app.exit(0);
      return ok(undefined);
    } catch (e) {
      console.error('[reset] fatal error:', e);
      return fail(e);
    }
  });

  ipcMain.handle('cache:has-data', () => {
    try {
      return ok(hasAnyCachedItems());
    } catch (e) {
      return fail(e);
    }
  });

  // ── Image Cache ──────────────────────────────────────
  ipcMain.handle('image:get-cached-url', (_, { url }: { url: string }) => {
    try {
      const cached = getCachedUrl(url);
      return ok(cached || url);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('image:precache', async (_, { urls }: { urls: string[] }) => {
    try {
      await precacheImages(urls);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Settings ─────────────────────────────────────────
  ipcMain.handle('settings:get', () => {
    try {
      return ok(getSettings());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('settings:get-value', (_, { key }: { key: string }) => {
    try {
      return ok(getSettingValue(key as keyof NocturneSettings));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('settings:set', (_, { key, value }: { key: string; value: unknown }) => {
    if (!isNonEmptyString(key) || !ALLOWED_SETTING_KEYS.has(key)) return fail('Invalid setting key');
    try {
      setSetting(key as keyof NocturneSettings, value as never);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('settings:set-multiple', (_, data: Partial<NocturneSettings>) => {
    try {
      const filtered: Partial<NocturneSettings> = {};
      for (const [key, value] of Object.entries(data)) {
        if (ALLOWED_SETTING_KEYS.has(key)) {
          (filtered as Record<string, unknown>)[key] = value;
        }
      }
      if (Object.keys(filtered).length > 0) {
        setMultipleSettings(filtered);
      }
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('settings:reset', () => {
    try {
      resetSettings();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Library Mapping ──────────────────────────────────
  ipcMain.handle('libraries:suggest-mapping', async () => {
    try {
      const views = await embyClient.getViews();
      const libraries = (views.Items || []).map((v: { Id: string; Name: string; CollectionType?: string }) => ({
        id: v.Id,
        name: v.Name,
        type: v.CollectionType || '',
      }));
      return ok(suggestLibraryMapping(libraries));
    } catch (e) {
      return fail(e);
    }
  });

  // ── Virtual Libraries ────────────────────────────────
  ipcMain.handle('vlib:get-all', () => {
    try {
      return ok(getVirtualLibraries());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('vlib:get-items', (_, args: {
    vlibId: string;
    startIndex?: number;
    limit?: number;
    sortBy?: string;
    sortOrder?: string;
    searchTerm?: string;
    itemType?: string;
  }) => {
    try {
      // Trakt watchlist sentinel — short-circuit before hitting SQL helpers.
      if (args.vlibId === TRAKT_WATCHLIST_VLIB_ID) {
        const all = getTraktWatchlistAsCachedItems();
        const filtered = args.itemType
          ? all.filter((i) => i.type === args.itemType)
          : all;
        const startIndex = args.startIndex || 0;
        const limit = args.limit || 40;
        return ok({
          items: filtered.slice(startIndex, startIndex + limit),
          total: filtered.length,
        });
      }
      const result = getVirtualLibraryItems(args.vlibId, {
        startIndex: args.startIndex || 0,
        limit: args.limit || 40,
        sortBy: args.sortBy || 'DateCreated',
        sortOrder: args.sortOrder || 'desc',
        searchTerm: args.searchTerm,
        itemType: args.itemType,
      });
      attachFallbacksToItems(result.items);
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('vlib:get-latest', (_, { vlibId, limit }: { vlibId: string; limit?: number }) => {
    try {
      if (vlibId === TRAKT_WATCHLIST_VLIB_ID) {
        return ok(getTraktWatchlistAsCachedItems().slice(0, limit || 20));
      }
      const items = getVirtualLibraryLatest(vlibId, limit || 20);
      attachFallbacksToItems(items);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('vlib:get-heroes', (_, { vlibId, limit }: { vlibId?: string; limit?: number }) => {
    try {
      const items = getVirtualLibraryHeroes(vlibId || null, limit || 20);
      attachFallbacksToItems(items);
      return ok(items);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Dedup ───────────────────────────────────────────
  ipcMain.handle('dedup:get-versions', (_, { itemId }: { itemId: string }) => {
    try {
      return ok(getItemVersions(itemId));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('dedup:get-episodes', (_, { seriesItemId, seasonNumber }: { seriesItemId: string; seasonNumber: number }) => {
    try {
      return ok(getSeriesEpisodeVersions(seriesItemId, seasonNumber));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('dedup:get-adjacent-episodes', (_, { episodeId }: { episodeId: string }) => {
    try {
      return ok(dbGetAdjacentEpisodes(episodeId));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('dedup:get-stats', () => {
    try {
      return ok(getVirtualLibraryDedupStats());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('dedup:rebuild', async () => {
    try {
      const result = await syncEngine.runDedup();
      if (result.success) return ok(result);
      return fail(new Error(result.error ?? 'Dedup failed'));
    } catch (e) {
      return fail(e);
    }
  });

  // ── Servers ─────────────────────────────────────────
  ipcMain.handle('servers:get-all', () => {
    try {
      return ok(serverManager.getServers());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:get-active', () => {
    try {
      return ok(serverManager.getActiveServer());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:add', (_, config: Omit<ServerConfig, 'id' | 'addedAt' | 'lastConnected'>) => {
    try {
      const server = serverManager.addServer(config);
      return ok(server);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:remove', (_, { serverId }: { serverId: string }) => {
    try {
      serverManager.removeServer(serverId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:switch', async (_, { serverId }: { serverId: string }) => {
    try {
      const success = await serverManager.switchServer(serverId);
      return ok(success);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:get-mappings', () => {
    try {
      return ok(serverManager.getActiveLibraryMappings());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:set-mappings', (_, { mappings }: { mappings: Record<string, { name: string; icon: string; libraryIds: string[] }> }) => {
    try {
      serverManager.setActiveLibraryMappings(mappings);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:get-library-mode', () => {
    try {
      return ok(serverManager.getLibraryMode());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:get-combined-mappings', () => {
    try {
      return ok(serverManager.getCombinedMappings());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:set-combined-mappings', (_, { mappings }: { mappings: Record<string, unknown> }) => {
    try {
      serverManager.setCombinedMappings(mappings as Record<string, import('./settings').CombinedMapping>);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('servers:get-all-libraries', () => {
    try {
      return ok(serverManager.getAllServerLibraries());
    } catch (e) {
      return fail(e);
    }
  });

  // ── Updater ─────────────────────────────────────────
  ipcMain.handle('updater:check', () => {
    try {
      checkForUpdates();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:download', () => {
    try {
      downloadUpdate();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:install', () => {
    try {
      installUpdate();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:get-status', () => {
    try {
      return ok(getUpdateStatus());
    } catch (e) {
      return fail(e);
    }
  });

  // ── Trakt ────────────────────────────────────────────
  ipcMain.handle('trakt:get-status', () => {
    try {
      return ok(traktClient.getStatus(traktScrobbler.getQueueCount()));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:auth-start', async () => {
    try {
      const data = await traktClient.startDeviceFlow();
      return ok(data);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:auth-poll', async (_, { deviceCode }: { deviceCode: string }) => {
    if (!isNonEmptyString(deviceCode)) return fail('Missing deviceCode');
    try {
      const state = await traktClient.pollDeviceFlow(deviceCode);
      return ok(state);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:disconnect', () => {
    try {
      traktClient.disconnect();
      clearTraktQueue();
      // Clear all Trakt-side mirrors so a reconnect (possibly to a different
      // account) starts from a clean slate. Fixes stale-watchlist visible in
      // the sidebar after disconnect.
      clearTraktWatched();
      clearTraktWatchlist();
      clearTraktRatings();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:drain-queue', async () => {
    try {
      await traktScrobbler.drainQueue();
      return ok({ remaining: traktScrobbler.getQueueCount() });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-queue-count', () => {
    try {
      return ok(traktScrobbler.getQueueCount());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-failed-queue-count', () => {
    try {
      return ok(traktScrobbler.getQueueCount());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:clear-failed-queue', () => {
    try {
      const cleared = traktScrobbler.clearQueue();
      return ok({ cleared });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-advanced-config', () => {
    try {
      return ok({
        clientIdOverride: (getSettingValue('traktClientIdOverride') as string) || '',
        clientSecretOverride: (getSettingValue('traktClientSecretOverride') as string) || '',
        bundledIdPresent: Boolean(TRAKT_BUNDLED_CLIENT_ID),
      });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'trakt:set-advanced-config',
    (_, { clientId, clientSecret }: { clientId: string; clientSecret: string }) => {
      try {
        setSetting('traktClientIdOverride', typeof clientId === 'string' ? clientId : '');
        setSetting('traktClientSecretOverride', typeof clientSecret === 'string' ? clientSecret : '');
        return ok(undefined);
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('trakt:open-verification', (_, { url }: { url: string }) => {
    if (!isValidUrl(url)) return fail('Invalid URL');
    try {
      shell.openExternal(url);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Trakt Phase 2/3/4 ─────────────────────────────

  ipcMain.handle('trakt:fetch-preview', async () => {
    console.log('[trakt-preview] handler entry');
    const t0 = Date.now();
    try {
      const preview = await traktSync.fetchInitialPreview();
      console.log(
        `[trakt-preview] handler resolved in ${Date.now() - t0}ms — `
          + `movies ${preview.movies.matchedInLibrary}/${preview.movies.totalOnTrakt}, `
          + `episodes ${preview.episodes.matchedInLibrary}/${preview.episodes.totalOnTrakt}`,
      );
      return ok(preview);
    } catch (e) {
      console.error(`[trakt-preview] handler FAILED after ${Date.now() - t0}ms:`, e);
      const err = e as { response?: { status?: number; data?: unknown }; code?: string; message?: string };
      if (err?.response) {
        console.error(
          `[trakt-preview]   HTTP status=${err.response.status} `
            + `body=${JSON.stringify(err.response.data).slice(0, 300)}`,
        );
      } else if (err?.code) {
        console.error(`[trakt-preview]   axios code=${err.code} message=${err.message}`);
      }
      return fail(e);
    }
  });

  ipcMain.handle('trakt:apply-watched-state', async (_, { embyIds }: { embyIds: string[] }) => {
    if (!Array.isArray(embyIds)) return fail('embyIds must be an array');
    try {
      const result = await traktSync.applyWatchedState(embyIds);
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:cancel-apply', () => {
    try {
      traktSync.cancelApply();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:sync-now', async () => {
    try {
      const [history, watchlist] = await Promise.all([
        traktSync.runBackgroundHistorySync(),
        traktSync.refreshWatchlist(),
      ]);
      return ok({ history, watchlist });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-stats', () => {
    try {
      return ok(traktSync.getStats());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-watchlist', () => {
    try {
      return ok(getTraktWatchlistAsCachedItems());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:refresh-watchlist', async () => {
    try {
      const result = await traktSync.refreshWatchlist();
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:add-to-watchlist', async (_, { itemId }: { itemId: string }) => {
    if (!isNonEmptyString(itemId)) return fail('Missing itemId');
    try {
      const result = await traktSync.addItemToWatchlist(itemId);
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'trakt:remove-from-watchlist',
    async (_, args: { itemId?: string; traktType?: 'movie' | 'show'; tmdbId?: string; key?: string }) => {
      try {
        if (args.itemId) {
          const result = await traktSync.removeItemFromWatchlist({ embyId: args.itemId });
          return ok(result);
        }
        if (args.traktType && args.tmdbId) {
          const result = await traktSync.removeItemFromWatchlist({
            traktType: args.traktType,
            tmdbId: args.tmdbId,
            key: args.key,
          });
          return ok(result);
        }
        return fail('Missing itemId or traktType/tmdbId');
      } catch (e) {
        return fail(e);
      }
    },
  );

  ipcMain.handle('trakt:in-watchlist', (_, { itemId }: { itemId: string }) => {
    try {
      return ok(traktSync.isInWatchlist(itemId));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:get-rating', async (_, { tmdbId, type }: { tmdbId: string; type: 'movie' | 'show' }) => {
    if (!isNonEmptyString(tmdbId)) return fail('Missing tmdbId');
    if (type !== 'movie' && type !== 'show') return fail('Invalid type');
    try {
      const result = await traktSync.getRating(tmdbId, type);
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('trakt:check-watched', (_, { tmdbId, type, season, episode }: { tmdbId: string; type: 'movie' | 'episode'; season?: number; episode?: number }) => {
    if (!isNonEmptyString(tmdbId)) return ok(false);
    try {
      if (type === 'movie') {
        return ok(isMovieWatchedOnTrakt(tmdbId));
      }
      if (type === 'episode' && typeof season === 'number' && typeof episode === 'number') {
        return ok(isEpisodeWatchedOnTrakt(tmdbId, season, episode));
      }
      return ok(false);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Analytics ────────────────────────────────────────
  ipcMain.handle(
    'analytics:get-stats',
    async (_, args: { rangeStart: string; rangeEnd: string; source?: 'local' | 'trakt' | 'combined' }) => {
      if (!isNonEmptyString(args?.rangeStart) || !isNonEmptyString(args?.rangeEnd)) {
        return fail('rangeStart and rangeEnd are required');
      }
      const t0 = Date.now();
      const source = args.source ?? 'local';
      console.log(`[analytics:get-stats] source=${source} range=${args.rangeStart}..${args.rangeEnd}`);
      try {
        // Lifetime block: Trakt /users/me/stats, cached 1h. Local mode skips
        // the call entirely so users without Trakt connected pay nothing.
        let lifetime: AnalyticsLifetimeBlock | null = null;
        if ((source === 'trakt' || source === 'combined') && traktClient.isConnected()) {
          const stats = await traktSync.getCachedUserStats();
          if (stats) {
            lifetime = {
              movies: stats.movies?.watched ?? 0,
              episodes: stats.episodes?.watched ?? 0,
              watchTimeMinutes: (stats.movies?.minutes ?? 0) + (stats.episodes?.minutes ?? 0),
              distinctShows: stats.shows?.watched ?? 0,
            };
          }
        }
        const result = computeAnalytics(
          { rangeStart: args.rangeStart, rangeEnd: args.rangeEnd },
          source,
          lifetime,
        );
        console.log(
          `[analytics:get-stats] ok (+${Date.now() - t0}ms) — totals=${result.totalWatched.movies}m/${result.totalWatched.episodes}e, `
            + `watchSec=${result.totalWatchTimeSeconds}, activityDays=${result.activityByDay.length}, `
            + `topSeries=${result.topSeries.length}, topMovies=${result.topMovies.length}, `
            + `genres=${result.genreBreakdown.length}, lifetime=${lifetime ? 'yes' : 'no'}, `
            + `unmatchedTrakt=${result.unmatchedTraktCount ?? 0}`,
        );
        return ok(result);
      } catch (e) {
        console.error(`[analytics:get-stats] FAILED after ${Date.now() - t0}ms:`, e);
        return fail(e);
      }
    },
  );

  ipcMain.handle('analytics:get-backfill-status', () => {
    try {
      return ok({
        backfilled: Boolean(getSettingValue('traktHistoryBackfilled')),
        cap: (getSettingValue('traktHistoryBackfillCap') as string | undefined) ?? 'two-years',
        eventCount: countTraktWatched(),
      });
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('analytics:trigger-backfill', async () => {
    if (!traktClient.isConnected()) return fail('Not connected to Trakt');
    try {
      const result = await traktSync.backfillHistory();
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  // ── Watch Party (v3.5) ────────────────────────────────
  // First-launch binary manager for ffmpeg + cloudflared. All work is lazy;
  // no setup runs until the renderer invokes watchparty:setup-binaries.
  ipcMain.handle('watchparty:binaries-ready', () => {
    try {
      return ok(watchPartyBinaryManager.isReady());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('watchparty:setup-binaries', async () => {
    try {
      const paths = await watchPartyBinaryManager.ensureBinaries();
      return ok(paths);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('watchparty:probe-encoder', async () => {
    try {
      return ok(await watchPartyEncoderProbe.probe());
    } catch (e) {
      return fail(e);
    }
  });

  // ── Watch Party — session-spawn pipeline (v3.5) ───────
  // The setup half (binary manager, encoder probe, pre-flight modal) is
  // upstream of this. These handlers drive the session manager state
  // machine: startSession → WAITING → startShow → LIVE → endSession.

  // IPC boundary input validation — the renderer sends a serialised
  // WatchPartySource and we trust the wire shape to nothing.
  function isValidWatchPartySource(s: unknown): s is WatchPartySource {
    if (!s || typeof s !== 'object') return false;
    const o = s as Record<string, unknown>;
    if (typeof o.title !== 'string') return false;
    if (!Array.isArray(o.versions) || o.versions.length === 0) return false;
    for (const v of o.versions) {
      if (!v || typeof v !== 'object') return false;
      const vv = v as Record<string, unknown>;
      if (typeof vv.serverId !== 'string') return false;
      if (typeof vv.itemId !== 'string') return false;
      if (typeof vv.mediaSourceId !== 'string') return false;
      if (typeof vv.widthPx !== 'number') return false;
      if (typeof vv.qualityLabel !== 'string') return false;
    }
    return true;
  }

  ipcMain.handle(
    'watchparty:start-session',
    async (
      _,
      payload: {
        source: unknown;
        durationSec?: number;
        maxGuests?: number | 'unlimited';
        qualityHeight?: 720 | 1080 | 2160;
        startOffsetSec?: number;
        trackHistory?: boolean;
      },
    ) => {
      // Log via the logger BEFORE any return path so we always see it —
      // the logger buffers pre-startSessionLog lines and flushes once the
      // file opens, so this lands in the on-disk log too.
      watchPartyLogger.info('ipc', 'watchparty:start-session received');
      try {
        if (!isValidWatchPartySource(payload?.source)) {
          watchPartyLogger.warn('ipc', 'watchparty:start-session rejected — invalid source payload');
          return fail('Invalid source payload');
        }
        const durationSec = typeof payload.durationSec === 'number' && payload.durationSec > 0 ? payload.durationSec : 0;
        const maxGuests = payload.maxGuests ?? 4;
        // 2160 is only honoured while the 4K-output Danger Zone toggle is
        // on — a stale or hand-crafted payload degrades to 1080.
        const qualityHeight =
          payload.qualityHeight === 720
            ? 720
            : payload.qualityHeight === 2160 && getSettingValue('watchPartyAllow4kOutput')
              ? 2160
              : 1080;
        const startOffsetSec =
          typeof payload.startOffsetSec === 'number' && payload.startOffsetSec > 0
            ? Math.floor(payload.startOffsetSec)
            : 0;
        const trackHistory = payload.trackHistory !== false; // default ON
        await watchPartySessionManager.startSession({
          source: payload.source,
          durationSec,
          maxGuests,
          qualityHeight,
          startOffsetSec,
          trackHistory,
        });
        return ok(watchPartySessionManager.getPublicState());
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        watchPartyLogger.error('ipc', `watchparty:start-session threw: ${msg}`);
        return fail(e);
      }
    },
  );

  ipcMain.handle('watchparty:start-show', async () => {
    watchPartyLogger.info('ipc', 'watchparty:start-show received');
    try {
      await watchPartySessionManager.startShow();
      return ok(watchPartySessionManager.getPublicState());
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      watchPartyLogger.error('ipc', `watchparty:start-show threw: ${msg}`);
      return fail(e);
    }
  });

  ipcMain.handle('watchparty:end-session', async () => {
    watchPartyLogger.info('ipc', 'watchparty:end-session received');
    try {
      await watchPartySessionManager.endSession('host');
      return ok(undefined);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      watchPartyLogger.error('ipc', `watchparty:end-session threw: ${msg}`);
      return fail(e);
    }
  });

  ipcMain.handle('watchparty:get-state', () => {
    try {
      return ok(watchPartySessionManager.getPublicState());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle(
    'watchparty:host-event',
    (
      _,
      payload: { type: 'play' | 'pause' | 'seek' | 'time-update'; position: number },
    ) => {
      try {
        if (!payload || typeof payload.type !== 'string') return fail('Invalid host event');
        watchPartySessionManager.recordHostEvent(payload);
        return ok(undefined);
      } catch (e) {
        return fail(e);
      }
    },
  );

  watchPartySessionManager.on('state', (state) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('watchparty:state', state);
  });

  watchPartyBinaryManager.on('progress', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('watchparty:setup-progress', data);
  });
  watchPartyBinaryManager.on('error', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('watchparty:setup-error', data);
  });

  // Forward Trakt events to renderer
  traktClient.on('auth-success', () => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:auth-success');
    // Re-arm sync timers now that we have credentials.
    traktSync.startTimers();
    // Kick off the initial watchlist refresh in PARALLEL with whatever the
    // user does next (review preview, apply, skip, etc.). No await — the
    // resulting `watchlist-updated` event will refresh the sidebar count
    // and the LibraryPage when it lands. Critical: do NOT chain after
    // applyWatchedState — those are independent and parallel.
    console.log('[trakt] auth-success — kicking initial watchlist refresh in parallel');
    void traktSync.refreshWatchlist();
  });
  traktClient.on('token-refresh-failed', () => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:token-refresh-failed');
    traktSync.stopTimers();
  });
  traktClient.on('disconnected', () => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:disconnected');
    traktSync.stopTimers();
  });
  traktScrobbler.on('scrobble-error', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:scrobble-error', data);
  });
  traktSync.on('background-sync-complete', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:sync-complete', data);
  });
  traktSync.on('watchlist-updated', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:watchlist-updated', data);
  });
  traktSync.on('apply-progress', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('trakt:apply-progress', data);
  });
  traktSync.on('backfill-progress', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('analytics:backfill-progress', data);
  });
  traktSync.on('backfill-complete', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('analytics:backfill-complete', data);
  });
  traktSync.on('backfill-failed', (data) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('analytics:backfill-failed', data);
  });
}
