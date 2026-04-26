import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { Play, Eye, EyeOff, Heart, HeartOff, X } from 'lucide-react';
import { useContextMenuStore } from '../../stores/context-menu-store';
import { useLibraryStore } from '../../stores/library-store';
import { useToastStore } from '../../stores/toast-store';
import styles from './ContextMenu.module.css';

export default function ContextMenu() {
  const { visible, x, y, item, close } = useContextMenuStore();
  const addToast = useToastStore((s) => s.addToast);
  const fetchResume = useLibraryStore((s) => s.fetchResume);
  const navigate = useNavigate();
  const menuRef = useRef<HTMLDivElement>(null);

  // Close on click outside
  useEffect(() => {
    if (!visible) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) close();
    };
    const timer = setTimeout(() => document.addEventListener('mousedown', handler), 0);
    return () => {
      clearTimeout(timer);
      document.removeEventListener('mousedown', handler);
    };
  }, [visible, close]);

  // Close on ESC
  useEffect(() => {
    if (!visible) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [visible, close]);

  if (!visible || !item) return null;

  const isPlayed = item.UserData?.Played ?? false;
  const isFavorite = item.UserData?.IsFavorite ?? false;
  const hasProgress = (item.UserData?.PlaybackPositionTicks ?? 0) > 0;
  const isPlayable = item.Type === 'Movie' || item.Type === 'Episode';

  // Keep menu within viewport
  const estW = 240;
  const estH = hasProgress ? 220 : 170;
  const adjX = x + estW > window.innerWidth ? Math.max(8, x - estW) : x;
  const adjY = y + estH > window.innerHeight ? Math.max(8, y - estH) : y;

  const handlePlay = () => {
    close();
    navigate(`/detail/${item.Id}`);
  };

  const handleMarkPlayed = async () => {
    close();
    if (isPlayed) {
      await window.api.item.markUnplayed({ itemId: item.Id, serverId: item.serverId });
      addToast('Marked as unwatched', 'success');
    } else {
      await window.api.item.markPlayed({ itemId: item.Id, serverId: item.serverId });
      addToast('Marked as watched', 'success');
    }
    fetchResume();
  };

  const handleToggleFavorite = async () => {
    close();
    await window.api.item.toggleFavorite({
      itemId: item.Id,
      serverId: item.serverId,
      isFavorite: !isFavorite,
    });
    addToast(isFavorite ? 'Removed from favorites' : 'Added to favorites', 'success');
  };

  const handleRemoveFromContinue = async () => {
    close();
    await window.api.item.removeFromContinue({ itemId: item.Id, serverId: item.serverId });
    addToast('Removed from Continue Watching', 'success');
    fetchResume();
  };

  return (
    <div ref={menuRef} className={styles.menu} style={{ top: adjY, left: adjX }}>
      {isPlayable && (
        <>
          <button className={styles.item} onClick={handlePlay}>
            <Play size={14} />
            <span>Play</span>
          </button>
          <div className={styles.separator} />
        </>
      )}

      <button className={styles.item} onClick={handleMarkPlayed}>
        {isPlayed ? <EyeOff size={14} /> : <Eye size={14} />}
        <span>{isPlayed ? 'Mark as Unwatched' : 'Mark as Watched'}</span>
      </button>

      <button className={styles.item} onClick={handleToggleFavorite}>
        {isFavorite ? <HeartOff size={14} /> : <Heart size={14} />}
        <span>{isFavorite ? 'Remove from Favorites' : 'Add to Favorites'}</span>
      </button>

      {hasProgress && (
        <>
          <div className={styles.separator} />
          <button className={`${styles.item} ${styles.danger}`} onClick={handleRemoveFromContinue}>
            <X size={14} />
            <span>Remove from Continue Watching</span>
          </button>
        </>
      )}
    </div>
  );
}
