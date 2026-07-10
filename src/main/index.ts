import { app, BrowserWindow } from 'electron';
import { createWindow, registerWindowIpc } from './window';
import { registerIpcHandlers } from './ipc-handlers';
import { mpvManager } from './mpv-manager';
import { initDatabase, closeDatabase } from './database';
import { initImageCache } from './image-cache';
import { syncEngine } from './sync-engine';
import { initSettings } from './settings';
import { initUpdater, checkForUpdates } from './updater';
import { traktScrobbler } from './trakt-scrobbler';
import { traktSync } from './trakt-sync';
import { watchPartySessionManager } from './watchparty-session';

app.setAppUserModelId('com.nocturne.desktop');

app.whenReady().then(() => {
  // Initialize local data layer
  initDatabase();
  initImageCache();
  initSettings();
  initUpdater();

  registerIpcHandlers();
  registerWindowIpc();

  const mainWindow = createWindow();
  mainWindow.on('ready-to-show', () => {
    mainWindow.show();
    mainWindow.setFullScreen(true);

    // Pre-start mpv in idle mode — fire-and-forget so it does NOT block the
    // rest of the post-show kickoffs (Trakt watchlist, scrobbler drain,
    // dedup drift check). mpv spawn is 200-800ms+; the user has no reason
    // to wait on that for the sidebar / watchlist count to populate.
    // Failures still surface to the renderer via `player:mpv-unavailable`.
    mpvManager.startIdle().catch((err) => {
      console.error('[main] Failed to pre-start mpv:', err);
      if (!mainWindow.isDestroyed()) {
        mainWindow.webContents.send('player:mpv-unavailable');
      }
    });

    // Check for updates after launch (non-blocking), then every 6 hours
    if (app.isPackaged) {
      setTimeout(() => checkForUpdates(), 5000);
      updateCheckInterval = setInterval(() => checkForUpdates(), 6 * 60 * 60 * 1000);
    }

    // If dedup hasn't run in > 7 days, schedule a background rebuild
    syncEngine.checkDedupDrift();

    // Drain any Trakt scrobbles queued from a prior session
    traktScrobbler.init();

    // Start Trakt history (6h) + watchlist (1h) periodic sync timers.
    // No-op when not connected; re-armed on successful auth.
    traktSync.startTimers();

    // Kick an immediate watchlist refresh on launch so the sidebar reflects
    // changes made on Trakt's side since last run, instead of waiting up to
    // 1h for the timer. The single-flight guard in refreshWatchlist makes
    // boot + auth-success collisions a no-op (second caller attaches to the
    // first promise), so we don't need mode-detection here.
    void traktSync.refreshWatchlist();
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

let updateCheckInterval: ReturnType<typeof setInterval> | null = null;
let isQuitting = false;
app.on('before-quit', (e) => {
  if (isQuitting) return;
  isQuitting = true;
  e.preventDefault();
  if (updateCheckInterval) clearInterval(updateCheckInterval);
  syncEngine.cancel();
  traktSync.stopTimers();
  // Tear down any in-flight Watch Party so ffmpeg + cloudflared don't
  // outlive the app (including the "host closed the window during the
  // 10s end-grace countdown" path — main still has LIVE state then).
  // Called unconditionally: endSession is a no-op on IDLE and, when a
  // teardown is already mid-flight (host clicked End and quit immediately),
  // returns that same promise so we wait for the children to die instead
  // of app.exit() orphaning them.
  // The DB closes AFTER the teardown settles — the Watch Party stop path
  // writes final playback state to the cache, and closing first made that
  // write throw mid-teardown.
  const wpTeardown = watchPartySessionManager
    .endSession('error', 'App quitting')
    .catch((err) => {
      console.warn('[main] watchparty teardown on quit failed:', err);
    });
  Promise.allSettled([mpvManager.quit(), wpTeardown]).finally(() => {
    try {
      closeDatabase();
    } catch (err) {
      console.warn('[main] closeDatabase on quit failed:', err);
    }
    app.exit();
  });
});
