import { create } from 'zustand';
import type { EmbyServerInfo, EmbyUser, AuthResult } from '../api/types';

const SESSION_KEY = 'nocturne_session';

interface SavedSession {
  serverUrl: string;
  accessToken: string;
  userId: string;
}

interface AuthState {
  serverUrl: string | null;
  serverInfo: EmbyServerInfo | null;
  user: EmbyUser | null;
  accessToken: string | null;
  isAuthenticated: boolean;
  isConnecting: boolean;
  error: string | null;
  connectToServer: (url: string) => Promise<boolean>;
  login: (username: string, password: string) => Promise<boolean>;
  logout: () => Promise<void>;
  loadSavedSession: () => Promise<boolean>;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  serverUrl: null,
  serverInfo: null,
  user: null,
  accessToken: null,
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
      localStorage.setItem(
        SESSION_KEY,
        JSON.stringify({
          serverUrl,
          accessToken: auth.AccessToken,
          userId: auth.User.Id,
        }),
      );
      set({
        user: auth.User,
        accessToken: auth.AccessToken,
        isAuthenticated: true,
        isConnecting: false,
      });
      return true;
    }
    set({ isConnecting: false, error: res.error ?? 'Login failed' });
    return false;
  },

  logout: async () => {
    await window.api.auth.logout();
    localStorage.removeItem(SESSION_KEY);
    set({
      user: null,
      accessToken: null,
      isAuthenticated: false,
      serverUrl: null,
      serverInfo: null,
    });
  },

  loadSavedSession: async () => {
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return false;

    try {
      const session: SavedSession = JSON.parse(raw);
      const res = await window.api.auth.restore(
        session.serverUrl,
        session.accessToken,
        session.userId,
      );
      if (res.success) {
        set({
          serverUrl: session.serverUrl,
          accessToken: session.accessToken,
          user: res.data as EmbyUser,
          isAuthenticated: true,
        });
        return true;
      }
    } catch {
      // Session invalid, clear it
    }
    localStorage.removeItem(SESSION_KEY);
    return false;
  },
}));
