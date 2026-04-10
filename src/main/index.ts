import { app, BrowserWindow } from 'electron';
import { createWindow, registerWindowIpc } from './window';
import { registerIpcHandlers } from './ipc-handlers';
import { mpvManager } from './mpv-manager';

app.setAppUserModelId('com.nocturne.desktop');

app.whenReady().then(() => {
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

let isQuitting = false;
app.on('before-quit', (e) => {
  if (isQuitting) return;
  isQuitting = true;
  e.preventDefault();
  mpvManager.quit().finally(() => app.exit());
});
