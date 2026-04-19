import { app, BrowserWindow } from 'electron';
import { createWindow, registerWindowIpc } from './window';
import { registerIpcHandlers } from './ipc-handlers';
import { mpvManager } from './mpv-manager';
import { initDatabase, closeDatabase } from './database';
import { initImageCache } from './image-cache';
import { syncEngine } from './sync-engine';
import { initSettings } from './settings';
import { initUpdater, checkForUpdates } from './updater';

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
  mainWindow.on('ready-to-show', async () => {
    mainWindow.show();
    mainWindow.setFullScreen(true);

    // Pre-start mpv in idle mode (runs silently in background)
    try {
      await mpvManager.startIdle();
    } catch (err) {
      console.error('[main] Failed to pre-start mpv:', err);
      mainWindow.webContents.send('player:mpv-unavailable');
    }

    // Check for updates after launch (non-blocking), then every 6 hours
    if (app.isPackaged) {
      setTimeout(() => checkForUpdates(), 5000);
      updateCheckInterval = setInterval(() => checkForUpdates(), 6 * 60 * 60 * 1000);
    }

    // If dedup hasn't run in > 7 days, schedule a background rebuild
    syncEngine.checkDedupDrift();
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
  closeDatabase();
  mpvManager.quit().finally(() => app.exit());
});
