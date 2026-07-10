import { create } from 'zustand';
import type { EmbyServerInfo, EmbyUser, AuthResult, ServerConfig } from '../api/types';
import { refreshImageServerMap } from '../utils/image-url';

interface AuthState {
  serverUrl: string | null;
  serverInfo: EmbyServerInfo | null;
  user: EmbyUser | null;
  accessToken: string | null;
  activeServerId: string | null;
  isAuthenticated: boolean;
  isConnecting: boolean;
  error: string | null;
  connectToServer: (url: string) => Promise<boolean>;
  login: (username: string, password: string) => Promise<boolean>;
  logout: () => Promise<void>;
  loadSavedSession: () => Promise<boolean>;
  switchServer: (serverId: string) => Promise<boolean>;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  serverUrl: null,
  serverInfo: null,
  user: null,
  accessToken: null,
  activeServerId: null,
  isAuthenticated: false,
  isConnecting: false,
  error: null,

  connectToServer: async (url: string) => {
    set({ isConnecting: true, error: null });
    const res = await window.api.auth.connectToServer(url);
    if (res.success) {
      set({ serverUrl: url, serverInfo: res.data, isConnecting: false });
      return true;
    }
    set({ isConnecting: false, error: res.error ?? 'Failed to connect' });
    return false;
  },

  login: async (username: string, password: string) => {
    set({ isConnecting: true, error: null });
    const res = await window.api.auth.login(username, password);
    if (res.success) {
      const auth = res.data as AuthResult;
      const serverUrl = get().serverUrl!;
      const serverInfo = get().serverInfo!;

      // Save server config via multi-server manager
      const serverRes = await window.api.servers.add({
        name: serverInfo.ServerName,
        url: serverUrl,
        userId: auth.User.Id,
        username: auth.User.Name,
        accessToken: auth.AccessToken,
        version: serverInfo.Version,
      });

      const serverId = serverRes.success ? (serverRes.data as ServerConfig).id : null;

      set({
        user: auth.User,
        accessToken: auth.AccessToken,
        activeServerId: serverId,
        isAuthenticated: true,
        isConnecting: false,
      });
      void refreshImageServerMap();
      return true;
    }
    set({ isConnecting: false, error: res.error ?? 'Login failed' });
    return false;
  },

  logout: async () => {
    await window.api.auth.logout();
    set({
      user: null,
      accessToken: null,
      activeServerId: null,
      isAuthenticated: false,
      serverUrl: null,
      serverInfo: null,
    });
  },

  loadSavedSession: async () => {
    try {
      // Session credentials are stored only in the main process (settings.json)
      const activeRes = await window.api.servers.getActive();
      if (!activeRes.success || !activeRes.data) return false;
      const server = activeRes.data as ServerConfig;

      const res = await window.api.auth.restore(
        server.url,
        server.accessToken,
        server.userId,
      );
      if (res.success) {
        set({
          serverUrl: server.url,
          accessToken: server.accessToken,
          user: res.data as EmbyUser,
          activeServerId: server.id,
          isAuthenticated: true,
        });
        void refreshImageServerMap();
        return true;
      }
    } catch {
      // Session invalid
    }
    return false;
  },

  switchServer: async (serverId: string) => {
    set({ isConnecting: true, error: null });
    const res = await window.api.servers.switch(serverId);
    if (res.success && res.data) {
      // Refresh user info from the switched server
      const userRes = await window.api.user.getCurrentUser();
      const activeRes = await window.api.servers.getActive();
      const server = activeRes.success ? activeRes.data as ServerConfig : null;

      if (userRes.success && server) {
        set({
          serverUrl: server.url,
          serverInfo: { ServerName: server.name, Version: server.version, Id: '' },
          user: userRes.data as EmbyUser,
          accessToken: server.accessToken,
          activeServerId: server.id,
          isAuthenticated: true,
          isConnecting: false,
        });
        void refreshImageServerMap();
        return true;
      }
    }
    set({ isConnecting: false, error: 'Failed to switch server — token may have expired' });
    return false;
  },
}));
