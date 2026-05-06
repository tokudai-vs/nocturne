import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Check } from 'lucide-react';
import { useToastStore } from '../../stores/toast-store';
import type { TraktHistoryPreview, TraktMatchedEpisode, TraktMatchedMovie } from '../../api/types';
import styles from './TraktHistoryPreviewModal.module.css';

interface Props {
  username: string | null;
  onClose: () => void;
  onApplied?: (count: number) => void;
}

type Section = 'movies' | 'episodes';

export default function TraktHistoryPreviewModal({ username, onClose, onApplied }: Props) {
  const addToast = useToastStore((s) => s.addToast);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<TraktHistoryPreview | null>(null);
  const [moviesEnabled, setMoviesEnabled] = useState(true);
  const [episodesEnabled, setEpisodesEnabled] = useState(true);
  const [showList, setShowList] = useState<Section | null>(null);
  const [moviesSelected, setMoviesSelected] = useState<Set<string>>(new Set());
  const [episodesSelected, setEpisodesSelected] = useState<Set<string>>(new Set());
  const [applying, setApplying] = useState(false);
  const [applyProgress, setApplyProgress] = useState<{ current: number; total: number } | null>(null);
  // Increment to retry the fetch after a failure. The fetch effect depends on
  // this so re-clicking Try Again re-runs it.
  const [attempt, setAttempt] = useState(0);

  // Subscribe to main-process apply-progress events for the lifetime of the
  // modal. Cheap; the listener is a no-op when not applying.
  useEffect(() => {
    const off = window.api.trakt.onApplyProgress((data) => {
      setApplyProgress(data);
    });
    return off;
  }, []);

  // If the modal unmounts while an apply is in flight, tell main to abort.
  // The local SQLite update has already happened (Phase 2 of the pipeline),
  // so the user-visible "watched" state is consistent regardless.
  const applyingRef = useRef(false);
  applyingRef.current = applying;
  useEffect(() => {
    return () => {
      if (applyingRef.current) {
        void window.api.trakt.cancelApply();
      }
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    const t0 = Date.now();
    console.log('[trakt-preview-modal] mount — calling window.api.trakt.fetchPreview()');
    (async () => {
      try {
        const res = await window.api.trakt.fetchPreview();
        console.log(`[trakt-preview-modal] fetchPreview returned after ${Date.now() - t0}ms`, res);
        if (cancelled) return;
        if (!res.success || !res.data) {
          setError(res.error ?? 'Failed to fetch Trakt history');
          setLoading(false);
          return;
        }
        const p = res.data;
        setPreview(p);
        // Default: all unplayed matches selected.
        const ms = new Set<string>();
        for (const m of p.movies.items) if (!m.alreadyPlayed) ms.add(movieKey(m));
        const es = new Set<string>();
        for (const e of p.episodes.items) if (!e.alreadyPlayed) es.add(episodeKey(e));
        setMoviesSelected(ms);
        setEpisodesSelected(es);
        setLoading(false);
      } catch (e) {
        // Defensive: IPC bridge shouldn't throw (it returns {success,error}),
        // but if anything unexpected does, surface it instead of hanging.
        console.error('[trakt-preview-modal] unexpected exception:', e);
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Unexpected error');
        setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [attempt]);

  const moviesEligibleCount = useMemo(
    () => preview ? preview.movies.items.filter((m) => !m.alreadyPlayed).length : 0,
    [preview],
  );
  const episodesEligibleCount = useMemo(
    () => preview ? preview.episodes.items.filter((e) => !e.alreadyPlayed).length : 0,
    [preview],
  );

  const collectIdsToApply = useCallback((): string[] => {
    if (!preview) return [];
    const ids: string[] = [];
    if (moviesEnabled) {
      for (const m of preview.movies.items) {
        if (m.alreadyPlayed) continue;
        if (moviesSelected.has(movieKey(m))) ids.push(...m.embyIds);
      }
    }
    if (episodesEnabled) {
      for (const e of preview.episodes.items) {
        if (e.alreadyPlayed) continue;
        if (episodesSelected.has(episodeKey(e))) ids.push(...e.embyIds);
      }
    }
    return ids;
  }, [preview, moviesEnabled, episodesEnabled, moviesSelected, episodesSelected]);

  const totalToApply = useMemo(() => {
    if (!preview) return 0;
    let n = 0;
    if (moviesEnabled) {
      for (const m of preview.movies.items) {
        if (!m.alreadyPlayed && moviesSelected.has(movieKey(m))) n++;
      }
    }
    if (episodesEnabled) {
      for (const e of preview.episodes.items) {
        if (!e.alreadyPlayed && episodesSelected.has(episodeKey(e))) n++;
      }
    }
    return n;
  }, [preview, moviesEnabled, episodesEnabled, moviesSelected, episodesSelected]);

  const handleApply = useCallback(async () => {
    const ids = collectIdsToApply();
    if (ids.length === 0) {
      addToast('Nothing selected to apply', 'error');
      return;
    }
    setApplying(true);
    setApplyProgress({ current: 0, total: 0 });
    const res = await window.api.trakt.applyWatchedState(ids);
    setApplying(false);
    setApplyProgress(null);
    if (res.success && res.data) {
      const { applied, failed, cancelled } = res.data;
      const msg = cancelled
        ? `Cancelled — applied ${applied} so far`
        : failed > 0
          ? `Applied ${applied}, ${failed} failed`
          : `Applied ${applied} watched item${applied === 1 ? '' : 's'}`;
      addToast(msg, failed > 0 || cancelled ? 'error' : 'success');
      onApplied?.(applied);
      onClose();
    } else {
      addToast(`Apply failed: ${res.error ?? 'unknown error'}`, 'error');
    }
  }, [collectIdsToApply, addToast, onApplied, onClose]);

  const toggleMovieSelected = (m: TraktMatchedMovie) => {
    const key = movieKey(m);
    setMoviesSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  };
  const toggleEpisodeSelected = (e: TraktMatchedEpisode) => {
    const key = episodeKey(e);
    setEpisodesSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  };

  return (
    <div className={styles.backdrop} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <h2 className={styles.title}>Trakt connected</h2>
        {username && <div className={styles.subtitle}>as @{username}</div>}

        {loading && <div className={styles.loading}>Looking up your Trakt history…</div>}

        {error && (
          <>
            <div className={styles.error}>{error}</div>
            <div className={styles.footer}>
              <button className={styles.cancelBtn} onClick={onClose}>Skip</button>
              <button
                className={styles.applyBtn}
                onClick={() => {
                  setError(null);
                  setLoading(true);
                  setAttempt((a) => a + 1);
                }}
              >
                Try again
              </button>
            </div>
          </>
        )}

        {!loading && !error && preview && (
          <>
            <p className={styles.intro}>
              We found:
            </p>
            <ul className={styles.summary}>
              <li>
                <strong>{preview.movies.totalOnTrakt}</strong> watched movies
                {' '}({preview.movies.matchedInLibrary} in your library)
              </li>
              <li>
                <strong>{preview.episodes.totalOnTrakt}</strong> watched episodes
                {' '}({preview.episodes.matchedInLibrary} in your library)
              </li>
            </ul>

            <p className={styles.intro}>Apply Trakt watched state to your library?</p>

            {moviesEligibleCount > 0 && (
              <label className={styles.bulkRow}>
                <input
                  type="checkbox"
                  checked={moviesEnabled}
                  onChange={(e) => setMoviesEnabled(e.target.checked)}
                />
                <span>Movies ({moviesEligibleCount})</span>
                <button
                  className={styles.linkBtn}
                  onClick={() => setShowList(showList === 'movies' ? null : 'movies')}
                >
                  {showList === 'movies' ? 'Hide list' : 'Show list'}
                </button>
              </label>
            )}
            {showList === 'movies' && preview.movies.items.length > 0 && (
              <div className={styles.list}>
                {preview.movies.items.map((m) => {
                  const key = movieKey(m);
                  const checked = moviesSelected.has(key);
                  return (
                    <label
                      key={key}
                      className={`${styles.listRow} ${m.alreadyPlayed ? styles.alreadyPlayed : ''}`}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={m.alreadyPlayed}
                        onChange={() => toggleMovieSelected(m)}
                      />
                      <span className={styles.listName}>
                        {m.title}{m.year ? ` (${m.year})` : ''}
                      </span>
                      {m.alreadyPlayed && (
                        <span className={styles.alreadyBadge}>
                          <Check size={11} strokeWidth={3} /> already played
                        </span>
                      )}
                    </label>
                  );
                })}
              </div>
            )}

            {episodesEligibleCount > 0 && (
              <label className={styles.bulkRow}>
                <input
                  type="checkbox"
                  checked={episodesEnabled}
                  onChange={(e) => setEpisodesEnabled(e.target.checked)}
                />
                <span>Episodes ({episodesEligibleCount})</span>
                <button
                  className={styles.linkBtn}
                  onClick={() => setShowList(showList === 'episodes' ? null : 'episodes')}
                >
                  {showList === 'episodes' ? 'Hide list' : 'Show list'}
                </button>
              </label>
            )}
            {showList === 'episodes' && preview.episodes.items.length > 0 && (
              <div className={styles.list}>
                {preview.episodes.items.map((e) => {
                  const key = episodeKey(e);
                  const checked = episodesSelected.has(key);
                  return (
                    <label
                      key={key}
                      className={`${styles.listRow} ${e.alreadyPlayed ? styles.alreadyPlayed : ''}`}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={e.alreadyPlayed}
                        onChange={() => toggleEpisodeSelected(e)}
                      />
                      <span className={styles.listName}>
                        {e.showTitle} S{String(e.season).padStart(2, '0')}E{String(e.episode).padStart(2, '0')}
                      </span>
                      {e.alreadyPlayed && (
                        <span className={styles.alreadyBadge}>
                          <Check size={11} strokeWidth={3} /> already played
                        </span>
                      )}
                    </label>
                  );
                })}
              </div>
            )}

            {moviesEligibleCount === 0 && episodesEligibleCount === 0 && (
              <div className={styles.emptyState}>
                Nothing new to apply — your Trakt history matches your local watched state.
              </div>
            )}

            {applying && applyProgress && applyProgress.total > 0 && (
              <div className={styles.progressBar}>
                <div
                  className={styles.progressFill}
                  style={{
                    width: `${Math.min(100, Math.round((applyProgress.current / applyProgress.total) * 100))}%`,
                  }}
                />
              </div>
            )}
            <div className={styles.footer}>
              <button className={styles.cancelBtn} onClick={onClose}>
                {applying ? 'Cancel' : 'Skip'}
              </button>
              <button
                className={styles.applyBtn}
                onClick={handleApply}
                disabled={applying || totalToApply === 0}
              >
                {applying
                  ? applyProgress && applyProgress.total > 0
                    ? `Applying ${applyProgress.current.toLocaleString()} of ${applyProgress.total.toLocaleString()}…`
                    : 'Applying…'
                  : `Apply (${totalToApply.toLocaleString()})`}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function movieKey(m: TraktMatchedMovie): string {
  return m.tmdbId ? `m:${m.tmdbId}` : `m:imdb:${m.imdbId ?? ''}`;
}

function episodeKey(e: TraktMatchedEpisode): string {
  return `e:${e.showTmdbId}:${e.season}:${e.episode}`;
}
