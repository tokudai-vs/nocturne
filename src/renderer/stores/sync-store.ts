import { create } from 'zustand';
import type { SyncProgress, SyncStatus } from '../api/types';

interface SyncState {
  running: boolean;
  phase: string | null;
  progress: SyncProgress | null;
  lastFullSync: string | null;
  hasCachedData: boolean;
  completed: boolean;
  error: string | null;

  setProgress: (progress: SyncProgress) => void;
  setComplete: () => void;
  setError: (message: string) => void;
  setStatus: (status: SyncStatus) => void;
  reset: () => void;
}

export const useSyncStore = create<SyncState>((set) => ({
  running: false,
  phase: null,
  progress: null,
  lastFullSync: null,
  hasCachedData: false,
  completed: false,
  error: null,

  setProgress: (progress) =>
    set({ running: true, phase: progress.phase, progress, completed: false, error: null }),

  setComplete: () =>
    set({ running: false, phase: null, progress: null, completed: true, error: null }),

  setError: (message) =>
    set({ running: false, error: message }),

  setStatus: (status) =>
    set({
      running: status.running,
      phase: status.phase,
      progress: status.progress,
      lastFullSync: status.lastFullSync,
      hasCachedData: status.hasCachedData,
    }),

  reset: () =>
    set({ running: false, phase: null, progress: null, completed: false, error: null }),
}));
