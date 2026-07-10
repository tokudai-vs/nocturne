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
    getItem: (itemId: string, serverId?: string) =>
      ipcRenderer.invoke('emby:library:get-item', { itemId, serverId }),
    getLatest: (parentId: string, limit?: number) =>
      ipcRenderer.invoke('emby:library:get-latest', { parentId, limit }),
    getResume: () => ipcRenderer.invoke('emby:library:get-resume'),
    getNextUp: () => ipcRenderer.invoke('emby:library:get-nextup'),
    getSimilar: (itemId: string, serverId?: string) =>
      ipcRenderer.invoke('emby:library:get-similar', { itemId, serverId }),
    getSeasons: (seriesId: string, serverId?: string) =>
      ipcRenderer.invoke('emby:library:get-seasons', { seriesId, serverId }),
    getEpisodes: (seriesId: string, seasonId: string, serverId?: string) =>
      ipcRenderer.invoke('emby:library:get-episodes', { seriesId, seasonId, serverId }),
    getAllServersViews: () => ipcRenderer.invoke('libraries:get-all-servers-views'),
    getAllServersLatest: (limit?: number) =>
      ipcRenderer.invoke('libraries:get-all-servers-latest', { limit }),
    getAllServersResume: () => ipcRenderer.invoke('libraries:get-all-servers-resume'),
  },
  media: {
    getPlaybackInfo: (itemId: string, serverId?: string) =>
      ipcRenderer.invoke('emby:media:playback-info', { itemId, serverId }),
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
    play: (args: { itemId: string; mediaSourceId: string; startPositionTicks?: number; itemName?: string; serverId?: string }) =>
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
    onServerError: (cb: (data: { serverId: string; serverName: string; message: string }) => void) => {
      const handler = (_: unknown, data: { serverId: string; serverName: string; message: string }) => cb(data);
      ipcRenderer.on('sync:server-error', handler);
      return () => ipcRenderer.removeListener('sync:server-error', handler);
    },
    onPartial: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('sync:partial', handler);
      return () => ipcRenderer.removeListener('sync:partial', handler);
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
  trakt: {
    getStatus: () => ipcRenderer.invoke('trakt:get-status'),
    authStart: () => ipcRenderer.invoke('trakt:auth-start'),
    authPoll: (deviceCode: string) =>
      ipcRenderer.invoke('trakt:auth-poll', { deviceCode }),
    disconnect: () => ipcRenderer.invoke('trakt:disconnect'),
    drainQueue: () => ipcRenderer.invoke('trakt:drain-queue'),
    getQueueCount: () => ipcRenderer.invoke('trakt:get-queue-count'),
    getFailedQueueCount: () => ipcRenderer.invoke('trakt:get-failed-queue-count'),
    clearFailedQueue: () => ipcRenderer.invoke('trakt:clear-failed-queue'),
    getAdvancedConfig: () => ipcRenderer.invoke('trakt:get-advanced-config'),
    setAdvancedConfig: (cfg: { clientId: string; clientSecret: string }) =>
      ipcRenderer.invoke('trakt:set-advanced-config', cfg),
    openVerification: (url: string) =>
      ipcRenderer.invoke('trakt:open-verification', { url }),
    onAuthSuccess: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('trakt:auth-success', handler);
      return () => ipcRenderer.removeListener('trakt:auth-success', handler);
    },
    onDisconnected: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('trakt:disconnected', handler);
      return () => ipcRenderer.removeListener('trakt:disconnected', handler);
    },
    onScrobbleError: (
      cb: (err: { action: string; itemId: string; message: string }) => void,
    ) => {
      const handler = (
        _: unknown,
        err: { action: string; itemId: string; message: string },
      ) => cb(err);
      ipcRenderer.on('trakt:scrobble-error', handler);
      return () => ipcRenderer.removeListener('trakt:scrobble-error', handler);
    },
    onTokenRefreshFailed: (cb: () => void) => {
      const handler = () => cb();
      ipcRenderer.on('trakt:token-refresh-failed', handler);
      return () => ipcRenderer.removeListener('trakt:token-refresh-failed', handler);
    },
    // ── Phase 2/3/4 ──
    fetchPreview: () => ipcRenderer.invoke('trakt:fetch-preview'),
    applyWatchedState: (embyIds: string[]) =>
      ipcRenderer.invoke('trakt:apply-watched-state', { embyIds }),
    cancelApply: () => ipcRenderer.invoke('trakt:cancel-apply'),
    onApplyProgress: (cb: (data: { current: number; total: number }) => void) => {
      const handler = (_: unknown, data: { current: number; total: number }) => cb(data);
      ipcRenderer.on('trakt:apply-progress', handler);
      return () => ipcRenderer.removeListener('trakt:apply-progress', handler);
    },
    syncNow: () => ipcRenderer.invoke('trakt:sync-now'),
    getStats: () => ipcRenderer.invoke('trakt:get-stats'),
    getWatchlist: () => ipcRenderer.invoke('trakt:get-watchlist'),
    refreshWatchlist: () => ipcRenderer.invoke('trakt:refresh-watchlist'),
    addToWatchlist: (itemId: string) =>
      ipcRenderer.invoke('trakt:add-to-watchlist', { itemId }),
    removeFromWatchlist: (
      args: { itemId?: string; traktType?: 'movie' | 'show'; tmdbId?: string; key?: string },
    ) => ipcRenderer.invoke('trakt:remove-from-watchlist', args),
    inWatchlist: (itemId: string) =>
      ipcRenderer.invoke('trakt:in-watchlist', { itemId }),
    getRating: (tmdbId: string, type: 'movie' | 'show') =>
      ipcRenderer.invoke('trakt:get-rating', { tmdbId, type }),
    checkWatched: (
      args: { tmdbId: string; type: 'movie' | 'episode'; season?: number; episode?: number },
    ) => ipcRenderer.invoke('trakt:check-watched', args),
    onSyncComplete: (cb: (data: { newlyWatched: number; failed: number }) => void) => {
      const handler = (_: unknown, data: { newlyWatched: number; failed: number }) => cb(data);
      ipcRenderer.on('trakt:sync-complete', handler);
      return () => ipcRenderer.removeListener('trakt:sync-complete', handler);
    },
    onWatchlistUpdated: (cb: (data: { count: number }) => void) => {
      const handler = (_: unknown, data: { count: number }) => cb(data);
      ipcRenderer.on('trakt:watchlist-updated', handler);
      return () => ipcRenderer.removeListener('trakt:watchlist-updated', handler);
    },
  },
  analytics: {
    getStats: (args: { rangeStart: string; rangeEnd: string; source?: 'local' | 'trakt' | 'combined' }) =>
      ipcRenderer.invoke('analytics:get-stats', args),
    getBackfillStatus: () => ipcRenderer.invoke('analytics:get-backfill-status'),
    triggerBackfill: () => ipcRenderer.invoke('analytics:trigger-backfill'),
    onBackfillProgress: (cb: (data: { current: number; total: number }) => void) => {
      const handler = (_: unknown, data: { current: number; total: number }) => cb(data);
      ipcRenderer.on('analytics:backfill-progress', handler);
      return () => ipcRenderer.removeListener('analytics:backfill-progress', handler);
    },
    onBackfillComplete: (cb: (data: { inserted: number; total: number }) => void) => {
      const handler = (_: unknown, data: { inserted: number; total: number }) => cb(data);
      ipcRenderer.on('analytics:backfill-complete', handler);
      return () => ipcRenderer.removeListener('analytics:backfill-complete', handler);
    },
    onBackfillFailed: (cb: (err: { message: string }) => void) => {
      const handler = (_: unknown, err: { message: string }) => cb(err);
      ipcRenderer.on('analytics:backfill-failed', handler);
      return () => ipcRenderer.removeListener('analytics:backfill-failed', handler);
    },
  },
  watchparty: {
    binariesReady: () => ipcRenderer.invoke('watchparty:binaries-ready'),
    setupBinaries: () => ipcRenderer.invoke('watchparty:setup-binaries'),
    probeEncoder: () => ipcRenderer.invoke('watchparty:probe-encoder'),
    startSession: (payload: {
      source: unknown;
      durationSec?: number;
      maxGuests?: number | 'unlimited';
      qualityHeight?: 720 | 1080 | 2160;
      startOffsetSec?: number;
      trackHistory?: boolean;
    }) => ipcRenderer.invoke('watchparty:start-session', payload),
    startShow: () => ipcRenderer.invoke('watchparty:start-show'),
    endSession: () => ipcRenderer.invoke('watchparty:end-session'),
    getState: () => ipcRenderer.invoke('watchparty:get-state'),
    hostEvent: (payload: { type: 'play' | 'pause' | 'seek' | 'time-update'; position: number }) =>
      ipcRenderer.invoke('watchparty:host-event', payload),
    onState: (cb: (state: unknown) => void) => {
      const handler = (_: unknown, state: unknown) => cb(state);
      ipcRenderer.on('watchparty:state', handler);
      return () => ipcRenderer.removeListener('watchparty:state', handler);
    },
    onSetupProgress: (
      cb: (data: { phase: 'ffmpeg' | 'cloudflared' | 'unzip'; percent: number }) => void,
    ) => {
      const handler = (
        _: unknown,
        data: { phase: 'ffmpeg' | 'cloudflared' | 'unzip'; percent: number },
      ) => cb(data);
      ipcRenderer.on('watchparty:setup-progress', handler);
      return () => ipcRenderer.removeListener('watchparty:setup-progress', handler);
    },
    onSetupError: (cb: (err: { phase: string; message: string }) => void) => {
      const handler = (_: unknown, err: { phase: string; message: string }) => cb(err);
      ipcRenderer.on('watchparty:setup-error', handler);
      return () => ipcRenderer.removeListener('watchparty:setup-error', handler);
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
