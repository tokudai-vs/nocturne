import { useState } from 'react';
import { Search, ExternalLink, Trash2 } from 'lucide-react';
import { useToastStore } from '../../stores/toast-store';
import styles from './TraktExternalItemModal.module.css';

interface Props {
  title: string;
  year: number | null;
  overview: string | null;
  tmdbId: string | null;
  imdbId: string | null;
  traktType: 'movie' | 'show';
  traktKey?: string;
  onClose: () => void;
  onRemoved?: () => void;
}

export default function TraktExternalItemModal({
  title, year, overview, tmdbId, imdbId, traktType, traktKey, onClose, onRemoved,
}: Props) {
  const addToast = useToastStore((s) => s.addToast);
  const [removing, setRemoving] = useState(false);

  const tmdbType = traktType === 'show' ? 'tv' : 'movie';
  const tmdbUrl = tmdbId
    ? `https://www.themoviedb.org/${tmdbType}/${tmdbId}`
    : `https://www.themoviedb.org/search?query=${encodeURIComponent(title)}`;
  const imdbUrl = imdbId
    ? `https://www.imdb.com/title/${imdbId}/`
    : `https://www.imdb.com/find/?q=${encodeURIComponent(title)}`;

  const handleOpenExternal = (url: string) => {
    void window.api.trakt.openVerification(url);
  };

  const handleRemove = async () => {
    if (!tmdbId) {
      addToast('Cannot remove — no TMDB ID', 'error');
      return;
    }
    setRemoving(true);
    const res = await window.api.trakt.removeFromWatchlist({
      traktType, tmdbId, key: traktKey,
    });
    setRemoving(false);
    if (res.success && res.data?.ok) {
      addToast(`Removed ${title} from Trakt watchlist`, 'success');
      onRemoved?.();
      onClose();
    } else {
      addToast(`Remove failed: ${res.data?.error ?? res.error ?? 'unknown error'}`, 'error');
    }
  };

  return (
    <div className={styles.backdrop} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <h2 className={styles.title}>
          {title}{year ? ` (${year})` : ''}
        </h2>
        <div className={styles.statusLine}>
          In your Trakt watchlist · Not currently in your library
        </div>
        {overview && <p className={styles.overview}>{overview}</p>}

        <div className={styles.actions}>
          <button className={styles.actionBtn} onClick={() => handleOpenExternal(tmdbUrl)}>
            <Search size={14} /> {tmdbId ? 'Open on TMDB' : 'Search TMDB'}
          </button>
          <button className={styles.actionBtn} onClick={() => handleOpenExternal(imdbUrl)}>
            <ExternalLink size={14} /> {imdbId ? 'Open on IMDB' : 'Search IMDB'}
          </button>
          <button
            className={`${styles.actionBtn} ${styles.actionDanger}`}
            onClick={handleRemove}
            disabled={removing}
          >
            <Trash2 size={14} /> {removing ? 'Removing…' : 'Remove from watchlist'}
          </button>
        </div>

        <button className={styles.cancelBtn} onClick={onClose}>Close</button>
      </div>
    </div>
  );
}
