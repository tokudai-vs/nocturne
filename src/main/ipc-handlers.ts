import { app, ipcMain } from 'electron';
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
  updateItemUserData,
  checkpoint,
  type ItemFilters,
} from './database';

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
]);

export function registerIpcHandlers(): void {
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
  ipcMain.handle('emby:media:playback-info', async (_, { itemId }: { itemId: string }) => {
    try {
      const info = await embyClient.getPlaybackInfo(itemId);
      return ok(info);
    } catch (e) {
      return fail(e);
    }
  });

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
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:media:report-stop', async (_, data) => {
    try {
      await embyClient.reportPlaybackStopped(data);
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
    try {
      await embyClient.markPlayed(itemId);
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('emby:user:mark-unplayed', async (_, { itemId }: { itemId: string }) => {
    try {
      await embyClient.markUnplayed(itemId);
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
      updateItemUserData(itemId, { played: 1, playback_position_ticks: 0, played_percentage: 0 });
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
        updateItemUserData(t.emby_id, { played: 1, playback_position_ticks: 0, played_percentage: 0 });
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
  class PlaybackSession {
    private interval: ReturnType<typeof setInterval> | null = null;
    private session: { itemId: string; mediaSourceId: string; playSessionId: string } | null = null;
    private _lastPosition = 0;
    private _transitioning = false;
    private _sessionSeq = 0;

    get current() { return this.session; }
    get lastPosition() { return this._lastPosition; }
    get transitioning() { return this._transitioning; }
    set transitioning(v: boolean) { this._transitioning = v; }

    start(itemId: string, mediaSourceId: string, playSessionId: string, startPosSec: number): void {
      this.stopReporting();
      this._sessionSeq++;
      this.session = { itemId, mediaSourceId, playSessionId };
      this._lastPosition = startPosSec;
      const seq = this._sessionSeq;
      this.interval = setInterval(async () => {
        if (seq !== this._sessionSeq) { this.stopReporting(); return; }
        if (!mpvManager.running()) { this.stopReporting(); return; }
        try {
          const pos = (await mpvManager.getProperty('time-pos')) as number | null;
          const paused = (await mpvManager.getProperty('pause')) as boolean;
          if (pos != null) this._lastPosition = pos;
          await embyClient.reportPlaybackProgress({
            ItemId: itemId,
            MediaSourceId: mediaSourceId,
            PlaySessionId: playSessionId,
            PositionTicks: Math.floor((pos || 0) * 10_000_000),
            IsPaused: paused,
            CanSeek: true,
            PlayMethod: 'DirectPlay',
          });
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

  // Persistent listener: when mpv file ends (ESC/q = stop, or file finishes)
  mpvManager.on('end-file', async (data: unknown) => {
    const reason = (data as { reason?: string })?.reason;
    // Redirect is handled internally by mpv — ignore
    if (reason === 'redirect') return;

    // Playback error — notify renderer
    if (reason === 'error') {
      const w = getMainWindow();
      if (w && !w.isDestroyed()) {
        w.webContents.send('player:playback-error', {
          message: 'Playback failed — file may be corrupt or codec unsupported',
        });
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
    const session = playback.take();
    playback.stopReporting();
    if (session) {
      embyClient
        .reportPlaybackStopped({
          ItemId: session.itemId,
          MediaSourceId: session.mediaSourceId,
          PlaySessionId: session.playSessionId,
          PositionTicks: finalPositionTicks,
        })
        .catch(() => {
          /* ignore */
        });

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

  ipcMain.handle(
    'player:play',
    async (
      _,
      {
        itemId,
        mediaSourceId,
        startPositionTicks,
        itemName,
      }: {
        itemId: string;
        mediaSourceId: string;
        startPositionTicks?: number;
        itemName?: string;
      },
    ) => {
      if (!isNonEmptyString(itemId) || !isNonEmptyString(mediaSourceId)) return fail('Missing itemId or mediaSourceId');
      if (playback.transitioning) return fail('Playback transition already in progress');
      playback.transitioning = true;
      try {
        const win = getMainWindow();
        if (!win) return fail('No window');

        // Trigger fade to black
        win.webContents.send('player:starting');

        // Wait for fade-in animation to complete (350ms transition + buffer)
        await new Promise((r) => setTimeout(r, 450));

        const url = embyClient.getStreamUrl(itemId, mediaSourceId);
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

        // Report playback start to Emby
        await embyClient.reportPlaybackStart({
          ItemId: itemId,
          MediaSourceId: mediaSourceId,
          PlaySessionId: playSessionId,
          PositionTicks: startPositionTicks || 0,
          CanSeek: true,
          PlayMethod: 'DirectPlay',
        });

        playback.start(itemId, mediaSourceId, playSessionId, (startPositionTicks || 0) / 10_000_000);

        return ok(undefined);
      } catch (e) {
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
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('sync:complete');
  });
  syncEngine.on('error', (err: Error) => {
    const w = getMainWindow();
    if (w && !w.isDestroyed()) w.webContents.send('sync:error', { message: err.message });
  });
  syncEngine.on('dedup-complete', (result: { groupsCreated: number; itemsMerged: number }) => {
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
      if (cached) return ok(cached);
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
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-resume-items', () => {
    try {
      return ok(dbGetResumeItemsDeduped());
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:get-latest-items', (_, { libraryId, limit }: { libraryId: string; limit?: number }) => {
    try {
      return ok(dbGetLatestItems(libraryId, limit));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('cache:search', (_, { query }: { query: string }) => {
    try {
      return ok(dbSearchItemsDeduped(query));
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
      const result = getVirtualLibraryItems(args.vlibId, {
        startIndex: args.startIndex || 0,
        limit: args.limit || 40,
        sortBy: args.sortBy || 'DateCreated',
        sortOrder: args.sortOrder || 'desc',
        searchTerm: args.searchTerm,
        itemType: args.itemType,
      });
      return ok(result);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('vlib:get-latest', (_, { vlibId, limit }: { vlibId: string; limit?: number }) => {
    try {
      return ok(getVirtualLibraryLatest(vlibId, limit || 20));
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('vlib:get-heroes', (_, { vlibId, limit }: { vlibId?: string; limit?: number }) => {
    try {
      return ok(getVirtualLibraryHeroes(vlibId || null, limit || 20));
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
      const { checkForUpdates } = require('./updater');
      checkForUpdates();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:download', () => {
    try {
      const { downloadUpdate } = require('./updater');
      downloadUpdate();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:install', () => {
    try {
      const { installUpdate } = require('./updater');
      installUpdate();
      return ok(undefined);
    } catch (e) {
      return fail(e);
    }
  });

  ipcMain.handle('updater:get-status', () => {
    try {
      const { getUpdateStatus } = require('./updater');
      return ok(getUpdateStatus());
    } catch (e) {
      return fail(e);
    }
  });
}
