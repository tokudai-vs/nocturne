// The core Emby HTTP client lives in the main process (src/main/emby-client.ts).
// The renderer communicates exclusively through IPC via window.api.
// This module re-exports the typed API surface for renderer-side convenience.

export const emby = {
  auth: {
    connectToServer: (url: string) => window.api.auth.connectToServer(url),
    login: (username: string, password: string) => window.api.auth.login(username, password),
    logout: () => window.api.auth.logout(),
    getPublicUsers: () => window.api.auth.getPublicUsers(),
  },
  library: {
    getViews: () => window.api.library.getViews(),
    getItems: (parentId: string, params?: Record<string, unknown>) =>
      window.api.library.getItems(parentId, params),
    getItem: (itemId: string) => window.api.library.getItem(itemId),
    getLatest: (parentId: string, limit?: number) =>
      window.api.library.getLatest(parentId, limit),
    getResume: () => window.api.library.getResume(),
    getNextUp: () => window.api.library.getNextUp(),
    getSimilar: (itemId: string) => window.api.library.getSimilar(itemId),
    getSeasons: (seriesId: string) => window.api.library.getSeasons(seriesId),
    getEpisodes: (seriesId: string, seasonId: string) =>
      window.api.library.getEpisodes(seriesId, seasonId),
  },
  media: {
    getPlaybackInfo: (itemId: string) => window.api.media.getPlaybackInfo(itemId),
    getStreamUrl: (itemId: string, mediaSourceId: string) =>
      window.api.media.getStreamUrl(itemId, mediaSourceId),
    reportStart: (data: Record<string, unknown>) => window.api.media.reportStart(data),
    reportProgress: (data: Record<string, unknown>) => window.api.media.reportProgress(data),
    reportStop: (data: Record<string, unknown>) => window.api.media.reportStop(data),
  },
  user: {
    getCurrentUser: () => window.api.user.getCurrentUser(),
    markPlayed: (itemId: string) => window.api.user.markPlayed(itemId),
    markUnplayed: (itemId: string) => window.api.user.markUnplayed(itemId),
    updateFavorite: (itemId: string, isFavorite: boolean) =>
      window.api.user.updateFavorite(itemId, isFavorite),
  },
  search: {
    query: (term: string, filters?: Record<string, unknown>) =>
      window.api.search.query(term, filters),
  },
  image: {
    getUrl: (itemId: string, imageType: string, params?: Record<string, unknown>) =>
      window.api.image.getUrl(itemId, imageType, params),
  },
};
