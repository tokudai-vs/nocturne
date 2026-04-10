import { create } from 'zustand';
import type { BaseItemDto, ItemsResult } from '../api/types';

interface LibraryState {
  views: BaseItemDto[];
  viewsLoading: boolean;
  currentItems: BaseItemDto[];
  currentItemsTotal: number;
  currentItemsLoading: boolean;
  resumeItems: BaseItemDto[];
  resumeLoading: boolean;
  nextUpItems: BaseItemDto[];
  nextUpLoading: boolean;
  fetchViews: () => Promise<void>;
  fetchItems: (parentId: string, params?: Record<string, unknown>) => Promise<void>;
  fetchResume: () => Promise<void>;
  fetchNextUp: () => Promise<void>;
  clearCurrentItems: () => void;
}

export const useLibraryStore = create<LibraryState>((set) => ({
  views: [],
  viewsLoading: false,
  currentItems: [],
  currentItemsTotal: 0,
  currentItemsLoading: false,
  resumeItems: [],
  resumeLoading: false,
  nextUpItems: [],
  nextUpLoading: false,

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

  fetchResume: async () => {
    set({ resumeLoading: true });
    const res = await window.api.library.getResume();
    if (res.success) {
      const data = res.data as ItemsResult;
      set({ resumeItems: data.Items, resumeLoading: false });
    } else {
      set({ resumeLoading: false });
    }
  },

  fetchNextUp: async () => {
    set({ nextUpLoading: true });
    const res = await window.api.library.getNextUp();
    if (res.success) {
      const data = res.data as ItemsResult;
      set({ nextUpItems: data.Items, nextUpLoading: false });
    } else {
      set({ nextUpLoading: false });
    }
  },

  clearCurrentItems: () => {
    set({ currentItems: [], currentItemsTotal: 0 });
  },
}));
