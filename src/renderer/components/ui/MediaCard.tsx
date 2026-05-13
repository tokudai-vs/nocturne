import { useNavigate } from 'react-router-dom';
import { Check } from 'lucide-react';
import { buildImageUrl } from '../../utils/image-url';
import { formatRuntime } from '../../utils/format';
import type { BaseItemDto } from '../../api/types';
import { useContextMenuStore } from '../../stores/context-menu-store';
import styles from './MediaCard.module.css';
import { useMemo, useState } from 'react';

// Session-level cache of image URLs known to 404/fail. Prevents the same
// broken primary from being re-attempted on every card mount, and lets us
// pre-skip past it to the first working fallback. In-memory only — never
// persisted; cleared on full reload.
const KNOWN_BAD_IMAGE_URLS = new Set<string>();

interface Props {
  item: BaseItemDto & {
    versionCount?: number;
    isExternal?: boolean;
    traktKey?: string;
    traktType?: 'movie' | 'show';
  };
  orientation?: 'portrait' | 'landscape';
  onClick?: () => void;
  /**
   * If set, called when the card represents an external Trakt-watchlist
   * item that isn't in the library. Lets parents render a custom info modal
   * instead of navigating to a non-existent detail page.
   */
  onExternalClick?: (item: Props['item']) => void;
}

function resolveImageSrc(item: BaseItemDto, isLandscape: boolean): string {
  if (isLandscape) {
    if (item.Type === 'Episode') {
      if (item.ImageTags?.['Thumb']) {
        return buildImageUrl(item.Id, 'Thumb', { maxWidth: 500, tag: item.ImageTags['Thumb'] });
      }
      if (item.ImageTags?.['Primary']) {
        return buildImageUrl(item.Id, 'Primary', { maxWidth: 500, tag: item.ImageTags['Primary'] });
      }
      if (item.BackdropImageTags?.[0]) {
        return buildImageUrl(item.Id, 'Backdrop', { maxWidth: 500, tag: item.BackdropImageTags[0] });
      }
      if (item.ParentThumbItemId && item.ParentThumbImageTag) {
        return buildImageUrl(item.ParentThumbItemId, 'Thumb', { maxWidth: 500, tag: item.ParentThumbImageTag });
      }
      if (item.SeriesId) {
        return buildImageUrl(item.SeriesId, 'Primary', { maxWidth: 500 });
      }
      return '';
    }
    if (item.BackdropImageTags?.[0]) {
      return buildImageUrl(item.Id, 'Backdrop', { maxWidth: 500, tag: item.BackdropImageTags[0] });
    }
    if (item.ImageTags?.['Thumb']) {
      return buildImageUrl(item.Id, 'Thumb', { maxWidth: 500, tag: item.ImageTags['Thumb'] });
    }
    return buildImageUrl(item.Id, 'Primary', { maxWidth: 500, tag: item.ImageTags?.['Primary'] });
  }

  return buildImageUrl(item.Id, 'Primary', { maxWidth: 300, tag: item.ImageTags?.['Primary'] });
}

export default function MediaCard({ item, orientation = 'portrait', onClick, onExternalClick }: Props) {
  const navigate = useNavigate();
  const isLandscape = orientation === 'landscape';
  const isExternal = item.isExternal === true;

  // Build the full URL chain once per item: [primary, ...fallbacks], filtered
  // through KNOWN_BAD_IMAGE_URLS so we never re-attempt a URL we've already
  // confirmed dead in this session.
  const chain = useMemo(() => {
    const primary = resolveImageSrc(item, isLandscape);
    const fallbacks = (item.ImageFallbacks ?? []).filter(Boolean);
    const all = (primary ? [primary, ...fallbacks] : fallbacks)
      .filter((u) => u && !KNOWN_BAD_IMAGE_URLS.has(u));
    return all;
    // ImageFallbacks identity is stable per-item from the IPC payload.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [item.Id, item.ImageFallbacks, isLandscape]);

  const [attempt, setAttempt] = useState(0);
  const [imgLoaded, setImgLoaded] = useState(false);
  const [exhausted, setExhausted] = useState(false);

  const src = !exhausted && attempt < chain.length ? chain[attempt] : '';

  const onImgError = () => {
    const failed = chain[attempt];
    if (failed) KNOWN_BAD_IMAGE_URLS.add(failed);
    if (attempt + 1 < chain.length) {
      // eslint-disable-next-line no-console
      console.log(`[image-fallback] card ${item.Id} URL failed, advancing to fallback ${attempt}/${chain.length - 1}`);
      setAttempt(attempt + 1);
    } else {
      // eslint-disable-next-line no-console
      console.log(`[image-fallback] card ${item.Id} all ${chain.length} URL(s) exhausted, showing letter placeholder`);
      setExhausted(true);
    }
  };

  const playedPct = item.UserData?.PlayedPercentage ?? 0;
  const played = item.UserData?.Played ?? false;
  const rating = item.CommunityRating ? item.CommunityRating.toFixed(1) : null;

  const episodeLabel =
    item.Type === 'Episode' && item.ParentIndexNumber != null && item.IndexNumber != null
      ? `S${String(item.ParentIndexNumber).padStart(2, '0')}E${String(item.IndexNumber).padStart(2, '0')}`
      : null;

  const runtime = formatRuntime(item.RunTimeTicks);

  const handleClick = () => {
    // External (Trakt-only) items have no detail page — defer to parent handler.
    if (isExternal) {
      onExternalClick?.(item);
      return;
    }
    if (onClick) onClick();
    else navigate(`/detail/${item.Id}`);
  };

  const openContextMenu = useContextMenuStore((s) => s.open);
  const handleContextMenu = (e: React.MouseEvent) => {
    if (isExternal) return; // No context-menu actions apply to external rows
    e.preventDefault();
    openContextMenu(e.clientX, e.clientY, item);
  };

  return (
    <div className={`${styles.card} ${isLandscape ? styles.landscape : styles.portrait}`} onContextMenu={handleContextMenu}>
      {isLandscape && (
        <div className={styles.seriesName}>
          {item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name}
        </div>
      )}
      <div
        className={`${styles.imageWrap} ${isExternal ? styles.externalDimmed : ''}`}
        onClick={handleClick}
      >
        {src ? (
          <img
            src={src}
            alt={item.Name}
            className={`${styles.image} ${imgLoaded ? styles.imageLoaded : ''}`}
            loading="lazy"
            onLoad={() => setImgLoaded(true)}
            onError={onImgError}
            key={src}
          />
        ) : (
          <div className={styles.fallback}>
            <span>{item.Name.charAt(0)}</span>
          </div>
        )}

        <div className={styles.overlay}>
          <div className={styles.overlayContent}>
            <div className={styles.title}>{item.Name}</div>
            <div className={styles.meta}>
              {item.ProductionYear && <span>{item.ProductionYear}</span>}
              {episodeLabel && <span>{episodeLabel}</span>}
              {runtime && <span>{runtime}</span>}
              {rating && <span className={styles.rating}>{rating}</span>}
            </div>
          </div>
        </div>

        {played && !isExternal && (
          <div className={styles.playedBadge}><Check size={14} strokeWidth={3} /></div>
        )}

        {item.versionCount && item.versionCount > 1 && !isExternal && (
          <div className={styles.versionBadge}>{item.versionCount} versions</div>
        )}

        {isExternal && (
          <div className={styles.externalBadge}>Not in library</div>
        )}

        {playedPct > 0 && playedPct < 100 && !isExternal && (
          <div className={styles.progressBar}>
            <div className={styles.progressFill} style={{ width: `${playedPct}%` }} />
          </div>
        )}
      </div>
    </div>
  );
}
