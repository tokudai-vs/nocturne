import { create } from 'zustand';
import type { NocturneSettings } from '../api/types';

interface SettingsState {
  settings: NocturneSettings | null;
  loading: boolean;
  fetchSettings: () => Promise<void>;
  updateSetting: (key: string, value: unknown) => Promise<void>;
  updateMultiple: (data: Record<string, unknown>) => Promise<void>;
}

export const useSettingsStore = create<SettingsState>((set, get) => ({
  settings: null,
  loading: false,

  fetchSettings: async () => {
    set({ loading: true });
    const res = await window.api.settings.get();
    if (res.success) {
      set({ settings: res.data as NocturneSettings, loading: false });
    } else {
      set({ loading: false });
    }
  },

  updateSetting: async (key, value) => {
    const current = get().settings;
    if (!current) return;
    // Optimistic update
    set({ settings: { ...current, [key]: value } });
    await window.api.settings.set(key, value);
  },

  updateMultiple: async (data) => {
    const current = get().settings;
    if (!current) return;
    set({ settings: { ...current, ...data } as NocturneSettings });
    await window.api.settings.setMultiple(data);
  },
}));
