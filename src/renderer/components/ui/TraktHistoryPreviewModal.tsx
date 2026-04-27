import { useCallback, useEffect, useMemo, useState } from 'react';
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

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const res = await window.api.trakt.fetchPreview();
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
    })();
    return () => { cancelled = true; };
  }, []);

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
    const res = await window.api.trakt.applyWatchedState(ids);
    setApplying(false);
    if (res.success && res.data) {
      const { applied, failed } = res.data;
      addToast(
        failed > 0
          ? `Applied ${applied}, ${failed} failed`
          : `Applied ${applied} watched item${applied === 1 ? '' : 's'}`,
        failed > 0 ? 'error' : 'success',
      );
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
          <div className={styles.error}>
            {error}
            <button className={styles.cancelBtn} onClick={onClose}>Close</button>
          </div>
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

            <div className={styles.footer}>
              <button className={styles.cancelBtn} onClick={onClose} disabled={applying}>Skip</button>
              <button
                className={styles.applyBtn}
                onClick={handleApply}
                disabled={applying || totalToApply === 0}
              >
                {applying ? 'Applying…' : `Apply (${totalToApply})`}
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
