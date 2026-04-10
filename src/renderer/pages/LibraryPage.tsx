import { useEffect, useState, useRef, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { FolderOpen, Check, Star } from 'lucide-react';
import { useLibraryStore } from '../stores/library-store';
import { buildImageUrl } from '../utils/image-url';
import MediaCard from '../components/ui/MediaCard';
import MediaCardSkeleton from '../components/ui/MediaCardSkeleton';
import type { BaseItemDto, ItemsResult } from '../api/types';
import styles from './LibraryPage.module.css';

const SORT_OPTIONS = [
  { value: 'DateCreated', label: 'Date Added' },
  { value: 'SortName', label: 'Name' },
  { value: 'CommunityRating', label: 'Rating' },
  { value: 'ProductionYear', label: 'Year' },
  { value: 'RunTimeTicks', label: 'Runtime' },
];

const PAGE_SIZE = 40;

export default function LibraryPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { views } = useLibraryStore();

  const [items, setItems] = useState<BaseItemDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [sortBy, setSortBy] = useState('DateCreated');
  const [sortOrder, setSortOrder] = useState<'Descending' | 'Ascending'>('Descending');
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');

  const sentinelRef = useRef<HTMLDivElement>(null);
  const hasMore = items.length < totalCount;

  const libraryName = views.find((v) => v.Id === id)?.Name ?? 'Library';

  const fetchPage = useCallback(
    async (startIndex: number, replace: boolean) => {
      if (!id) return;
      if (replace) setLoading(true);
      else setLoadingMore(true);

      const res = await window.api.library.getItems(id, {
        StartIndex: startIndex,
        Limit: PAGE_SIZE,
        SortBy: sortBy,
        SortOrder: sortOrder,
        Fields: 'Overview,People,Genres,Studios,MediaSources,UserData,ImageTags,BackdropImageTags',
        Recursive: true,
        IncludeItemTypes: 'Movie,Series,Episode,MusicAlbum,Audio',
      });

      if (res.success) {
        const data = res.data as ItemsResult;
        setItems((prev) => (replace ? data.Items : [...prev, ...data.Items]));
        setTotalCount(data.TotalRecordCount);
      }

      if (replace) setLoading(false);
      else setLoadingMore(false);
    },
    [id, sortBy, sortOrder],
  );

  // Initial fetch & reset on sort/library change
  useEffect(() => {
    setItems([]);
    setTotalCount(0);
    fetchPage(0, true);
  }, [fetchPage]);

  // Infinite scroll via IntersectionObserver
  useEffect(() => {
    const sentinel = sentinelRef.current;
    if (!sentinel) return;

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting && hasMore && !loading && !loadingMore) {
          fetchPage(items.length, false);
        }
      },
      { rootMargin: '200px' },
    );

    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [hasMore, loading, loadingMore, items.length, fetchPage]);

  const toggleSort = (value: string) => {
    if (sortBy === value) {
      setSortOrder((o) => (o === 'Descending' ? 'Ascending' : 'Descending'));
    } else {
      setSortBy(value);
      setSortOrder('Descending');
    }
  };

  return (
    <div className={`${styles.page} fade-in`}>
      <header className={styles.header}>
        <h1 className={styles.title}>{libraryName}</h1>
        {totalCount > 0 && (
          <span className={styles.count}>{totalCount} items</span>
        )}
      </header>

      <div className={styles.toolbar}>
        <div className={styles.sortGroup}>
          {SORT_OPTIONS.map((opt) => (
            <button
              key={opt.value}
              className={`${styles.sortBtn} ${sortBy === opt.value ? styles.sortActive : ''}`}
              onClick={() => toggleSort(opt.value)}
            >
              {opt.label}
              {sortBy === opt.value && (
                <span className={styles.sortArrow}>{sortOrder === 'Descending' ? ' ▼' : ' ▲'}</span>
              )}
            </button>
          ))}
        </div>
        <div className={styles.viewToggle}>
          <button
            className={`${styles.viewBtn} ${viewMode === 'grid' ? styles.viewActive : ''}`}
            onClick={() => setViewMode('grid')}
            title="Grid view"
          >
            ▦
          </button>
          <button
            className={`${styles.viewBtn} ${viewMode === 'list' ? styles.viewActive : ''}`}
            onClick={() => setViewMode('list')}
            title="List view"
          >
            ☰
          </button>
        </div>
      </div>

      {loading ? (
        <div className={styles.grid}>
          {Array.from({ length: 20 }, (_, i) => (
            <MediaCardSkeleton key={i} />
          ))}
        </div>
      ) : items.length === 0 ? (
        <div className={styles.empty}>
          <div className={styles.emptyIcon}><FolderOpen size={48} /></div>
          <p>This library is empty</p>
        </div>
      ) : viewMode === 'grid' ? (
        <div className={styles.grid}>
          {items.map((item) => (
            <MediaCard key={item.Id} item={item} />
          ))}
        </div>
      ) : (
        <div className={styles.list}>
          {items.map((item, i) => (
            <div
              key={item.Id}
              className={`${styles.listRow} ${i % 2 === 0 ? styles.listRowEven : ''}`}
              onClick={() => navigate(`/detail/${item.Id}`)}
            >
              <div className={styles.listThumb}>
                {item.ImageTags?.['Primary'] ? (
                  <img
                    src={buildImageUrl(item.Id, 'Primary', { maxWidth: 120, tag: item.ImageTags['Primary'] })}
                    alt=""
                    className={styles.listThumbImg}
                    loading="lazy"
                  />
                ) : (
                  <div className={styles.listThumbFallback}>{item.Name.charAt(0)}</div>
                )}
              </div>
              <div className={styles.listInfo}>
                <div className={styles.listName}>{item.Name}</div>
                <div className={styles.listMeta}>
                  {item.ProductionYear && <span>{item.ProductionYear}</span>}
                  {item.CommunityRating && <span><Star size={12} style={{display:'inline',verticalAlign:'middle',marginRight:2}} /> {item.CommunityRating.toFixed(1)}</span>}
                  {item.Genres?.slice(0, 3).join(', ')}
                </div>
              </div>
              {item.UserData?.Played && <span className={styles.listPlayed}><Check size={16} /></span>}
            </div>
          ))}
        </div>
      )}

      {loadingMore && (
        <div className={styles.loadingMore}>
          {Array.from({ length: 8 }, (_, i) => (
            <MediaCardSkeleton key={i} />
          ))}
        </div>
      )}

      <div ref={sentinelRef} className={styles.sentinel} />
    </div>
  );
}
