import { ipcMain } from 'electron';
import { execFile } from 'child_process';
import { embyClient } from './emby-client';
import { mpvManager } from './mpv-manager';
import { getMainWindow } from './window';

type IpcResult<T = unknown> = { success: true; data: T } | { success: false; error: string };

function ok<T>(data: T): IpcResult<T> {
  return { success: true, data };
}

function fail(err: unknown): IpcResult<never> {
  const message =
    err instanceof Error ? err.message : typeof err === 'string' ? err : 'Unknown error';
  return { success: false, error: message };
}

export function registerIpcHandlers(): void {
  // ── Auth ──────────────────────────────────────────────
  ipcMain.handle('emby:auth:connect', async (_, { url }: { url: string }) => {
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
  let progressInterval: ReturnType<typeof setInterval> | null = null;
  let currentPlaySession: { itemId: string; mediaSourceId: string; playSessionId: string } | null =
    null;
  let lastKnownPosition = 0;
  let isPlaybackTransitioning = false;

  function stopProgressReporting(): void {
    if (progressInterval) {
      clearInterval(progressInterval);
      progressInterval = null;
    }
  }

  function startProgressReporting(
    itemId: string,
    mediaSourceId: string,
    playSessionId: string,
  ): void {
    stopProgressReporting();
    progressInterval = setInterval(async () => {
      if (!mpvManager.running()) {
        stopProgressReporting();
        return;
      }
      try {
        const pos = (await mpvManager.getProperty('time-pos')) as number | null;
        const paused = (await mpvManager.getProperty('pause')) as boolean;
        if (pos != null) lastKnownPosition = pos;
        await embyClient.reportPlaybackProgress({
          ItemId: itemId,
          MediaSourceId: mediaSourceId,
          PlaySessionId: playSessionId,
          PositionTicks: Math.floor((pos || 0) * 10_000_000),
          IsPaused: paused,
          CanSeek: true,
          PlayMethod: 'DirectPlay',
        });
      } catch {
        /* ignore */
      }
    }, 10_000);
  }

  // Persistent listener: when mpv file ends (ESC/q = stop, or file finishes)
  mpvManager.on('end-file', async (data: unknown) => {
    const reason = (data as { reason?: string })?.reason;
    // Redirect is handled internally by mpv — ignore
    if (reason === 'redirect') return;

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
    const session = currentPlaySession;
    currentPlaySession = null;
    if (session) {
      embyClient
        .reportPlaybackStopped({
          ItemId: session.itemId,
          MediaSourceId: session.mediaSourceId,
          PlaySessionId: session.playSessionId,
          PositionTicks: Math.floor((lastKnownPosition || 0) * 10_000_000),
        })
        .catch(() => {
          /* ignore */
        });
    }
    stopProgressReporting();
  });

  // If mpv process dies unexpectedly, show main window
  mpvManager.on('process-exit', () => {
    stopProgressReporting();
    currentPlaySession = null;
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
      if (isPlaybackTransitioning) return fail('Playback transition already in progress');
      isPlaybackTransitioning = true;
      try {
        const win = getMainWindow();
        if (!win) return fail('No window');

        // Trigger fade to black
        win.webContents.send('player:starting');

        // Wait for fade-in animation to complete (350ms transition + buffer)
        await new Promise((r) => setTimeout(r, 450));

        const url = embyClient.getStreamUrl(itemId, mediaSourceId);
        const playSessionId = `nocturne-${Date.now()}`;

        currentPlaySession = { itemId, mediaSourceId, playSessionId };
        lastKnownPosition = (startPositionTicks || 0) / 10_000_000;

        // Load file into hot mpv — instant via IPC
        await mpvManager.loadFile(url, {
          startPositionTicks,
          title: itemName,
        });

        // Let mpv window appear, then hide main window behind it
        await new Promise((r) => setTimeout(r, 200));
        win.hide();

        // Force focus on mpv window using PowerShell (execFile avoids shell injection)
        const title = itemName || 'mpv';
        const psScript = `Start-Sleep -Milliseconds 100; (New-Object -ComObject WScript.Shell).AppActivate('${title.replace(/'/g, "''")}')`;
        const encoded = Buffer.from(psScript, 'utf16le').toString('base64');
        execFile('powershell', ['-NoProfile', '-EncodedCommand', encoded], (err) => {
          if (err) console.log('[mpv] AppActivate failed, user may need to click mpv window');
        });

        // Report playback start to Emby
        await embyClient.reportPlaybackStart({
          ItemId: itemId,
          MediaSourceId: mediaSourceId,
          PlaySessionId: playSessionId,
          PositionTicks: startPositionTicks || 0,
          CanSeek: true,
          PlayMethod: 'DirectPlay',
        });

        startProgressReporting(itemId, mediaSourceId, playSessionId);

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
        isPlaybackTransitioning = false;
      }
    },
  );

  ipcMain.handle('player:stop', async () => {
    try {
      stopProgressReporting();
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
}
