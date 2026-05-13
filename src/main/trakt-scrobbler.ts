import { EventEmitter } from 'events';
import { traktClient } from './trakt-client';
import {
  clearTraktQueue,
  countTraktQueue,
  deleteTraktScrobble,
  enqueueTraktScrobble,
  getItem as dbGetItem,
  getNextTraktScrobble,
  markTraktScrobbleAttempt,
  pruneStaleTraktQueue,
  type ItemRow,
} from './database';
import { getSettingValue } from './settings';
import type {
  QueueAction,
  ScrobbleAction,
  ScrobbleEpisodePayload,
  ScrobbleMoviePayload,
  ScrobblePayload,
} from './trakt-types';

const MIN_RETRY_DELAY_MS = 30 * 1000;
const MAX_RETRY_DELAY_MS = 30 * 60 * 1000;
const MAX_ATTEMPTS = 10;
const INITIAL_DRAIN_DELAY_MS = 5_000;

class TraktScrobbler extends EventEmitter {
  private drainTimer: ReturnType<typeof setTimeout> | null = null;
  private draining = false;

  init(): void {
    const pruned = pruneStaleTraktQueue();
    if (pruned.lowProgress > 0) {
      console.log(`[trakt] pruned ${pruned.lowProgress} stale queue events (progress=0 stop/pause)`);
    }
    if (pruned.pauseHighProgress > 0) {
      console.log(`[trakt] pruned ${pruned.pauseHighProgress} stale queue events (pause action with progress >= 80%)`);
    }
    // Wait briefly so app/network can settle before draining queued scrobbles.
    this.scheduleDrain(INITIAL_DRAIN_DELAY_MS);
  }

  getQueueCount(): number {
    return countTraktQueue();
  }

  /** Wipe the entire failed-event queue. Returns the number of entries cleared. */
  clearQueue(): number {
    const count = countTraktQueue();
    clearTraktQueue();
    return count;
  }

  /** Public scrobble entry. Silently no-ops when disabled / not connected / unsupported item. */
  async scrobble(
    action: ScrobbleAction,
    itemId: string,
    positionSec: number,
    durationSec: number,
  ): Promise<void> {
    if (!this.autoScrobbleEnabled()) return;
    if (!traktClient.isConnected()) return;

    const item = dbGetItem(itemId);
    if (!item) {
      console.log(`[trakt-scrobbler] skip ${action}: ${itemId} not in cache`);
      return;
    }
    let progress = this.progressPct(positionSec, durationSec);
    // Trakt requires progress >= 1.0% on stop/pause (422 otherwise). On ESC
    // the position polling loop may have already stopped, leaving stale
    // values, but if we're sending stop the user has been watching — clamp
    // up so the event isn't rejected.
    if ((action === 'stop' || action === 'pause') && progress < 1) {
      progress = 1;
    }
    // Trakt rejects /scrobble/pause with progress >= 80% — at that threshold
    // it expects /scrobble/stop so the event auto-marks watched. Convert in
    // place so the user-initiated pause still records as a finished watch
    // rather than getting queued + retried forever.
    let effectiveAction = action;
    if (action === 'pause' && progress >= 80) {
      console.log(`[trakt] progress ${progress.toFixed(2)}% — converting pause to stop`);
      effectiveAction = 'stop';
    }
    const payload = this.resolvePayload(item, progress);
    if (!payload) {
      console.log(
        `[trakt-scrobbler] skip ${action}: ${item.type} "${item.name}" lacks Trakt-compatible IDs`,
      );
      return;
    }

    try {
      const result = await traktClient.scrobble(effectiveAction, payload);
      if (result.ok) {
        this.scheduleDrain(0);
      }
    } catch (err) {
      const msg = errorMessage(err);
      console.warn(`[trakt-scrobbler] ${effectiveAction} failed, queueing — ${msg}`);
      enqueueTraktScrobble(effectiveAction, JSON.stringify(payload), itemId, msg);
      this.emit('scrobble-error', { action: effectiveAction, itemId, message: msg });
      this.scheduleDrain(MIN_RETRY_DELAY_MS);
    }
  }

  /**
   * Enqueue a non-scrobble Trakt action (Phase 2 history push). Used by the
   * sync engine when a /sync/history POST fails.
   */
  enqueueHistoryAction(action: 'history-add' | 'history-remove', body: Record<string, unknown>, embyId: string | null, lastError: string | null): void {
    enqueueTraktScrobble(action, JSON.stringify(body), embyId, lastError);
    this.scheduleDrain(MIN_RETRY_DELAY_MS);
  }

  /** Drain queued scrobbles + history actions. Idempotent via internal lock. */
  async drainQueue(): Promise<void> {
    if (this.draining) return;
    if (!traktClient.isConnected()) return;
    this.draining = true;
    try {
      while (true) {
        const next = getNextTraktScrobble();
        if (!next) break;
        if (next.next_retry_at && new Date(next.next_retry_at).getTime() > Date.now()) {
          this.scheduleDrain(new Date(next.next_retry_at).getTime() - Date.now());
          break;
        }
        let parsed: unknown;
        try {
          parsed = JSON.parse(next.payload);
        } catch {
          console.warn(`[trakt-scrobbler] dropping malformed queue entry ${next.id}`);
          deleteTraktScrobble(next.id);
          continue;
        }

        try {
          const action = next.action as QueueAction;
          if (action === 'history-add') {
            await traktClient.addHistory(parsed as Record<string, unknown>);
            deleteTraktScrobble(next.id);
            continue;
          }
          if (action === 'history-remove') {
            await traktClient.removeHistory(parsed as Record<string, unknown>);
            deleteTraktScrobble(next.id);
            continue;
          }
          // Default: scrobble action (start/pause/stop)
          const result = await traktClient.scrobble(action as ScrobbleAction, parsed as ScrobblePayload);
          // Both ok and 404/409 are terminal — drop from queue.
          if (result.ok || result.status === 404 || result.status === 409) {
            deleteTraktScrobble(next.id);
            continue;
          }
          deleteTraktScrobble(next.id);
        } catch (err) {
          const attempts = next.attempts + 1;
          if (attempts >= MAX_ATTEMPTS) {
            console.warn(
              `[trakt-scrobbler] dropping after ${attempts} attempts: ${errorMessage(err)}`,
            );
            deleteTraktScrobble(next.id);
            continue;
          }
          const delay = Math.min(
            MIN_RETRY_DELAY_MS * Math.pow(2, attempts - 1),
            MAX_RETRY_DELAY_MS,
          );
          const nextRetry = new Date(Date.now() + delay).toISOString();
          markTraktScrobbleAttempt(next.id, nextRetry, errorMessage(err));
          this.scheduleDrain(delay);
          break;
        }
      }
    } finally {
      this.draining = false;
    }
  }

  // ── Internals ──────────────────────────────────────

  private autoScrobbleEnabled(): boolean {
    const v = getSettingValue('traktAutoScrobble');
    return v === undefined ? true : Boolean(v);
  }

  private progressPct(positionSec: number, durationSec: number): number {
    if (!durationSec || durationSec <= 0) return 0;
    return Math.max(0, Math.min(100, (positionSec / durationSec) * 100));
  }

  private resolvePayload(item: ItemRow, progress: number): ScrobblePayload | null {
    if (item.type === 'Movie') {
      const tmdb = item.tmdb_id ? Number(item.tmdb_id) : undefined;
      const imdb = item.imdb_id || undefined;
      if (!tmdb && !imdb) return null;
      const out: ScrobbleMoviePayload = {
        movie: { ids: { tmdb, imdb } },
        progress,
      };
      return out;
    }
    if (item.type === 'Episode') {
      if (!item.series_id || item.season_number == null || item.episode_number == null) {
        return null;
      }
      const series = dbGetItem(item.series_id);
      if (!series) return null;
      const tmdb = series.tmdb_id ? Number(series.tmdb_id) : undefined;
      const imdb = series.imdb_id || undefined;
      const tvdb = series.tvdb_id ? Number(series.tvdb_id) : undefined;
      if (!tmdb && !imdb && !tvdb) return null;
      const out: ScrobbleEpisodePayload = {
        show: { ids: { tmdb, imdb, tvdb } },
        episode: { season: item.season_number, number: item.episode_number },
        progress,
      };
      return out;
    }
    return null;
  }

  private scheduleDrain(delayMs: number): void {
    if (this.drainTimer) clearTimeout(this.drainTimer);
    this.drainTimer = setTimeout(() => {
      this.drainTimer = null;
      void this.drainQueue();
    }, Math.max(0, delayMs));
  }
}

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message;
  if (typeof err === 'string') return err;
  return 'unknown';
}

export const traktScrobbler = new TraktScrobbler();
