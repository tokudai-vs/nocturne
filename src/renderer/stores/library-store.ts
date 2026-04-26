import { create } from 'zustand';
import type { BaseItemDto, ItemsResult, VirtualLibrary, CachedItem } from '../api/types';
import { cachedToBaseItems } from '../utils/cache-adapter';
import { useSettingsStore } from './settings-store';

interface LibraryState {
  // Virtual libraries (sidebar)
  virtualLibraries: VirtualLibrary[];
  vlibsLoaded: boolean;

  // Raw Emby views (fallback when cache is empty)
  views: BaseItemDto[];
  viewsLoading: boolean;

  // Home page data
  resumeItems: BaseItemDto[];
  resumeLoading: boolean;
  nextUpItems: BaseItemDto[];
  nextUpLoading: boolean;

  // Current library page data (kept for compatibility)
  currentItems: BaseItemDto[];
  currentItemsTotal: number;
  currentItemsLoading: boolean;

  // Actions
  fetchVirtualLibraries: () => Promise<void>;
  fetchViews: () => Promise<void>;
  fetchResume: () => Promise<void>;
  fetchNextUp: () => Promise<void>;
  fetchItems: (parentId: string, params?: Record<string, unknown>) => Promise<void>;
  clearCurrentItems: () => void;
  refreshAll: () => Promise<void>;
}

export const useLibraryStore = create<LibraryState>((set, get) => ({
  virtualLibraries: [],
  vlibsLoaded: false,
  views: [],
  viewsLoading: false,
  resumeItems: [],
  resumeLoading: false,
  nextUpItems: [],
  nextUpLoading: false,
  currentItems: [],
  currentItemsTotal: 0,
  currentItemsLoading: false,

  fetchVirtualLibraries: async () => {
    const res = await window.api.vlib.getAll();
    if (res.success) {
      const vlibs = res.data as VirtualLibrary[];
      set({ virtualLibraries: vlibs, vlibsLoaded: true });
    } else {
      // Fallback: if vlib returns empty (no cache yet), fetch raw views
      set({ vlibsLoaded: true });
    }
  },

  fetchViews: async () => {
    set({ viewsLoading: true });
    const res = await window.api.library.getViews();
    if (res.success) {
      const data = res.data as ItemsResult;
      set({ views: data.Items, viewsLoading: false });
    } else {
      set({ viewsLoading: false });
    }
  },

  fetchResume: async () => {
    set({ resumeLoading: true });
    const settings = useSettingsStore.getState().settings;
    const isCombined = settings?.libraryMode === 'combined';

    if (isCombined) {
      // Trust the cache only when sync reports 'complete'. A partial cache
      // (one server done, another mid-sync) would otherwise hide the
      // in-flight server's resume items.
      const statusRes = await window.api.sync.getStatus();
      const complete = statusRes.success && statusRes.data?.syncStatus === 'complete';
      if (!complete) {
        const res = await window.api.library.getAllServersResume();
        if (res.success && res.data) {
          set({ resumeItems: res.data.items, resumeLoading: false });
        } else {
          set({ resumeItems: [], resumeLoading: false });
        }
        return;
      }
    }

    // Sync complete (or separate mode): cache is authoritative.
    const cacheRes = await window.api.cache.getResumeItems();
    if (cacheRes.success) {
      const cached = cacheRes.data as CachedItem[];
      if (cached.length > 0) {
        set({ resumeItems: cachedToBaseItems(cached), resumeLoading: false });
        return;
      }
    }

    // Separate mode with empty cache: fall back to active-server Emby API.
    if (!isCombined) {
      const res = await window.api.library.getResume();
      if (res.success) {
        const data = res.data as ItemsResult;
        set({ resumeItems: data.Items, resumeLoading: false });
        return;
      }
    }

    set({ resumeItems: [], resumeLoading: false });
  },

  fetchNextUp: async () => {
    set({ nextUpLoading: true });
    // Next Up requires server-side logic — always use Emby API
    const res = await window.api.library.getNextUp();
    if (res.success) {
      const data = res.data as ItemsResult;
      set({ nextUpItems: data.Items, nextUpLoading: false });
    } else {
      set({ nextUpLoading: false });
    }
  },

  fetchItems: async (parentId, params) => {
    set({ currentItemsLoading: true });
    const res = await window.api.library.getItems(parentId, params);
    if (res.success) {
      const data = res.data as ItemsResult;
      set({
        currentItems: data.Items,
        currentItemsTotal: data.TotalRecordCount,
        currentItemsLoading: false,
      });
    } else {
      set({ currentItemsLoading: false });
    }
  },

  clearCurrentItems: () => {
    set({ currentItems: [], currentItemsTotal: 0 });
  },

  refreshAll: async () => {
    const { fetchVirtualLibraries, fetchResume, fetchNextUp } = get();
    await Promise.all([
      fetchVirtualLibraries(),
      fetchResume(),
      fetchNextUp(),
    ]);
  },
}));
