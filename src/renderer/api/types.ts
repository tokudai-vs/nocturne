// Server
export interface EmbyServerInfo {
  ServerName: string;
  Version: string;
  Id: string;
}

// Auth
export interface AuthResult {
  AccessToken: string;
  User: EmbyUser;
  ServerId: string;
}

export interface EmbyUser {
  Id: string;
  Name: string;
  PrimaryImageTag?: string;
  HasPassword: boolean;
}

export interface PublicUser {
  Name: string;
  Id: string;
  HasPassword: boolean;
  PrimaryImageTag?: string;
}

// Library
export interface BaseItemDto {
  Id: string;
  Name: string;
  Type:
    | 'Movie'
    | 'Series'
    | 'Season'
    | 'Episode'
    | 'BoxSet'
    | 'MusicAlbum'
    | 'Audio'
    | 'Folder'
    | 'CollectionFolder';
  Overview?: string;
  RunTimeTicks?: number;
  CommunityRating?: number;
  OfficialRating?: string;
  ProductionYear?: number;
  PremiereDate?: string;
  Genres?: string[];
  Studios?: { Name: string }[];
  People?: {
    Id: string;
    Name: string;
    Role?: string;
    Type: string;
    PrimaryImageTag?: string;
  }[];
  ImageTags?: Record<string, string>;
  BackdropImageTags?: string[];
  UserData?: UserItemData;
  MediaSources?: MediaSource[];
  SeriesId?: string;
  SeriesName?: string;
  SeasonId?: string;
  SeasonName?: string;
  IndexNumber?: number;
  ParentIndexNumber?: number;
  ParentThumbItemId?: string;
  ParentThumbImageTag?: string;
  ParentBackdropItemId?: string;
  ParentBackdropImageTags?: string[];
  CriticRating?: number;
  ChildCount?: number;
  RecursiveItemCount?: number;
  Status?: string;
}

export interface UserItemData {
  PlaybackPositionTicks: number;
  PlayCount: number;
  IsFavorite: boolean;
  Played: boolean;
  PlayedPercentage?: number;
}

export interface MediaSource {
  Id: string;
  Name: string;
  Path: string;
  Container: string;
  Size: number;
  Bitrate: number;
  MediaStreams: MediaStream[];
  SupportsDirectPlay: boolean;
  SupportsDirectStream: boolean;
}

export interface MediaStream {
  Type: 'Video' | 'Audio' | 'Subtitle';
  Index: number;
  Codec: string;
  DisplayTitle: string;
  Language?: string;
  IsDefault: boolean;
  IsExternal?: boolean;
}

export interface ItemsResult {
  Items: BaseItemDto[];
  TotalRecordCount: number;
}

export type ImageType = 'Primary' | 'Backdrop' | 'Logo' | 'Thumb' | 'Banner';

// IPC response wrapper
export interface IpcResponse<T = unknown> {
  success: boolean;
  data?: T;
  error?: string;
}

// Window API type declaration
declare global {
  interface Window {
    api: {
      auth: {
        connectToServer: (url: string) => Promise<IpcResponse<EmbyServerInfo>>;
        login: (username: string, password: string) => Promise<IpcResponse<AuthResult>>;
        logout: () => Promise<IpcResponse<void>>;
        getPublicUsers: () => Promise<IpcResponse<PublicUser[]>>;
        restore: (serverUrl: string, token: string, userId: string) => Promise<IpcResponse<EmbyUser>>;
      };
      library: {
        getViews: () => Promise<IpcResponse<ItemsResult>>;
        getItems: (parentId: string, params?: Record<string, unknown>) => Promise<IpcResponse<ItemsResult>>;
        getItem: (itemId: string) => Promise<IpcResponse<BaseItemDto>>;
        getLatest: (parentId: string, limit?: number) => Promise<IpcResponse<BaseItemDto[]>>;
        getResume: () => Promise<IpcResponse<ItemsResult>>;
        getNextUp: () => Promise<IpcResponse<ItemsResult>>;
        getSimilar: (itemId: string) => Promise<IpcResponse<ItemsResult>>;
        getSeasons: (seriesId: string) => Promise<IpcResponse<ItemsResult>>;
        getEpisodes: (seriesId: string, seasonId: string) => Promise<IpcResponse<ItemsResult>>;
      };
      media: {
        getPlaybackInfo: (itemId: string) => Promise<IpcResponse<{ MediaSources: MediaSource[] }>>;
        getStreamUrl: (itemId: string, mediaSourceId: string) => Promise<IpcResponse<string>>;
        reportStart: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
        reportProgress: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
        reportStop: (data: Record<string, unknown>) => Promise<IpcResponse<void>>;
      };
      user: {
        getCurrentUser: () => Promise<IpcResponse<EmbyUser>>;
        markPlayed: (itemId: string) => Promise<IpcResponse<void>>;
        markUnplayed: (itemId: string) => Promise<IpcResponse<void>>;
        updateFavorite: (itemId: string, isFavorite: boolean) => Promise<IpcResponse<void>>;
      };
      search: {
        query: (term: string, filters?: Record<string, unknown>) => Promise<IpcResponse<ItemsResult>>;
      };
      image: {
        getUrl: (itemId: string, imageType: string, params?: Record<string, unknown>) => Promise<IpcResponse<string>>;
      };
      player: {
        play: (args: { itemId: string; mediaSourceId: string; startPositionTicks?: number; itemName?: string }) => Promise<IpcResponse<void>>;
        stop: () => Promise<IpcResponse<void>>;
        onExited: (cb: () => void) => () => void;
        onStarting: (cb: () => void) => () => void;
        onStartFailed: (cb: () => void) => () => void;
        onMpvUnavailable: (cb: () => void) => () => void;
      };
      window: {
        minimize: () => Promise<void>;
        maximize: () => Promise<void>;
        close: () => Promise<void>;
        isMaximized: () => Promise<boolean>;
        toggleFullscreen: () => Promise<void>;
        isFullscreen: () => Promise<boolean>;
        openExternal: (url: string) => Promise<void>;
        onMaximizeChange: (cb: (maximized: boolean) => void) => () => void;
        onFullscreenChange: (cb: (fullscreen: boolean) => void) => () => void;
      };
    };
  }
}

export {};
