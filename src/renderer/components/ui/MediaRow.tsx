import { useRef } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import MediaCard from './MediaCard';
import MediaCardSkeleton from './MediaCardSkeleton';
import type { BaseItemDto } from '../../api/types';
import styles from './MediaRow.module.css';

interface Props {
  title: string;
  items: BaseItemDto[];
  orientation?: 'portrait' | 'landscape';
  loading?: boolean;
  onSeeAll?: () => void;
  onItemClick?: (item: BaseItemDto) => void;
}

export default function MediaRow({ title, items, orientation = 'portrait', loading, onSeeAll, onItemClick }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);

  if (!loading && items.length === 0) return null;

  const scroll = (dir: 'left' | 'right') => {
    if (!scrollRef.current) return;
    const distance = scrollRef.current.clientWidth * 0.75;
    scrollRef.current.scrollBy({ left: dir === 'left' ? -distance : distance });
  };

  return (
    <section className={styles.section}>
      <div className={styles.header}>
        <h2 className={styles.title}>{title}</h2>
        {onSeeAll && (
          <button className={styles.seeAll} onClick={onSeeAll}>
            See All
          </button>
        )}
      </div>
      <div className={styles.rowWrap}>
        <div className={styles.row} ref={scrollRef}>
          <div className={styles.edgeSpacer} aria-hidden="true" />
          {loading
            ? Array.from({ length: 8 }).map((_, i) => (
                <MediaCardSkeleton key={i} orientation={orientation} />
              ))
            : items.map((item) => (
                <MediaCard
                  key={item.Id}
                  item={item}
                  orientation={orientation}
                  onClick={onItemClick ? () => onItemClick(item) : undefined}
                />
              ))}
          <div className={styles.edgeSpacer} aria-hidden="true" />
        </div>
        <button className={`${styles.arrow} ${styles.arrowLeft}`} onClick={() => scroll('left')}>
          <ChevronLeft size={24} />
        </button>
        <button className={`${styles.arrow} ${styles.arrowRight}`} onClick={() => scroll('right')}>
          <ChevronRight size={24} />
        </button>
      </div>
    </section>
  );
}
