import { formatFileSize, formatBitrate } from '../../utils/format';
import type { MediaSource } from '../../api/types';
import styles from './MediaSourcePicker.module.css';

interface Props {
  sources: MediaSource[];
  onSelect: (source: MediaSource) => void;
  onCancel: () => void;
}

export default function MediaSourcePicker({ sources, onSelect, onCancel }: Props) {
  return (
    <div className={styles.backdrop} onClick={onCancel}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div className={styles.title}>Choose Version</div>
          <div className={styles.subtitle}>{sources.length} versions available</div>
        </div>
        <div className={styles.list}>
          {sources.map((s) => {
            const video = s.MediaStreams?.find((m) => m.Type === 'Video');
            return (
              <button key={s.Id} className={styles.sourceItem} onClick={() => onSelect(s)}>
                <div className={styles.sourceInfo}>
                  <div className={styles.sourceName}>{s.Name}</div>
                  <div className={styles.sourceMeta}>
                    {video?.DisplayTitle ?? video?.Codec ?? s.Container?.toUpperCase()}
                    {' \u00b7 '}{formatFileSize(s.Size)}
                    {' \u00b7 '}{formatBitrate(s.Bitrate)}
                  </div>
                </div>
                {s.SupportsDirectPlay && <span className={styles.directBadge}>Direct Play</span>}
              </button>
            );
          })}
        </div>
        <button className={styles.cancelBtn} onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}
