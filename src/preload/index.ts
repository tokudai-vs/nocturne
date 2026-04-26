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
    // Standalone — don't change active client state
    connectToServerStandalone: (url: string) =>
      ipcRenderer.invoke('emby:auth:connect-to-server', { url }),
    getPublicUsersForServer: (url: string) =>
      ipcRenderer.invoke('emby:auth:public-users-for-server', { url }),
    loginToServer: (url: string, username: string, password: string) =>
      ipcRenderer.invoke('emby:auth:login-to-server', { url, username, password }),
    checkServer: (url: string) =>
      ipcRenderer.invoke('emby:auth:check-server', { url }),
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
    getAllServersViews: () => ipcRenderer.invoke('libraries:get-all-servers-views'),
    getAllServersLatest: (limit?: number) =>
      ipcRenderer.invoke('libraries:get-all-servers-latest', { limit }),
    getAllServersResume: () => ipcRenderer.invoke('libraries:get-all-servers-resume'),
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
  item: {
    markPlayed: (args: { itemId: string; serverId?: string }) =>
      ipcRenderer.invoke('item:mark-played', args),
    markUnplayed: (args: { itemId: string; serverId?: string }) =>
      ipcRenderer.invoke('item:mark-unplayed', args),
    toggleFavorite: (args: { itemId: string; serverId?: string; isFavorite: boolean }) =>
      ipcRenderer.invoke('item:toggle-favorite', args),
    removeFromContinue: (args: { itemId: string; serverId?: string }) =>
      ipcRenderer.invoke('item:remove-from-continue', args),
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
  sync: {
    startFull: () => ipcRenderer.invoke('sync:start-full'),
    startIncremental: () => ipcRenderer.invoke('sync:start-incremental'),
    autoStart: () => ipcRenderer.invoke('sync:auto-start'),
    cancel: () => ipcRenderer.invoke('sync:cancel'),
    getStatus: () => ipcRenderer.invoke('sync:get-status'),
    onProgress: (cb: (data: unknown) => void) => {
      const handler = (_: unknown, data: unknown) => cb(data);
      ipcRenderer.on('sync:progress', handler);
      return () => ipcRenderer.removeListener('sync:progress', handler);
    },
    onComplete: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('sync:complete', handler);
      return () => ipcRenderer.removeListener('sync:complete', handler);
    },
    onError: (cb: (err: { message: string }) => void) => {
      const handler = (_: unknown, err: { message: string }) => cb(err);
      ipcRenderer.on('sync:error', handler);
      return () => ipcRenderer.removeListener('sync:error', handler);
    },
  },
  cache: {
    getItem: (itemId: string) => ipcRenderer.invoke('cache:get-item', { itemId }),
    getLibraryItems: (filters: Record<string, unknown>) =>
      ipcRenderer.invoke('cache:get-library-items', { filters }),
    getResumeItems: () => ipcRenderer.invoke('cache:get-resume-items'),
    getLatestItems: (libraryId: string, limit?: number) =>
      ipcRenderer.invoke('cache:get-latest-items', { libraryId, limit }),
    search: (query: string) => ipcRenderer.invoke('cache:search', { query }),
    resolveDedupGroups: (ids: string[]) => ipcRenderer.invoke('cache:resolve-dedup-groups', { ids }),
    getStats: () => ipcRenderer.invoke('cache:get-stats'),
    clear: () => ipcRenderer.invoke('cache:clear'),
    hasData: () => ipcRenderer.invoke('cache:has-data'),
  },
  imageCache: {
    getCachedUrl: (url: string) => ipcRenderer.invoke('image:get-cached-url', { url }),
    precache: (urls: string[]) => ipcRenderer.invoke('image:precache', { urls }),
  },
  settings: {
    get: () => ipcRenderer.invoke('settings:get'),
    getValue: (key: string) => ipcRenderer.invoke('settings:get-value', { key }),
    set: (key: string, value: unknown) => ipcRenderer.invoke('settings:set', { key, value }),
    setMultiple: (data: Record<string, unknown>) => ipcRenderer.invoke('settings:set-multiple', data),
    reset: () => ipcRenderer.invoke('settings:reset'),
  },
  libraries: {
    suggestMapping: () => ipcRenderer.invoke('libraries:suggest-mapping'),
  },
  vlib: {
    getAll: () => ipcRenderer.invoke('vlib:get-all'),
    getItems: (vlibId: string, opts?: Record<string, unknown>) =>
      ipcRenderer.invoke('vlib:get-items', { vlibId, ...opts }),
    getLatest: (vlibId: string, limit?: number) =>
      ipcRenderer.invoke('vlib:get-latest', { vlibId, limit }),
    getHeroes: (vlibId?: string, limit?: number) =>
      ipcRenderer.invoke('vlib:get-heroes', { vlibId, limit }),
  },
  dedup: {
    getVersions: (itemId: string) =>
      ipcRenderer.invoke('dedup:get-versions', { itemId }),
    getEpisodes: (seriesItemId: string, seasonNumber: number) =>
      ipcRenderer.invoke('dedup:get-episodes', { seriesItemId, seasonNumber }),
    getAdjacentEpisodes: (episodeId: string) =>
      ipcRenderer.invoke('dedup:get-adjacent-episodes', { episodeId }),
    getStats: () => ipcRenderer.invoke('dedup:get-stats'),
    rebuild: () => ipcRenderer.invoke('dedup:rebuild'),
    onComplete: (cb: (data: { groupsCreated: number; itemsMerged: number }) => void) => {
      const handler = (_: unknown, data: { groupsCreated: number; itemsMerged: number }) => cb(data);
      ipcRenderer.on('dedup:complete', handler);
      return () => ipcRenderer.removeListener('dedup:complete', handler);
    },
    onError: (cb: (err: { message: string }) => void) => {
      const handler = (_: unknown, err: { message: string }) => cb(err);
      ipcRenderer.on('dedup:error', handler);
      return () => ipcRenderer.removeListener('dedup:error', handler);
    },
  },
  servers: {
    getAll: () => ipcRenderer.invoke('servers:get-all'),
    getActive: () => ipcRenderer.invoke('servers:get-active'),
    add: (config: Record<string, unknown>) => ipcRenderer.invoke('servers:add', config),
    remove: (serverId: string) => ipcRenderer.invoke('servers:remove', { serverId }),
    switch: (serverId: string) => ipcRenderer.invoke('servers:switch', { serverId }),
    getMappings: () => ipcRenderer.invoke('servers:get-mappings'),
    setMappings: (mappings: Record<string, unknown>) =>
      ipcRenderer.invoke('servers:set-mappings', { mappings }),
    getLibraryMode: () => ipcRenderer.invoke('servers:get-library-mode'),
    getCombinedMappings: () => ipcRenderer.invoke('servers:get-combined-mappings'),
    setCombinedMappings: (mappings: Record<string, unknown>) =>
      ipcRenderer.invoke('servers:set-combined-mappings', { mappings }),
    getAllLibraries: () => ipcRenderer.invoke('servers:get-all-libraries'),
  },
  updater: {
    check: () => ipcRenderer.invoke('updater:check'),
    download: () => ipcRenderer.invoke('updater:download'),
    install: () => ipcRenderer.invoke('updater:install'),
    getStatus: () => ipcRenderer.invoke('updater:get-status'),
    onStatus: (cb: (status: unknown) => void) => {
      const handler = (_: unknown, status: unknown) => cb(status);
      ipcRenderer.on('updater:status', handler);
      return () => ipcRenderer.removeListener('updater:status', handler);
    },
  },
  app: {
    onVisibilityChange: (cb: (data: { visible: boolean }) => void) => {
      const handler = (_: unknown, data: { visible: boolean }) => cb(data);
      ipcRenderer.on('app:visibility', handler);
      return () => ipcRenderer.removeListener('app:visibility', handler);
    },
    onFocusChange: (cb: (data: { focused: boolean }) => void) => {
      const handler = (_: unknown, data: { focused: boolean }) => cb(data);
      ipcRenderer.on('app:focus', handler);
      return () => ipcRenderer.removeListener('app:focus', handler);
    },
    resetFull: () => ipcRenderer.invoke('app:reset-full'),
  },
  session: {
    onExpired: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('auth:session-expired', handler);
      return () => ipcRenderer.removeListener('auth:session-expired', handler);
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
