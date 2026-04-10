import { useNavigate } from 'react-router-dom';
import { Check } from 'lucide-react';
import { buildImageUrl } from '../../utils/image-url';
import { formatRuntime } from '../../utils/format';
import type { BaseItemDto } from '../../api/types';
import styles from './MediaCard.module.css';
import { useState } from 'react';

interface Props {
  item: BaseItemDto;
  orientation?: 'portrait' | 'landscape';
  onClick?: () => void;
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

export default function MediaCard({ item, orientation = 'portrait', onClick }: Props) {
  const navigate = useNavigate();
  const [imgError, setImgError] = useState(false);
  const [imgLoaded, setImgLoaded] = useState(false);
  const isLandscape = orientation === 'landscape';

  const src = !imgError ? resolveImageSrc(item, isLandscape) : '';

  const playedPct = item.UserData?.PlayedPercentage ?? 0;
  const played = item.UserData?.Played ?? false;
  const rating = item.CommunityRating ? item.CommunityRating.toFixed(1) : null;

  const episodeLabel =
    item.Type === 'Episode' && item.ParentIndexNumber != null && item.IndexNumber != null
      ? `S${String(item.ParentIndexNumber).padStart(2, '0')}E${String(item.IndexNumber).padStart(2, '0')}`
      : null;

  const runtime = formatRuntime(item.RunTimeTicks);

  const handleClick = () => {
    if (onClick) onClick();
    else navigate(`/detail/${item.Id}`);
  };

  return (
    <div className={`${styles.card} ${isLandscape ? styles.landscape : styles.portrait}`}>
      {isLandscape && item.SeriesName && (
        <div className={styles.seriesName}>{item.SeriesName}</div>
      )}
      <div className={styles.imageWrap} onClick={handleClick}>
        {src ? (
          <img
            src={src}
            alt={item.Name}
            className={`${styles.image} ${imgLoaded ? styles.imageLoaded : ''}`}
            loading="lazy"
            onLoad={() => setImgLoaded(true)}
            onError={() => setImgError(true)}
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

        {played && (
          <div className={styles.playedBadge}><Check size={14} strokeWidth={3} /></div>
        )}

        {playedPct > 0 && playedPct < 100 && (
          <div className={styles.progressBar}>
            <div className={styles.progressFill} style={{ width: `${playedPct}%` }} />
          </div>
        )}
      </div>
    </div>
  );
}
