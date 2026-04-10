import { contextBridge, ipcRenderer } from 'electron';

const api = {
  auth: {
    connectToServer: (url: string) => ipcRenderer.invoke('emby:auth:connect', { url }),
    login: (username: string, password: string) =>
      ipcRenderer.invoke('emby:auth:login', { username, password }),
    logout: () => ipcRenderer.invoke('emby:auth:logout'),
    getPublicUsers: () => ipcRenderer.invoke('emby:auth:public-users'),
    restore: (serverUrl: string, token: string, userId: string) =>
      ipcRenderer.invoke('emby:auth:restore', { serverUrl, token, userId }),
  },
  library: {
    getViews: () => ipcRenderer.invoke('emby:library:get-views'),
    getItems: (parentId: string, params?: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:library:get-items', { parentId, params }),
    getItem: (itemId: string) => ipcRenderer.invoke('emby:library:get-item', { itemId }),
    getLatest: (parentId: string, limit?: number) =>
      ipcRenderer.invoke('emby:library:get-latest', { parentId, limit }),
    getResume: () => ipcRenderer.invoke('emby:library:get-resume'),
    getNextUp: () => ipcRenderer.invoke('emby:library:get-nextup'),
    getSimilar: (itemId: string) =>
      ipcRenderer.invoke('emby:library:get-similar', { itemId }),
    getSeasons: (seriesId: string) =>
      ipcRenderer.invoke('emby:library:get-seasons', { seriesId }),
    getEpisodes: (seriesId: string, seasonId: string) =>
      ipcRenderer.invoke('emby:library:get-episodes', { seriesId, seasonId }),
  },
  media: {
    getPlaybackInfo: (itemId: string) =>
      ipcRenderer.invoke('emby:media:playback-info', { itemId }),
    getStreamUrl: (itemId: string, mediaSourceId: string) =>
      ipcRenderer.invoke('emby:media:stream-url', { itemId, mediaSourceId }),
    reportStart: (data: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:media:report-start', data),
    reportProgress: (data: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:media:report-progress', data),
    reportStop: (data: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:media:report-stop', data),
  },
  user: {
    getCurrentUser: () => ipcRenderer.invoke('emby:user:current'),
    markPlayed: (itemId: string) =>
      ipcRenderer.invoke('emby:user:mark-played', { itemId }),
    markUnplayed: (itemId: string) =>
      ipcRenderer.invoke('emby:user:mark-unplayed', { itemId }),
    updateFavorite: (itemId: string, isFavorite: boolean) =>
      ipcRenderer.invoke('emby:user:favorite', { itemId, isFavorite }),
  },
  search: {
    query: (term: string, filters?: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:search:query', { term, filters }),
  },
  image: {
    getUrl: (itemId: string, imageType: string, params?: Record<string, unknown>) =>
      ipcRenderer.invoke('emby:image:get-url', { itemId, imageType, params }),
  },
  player: {
    play: (args: { itemId: string; mediaSourceId: string; startPositionTicks?: number; itemName?: string }) =>
      ipcRenderer.invoke('player:play', args),
    stop: () => ipcRenderer.invoke('player:stop'),
    onExited: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('player:exited', handler);
      return () => ipcRenderer.removeListener('player:exited', handler);
    },
    onStarting: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('player:starting', handler);
      return () => ipcRenderer.removeListener('player:starting', handler);
    },
    onStartFailed: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('player:start-failed', handler);
      return () => ipcRenderer.removeListener('player:start-failed', handler);
    },
    onMpvUnavailable: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('player:mpv-unavailable', handler);
      return () => ipcRenderer.removeListener('player:mpv-unavailable', handler);
    },
  },
  window: {
    minimize: () => ipcRenderer.invoke('window:minimize'),
    maximize: () => ipcRenderer.invoke('window:maximize'),
    close: () => ipcRenderer.invoke('window:close'),
    isMaximized: () => ipcRenderer.invoke('window:is-maximized') as Promise<boolean>,
    toggleFullscreen: () => ipcRenderer.invoke('window:toggle-fullscreen'),
    isFullscreen: () => ipcRenderer.invoke('window:is-fullscreen') as Promise<boolean>,
    openExternal: (url: string) => ipcRenderer.invoke('window:open-external', url),
    onMaximizeChange: (cb: (maximized: boolean) => void) => {
      const handler = (_: unknown, val: boolean) => cb(val);
      ipcRenderer.on('window:maximized-change', handler);
      return () => ipcRenderer.removeListener('window:maximized-change', handler);
    },
    onFullscreenChange: (cb: (fullscreen: boolean) => void) => {
      const handler = (_: unknown, val: boolean) => cb(val);
      ipcRenderer.on('window:fullscreen-change', handler);
      return () => ipcRenderer.removeListener('window:fullscreen-change', handler);
    },
  },
};

contextBridge.exposeInMainWorld('api', api);
