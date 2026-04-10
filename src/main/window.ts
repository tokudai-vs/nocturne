import { BrowserWindow, shell, ipcMain } from 'electron';
import { join } from 'path';

function safeOpenExternal(url: string): void {
  try {
    const parsed = new URL(url);
    if (parsed.protocol === 'https:' || parsed.protocol === 'http:') {
      shell.openExternal(url);
    }
  } catch {
    /* invalid URL */
  }
}

let mainWindow: BrowserWindow | null = null;

export function getMainWindow(): BrowserWindow | null {
  return mainWindow;
}

export function createWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 1024,
    minHeight: 600,
    backgroundColor: '#0f0f0f',
    title: 'Nocturne',
    frame: false,
    titleBarStyle: 'hidden',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      backgroundThrottling: false,
    },
  });

  mainWindow = win;

  win.webContents.setWindowOpenHandler(({ url }) => {
    safeOpenExternal(url);
    return { action: 'deny' };
  });

  // Forward window state events to renderer
  win.on('maximize', () => win.webContents.send('window:maximized-change', true));
  win.on('unmaximize', () => win.webContents.send('window:maximized-change', false));
  win.on('enter-full-screen', () => win.webContents.send('window:fullscreen-change', true));
  win.on('leave-full-screen', () => {
    // Lock to fullscreen — force back immediately
    win.setFullScreen(true);
  });

  if (process.env['ELECTRON_RENDERER_URL']) {
    win.loadURL(process.env['ELECTRON_RENDERER_URL']);
  } else {
    win.loadFile(join(__dirname, '../renderer/index.html'));
  }

  return win;
}

export function registerWindowIpc(): void {
  ipcMain.handle('window:minimize', () => mainWindow?.minimize());
  ipcMain.handle('window:maximize', () => {
    if (!mainWindow) return;
    if (mainWindow.isMaximized()) mainWindow.unmaximize();
    else mainWindow.maximize();
  });
  ipcMain.handle('window:close', () => mainWindow?.close());
  ipcMain.handle('window:is-maximized', () => mainWindow?.isMaximized() ?? false);
  ipcMain.handle('window:toggle-fullscreen', () => {
    if (!mainWindow) return;
    mainWindow.setFullScreen(!mainWindow.isFullScreen());
  });
  ipcMain.handle('window:is-fullscreen', () => mainWindow?.isFullScreen() ?? false);
  ipcMain.handle('window:open-external', (_, url: string) => {
    safeOpenExternal(url);
  });
}
