import { create } from 'zustand';
import type { SyncProgress, SyncStatus } from '../api/types';

interface ServerError {
  serverName: string;
  message: string;
  at: string;
}

interface SyncState {
  running: boolean;
  phase: string | null;
  progress: SyncProgress | null;
  lastFullSync: string | null;
  hasCachedData: boolean;
  completed: boolean;
  error: string | null;
  // Per-server sync failures, keyed by serverId. Cleared for a server once its
  // next sync succeeds (setStatus rebuilds this from serverHealth every poll).
  serverErrors: Record<string, ServerError>;

  setProgress: (progress: SyncProgress) => void;
  setComplete: () => void;
  setPartial: () => void;
  setError: (message: string) => void;
  setServerError: (data: { serverId: string; serverName: string; message: string }) => void;
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
  serverErrors: {},

  setProgress: (progress) =>
    set({ running: true, phase: progress.phase, progress, completed: false, error: null }),

  setComplete: () =>
    set({ running: false, phase: null, progress: null, completed: true, error: null }),

  // Full sync finished its pass but >=1 server failed. Not 'completed' (no
  // checkmark, and cache-authoritative consumers keep treating the sync as
  // unfinished) — but definitely not running, so the ring hides and the
  // per-server failure chip can take over.
  setPartial: () =>
    set({ running: false, phase: null, progress: null, completed: false }),

  setError: (message) =>
    set({ running: false, error: message }),

  setServerError: (data) =>
    set((s) => ({
      serverErrors: {
        ...s.serverErrors,
        [data.serverId]: {
          serverName: data.serverName,
          message: data.message,
          at: new Date().toISOString(),
        },
      },
    })),

  setStatus: (status) =>
    set({
      running: status.running,
      phase: status.phase,
      progress: status.progress,
      lastFullSync: status.lastFullSync,
      hasCachedData: status.hasCachedData,
      // Replace (not merge) so a server that has since synced successfully drops
      // out of the error set. Only 'failed' health entries become errors.
      serverErrors: Object.fromEntries(
        Object.entries(status.serverHealth ?? {})
          .filter(([, h]) => h.status === 'failed')
          .map(([serverId, h]) => [
            serverId,
            { serverName: h.serverName, message: h.message ?? 'Sync failed', at: h.at },
          ]),
      ),
    }),

  reset: () =>
    set({ running: false, phase: null, progress: null, completed: false, error: null, serverErrors: {} }),
}));
