import { create } from 'zustand';
import type { BaseItemDto } from '../api/types';

interface PlayerState {
  isPlaying: boolean;
  currentItem: BaseItemDto | null;
  error: string | null;

  setPlaying: (playing: boolean) => void;
  setCurrentItem: (item: BaseItemDto | null) => void;
  setError: (error: string | null) => void;
  reset: () => void;
}

export const usePlayerStore = create<PlayerState>((set) => ({
  isPlaying: false,
  currentItem: null,
  error: null,

  setPlaying: (playing) => set({ isPlaying: playing }),
  setCurrentItem: (item) => set({ currentItem: item }),
  setError: (error) => set({ error }),
  reset: () => set({ isPlaying: false, currentItem: null, error: null }),
}));
