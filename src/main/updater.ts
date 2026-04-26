import { autoUpdater } from 'electron-updater';
import { BrowserWindow } from 'electron';

export interface UpdateInfo {
  version: string;
  releaseNotes?: string;
}

export interface UpdateStatus {
  state: 'idle' | 'checking' | 'available' | 'downloading' | 'downloaded' | 'error';
  info: UpdateInfo | null;
  progress: number;
  error: string | null;
}

let status: UpdateStatus = {
  state: 'idle',
  info: null,
  progress: 0,
  error: null,
};

function broadcast(data: Partial<UpdateStatus>): void {
  status = { ...status, ...data };
  for (const win of BrowserWindow.getAllWindows()) {
    win.webContents.send('updater:status', status);
  }
}

export function initUpdater(): void {
  autoUpdater.autoDownload = false;
  autoUpdater.autoInstallOnAppQuit = true;

  autoUpdater.on('checking-for-update', () => {
    broadcast({ state: 'checking', error: null });
  });

  autoUpdater.on('update-available', (info) => {
    broadcast({
      state: 'available',
      info: { version: info.version, releaseNotes: typeof info.releaseNotes === 'string' ? info.releaseNotes : undefined },
    });
  });

  autoUpdater.on('update-not-available', () => {
    broadcast({ state: 'idle' });
  });

  autoUpdater.on('download-progress', (prog) => {
    broadcast({ state: 'downloading', progress: Math.round(prog.percent) });
  });

  autoUpdater.on('update-downloaded', () => {
    broadcast({ state: 'downloaded', progress: 100 });
  });

  autoUpdater.on('error', (err) => {
    broadcast({ state: 'error', error: err.message });
  });
}

export function checkForUpdates(): void {
  autoUpdater.checkForUpdates().catch((err) => {
    broadcast({ state: 'error', error: err.message });
  });
}

export function downloadUpdate(): void {
  autoUpdater.downloadUpdate().catch((err) => {
    broadcast({ state: 'error', error: err.message });
  });
}

export function installUpdate(): void {
  autoUpdater.quitAndInstall(false, true);
}

export function getUpdateStatus(): UpdateStatus {
  return status;
}
