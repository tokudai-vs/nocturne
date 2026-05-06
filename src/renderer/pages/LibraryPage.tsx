import { useEffect, useState, useRef, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { FolderOpen, Check, Star } from 'lucide-react';
import { useLibraryStore } from '../stores/library-store';
import { useSyncStore } from '../stores/sync-store';
import { buildImageUrl } from '../utils/image-url';
import { cachedToBaseItems } from '../utils/cache-adapter';
import MediaCard from '../components/ui/MediaCard';
import MediaCardSkeleton from '../components/ui/MediaCardSkeleton';
import TraktExternalItemModal from '../components/ui/TraktExternalItemModal';
import type { BaseItemDto, CachedItem, ItemsResult } from '../api/types';
import styles from './LibraryPage.module.css';

type ExternalItem = BaseItemDto & {
  isExternal?: boolean;
  traktKey?: string;
  traktType?: 'movie' | 'show';
};

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
  const { virtualLibraries, vlibsLoaded, views } = useLibraryStore();
  const { completed: syncCompleted } = useSyncStore();

  const [externalItem, setExternalItem] = useState<ExternalItem | null>(null);
  const [items, setItems] = useState<BaseItemDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [sortBy, setSortBy] = useState('DateCreated');
  const [sortOrder, setSortOrder] = useState<'Descending' | 'Ascending'>('Descending');
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [usingCache, setUsingCache] = useState(false);

  const sentinelRef = useRef<HTMLDivElement>(null);
  const hasMore = items.length < totalCount;

  // Resolve library name
  const vlib = virtualLibraries.find((v) => v.id === id);
  const viewLib = views.find((v) => v.Id === id);
  const libraryName = vlib?.name ?? viewLib?.Name ?? 'Library';

  // Orphan vlib URL guard: if vlibs have loaded and the id resolves to neither
  // a virtual library nor a raw Emby view, the group or library was deleted —
  // bounce to home instead of rendering an empty page.
  useEffect(() => {
    if (!vlibsLoaded) return;
    if (!id) return;
    if (vlib || viewLib) return;
    navigate('/', { replace: true });
  }, [vlibsLoaded, id, vlib, viewLib, navigate]);

  const fetchPage = useCallback(
    async (startIndex: number, replace: boolean) => {
      if (!id) return;
      if (replace) setLoading(true);
      else setLoadingMore(true);

      // Try vlib (SQLite) first
      const vlibRes = await window.api.vlib.getItems(id, {
        startIndex,
        limit: PAGE_SIZE,
        sortBy,
        sortOrder: sortOrder === 'Descending' ? 'desc' : 'asc',
      });

      if (vlibRes.success) {
        const data = vlibRes.data as { items: CachedItem[]; total: number };
        if (data.total > 0 || startIndex > 0) {
          const converted = cachedToBaseItems(data.items);
          setItems((prev) => (replace ? converted : [...prev, ...converted]));
          setTotalCount(data.total);
          setUsingCache(true);
          if (replace) setLoading(false);
          else setLoadingMore(false);
          return;
        }
      }

      // Fallback: Emby API (raw library ID)
      setUsingCache(false);
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

  // Refresh when sync completes
  useEffect(() => {
    if (syncCompleted) {
      setItems([]);
      setTotalCount(0);
      fetchPage(0, true);
    }
  }, [syncCompleted]);

  // Bug 2 fix: when viewing the Trakt watchlist sentinel, refetch on
  // watchlist-updated events (fired after add-to-watchlist / remove /
  // background refresh). Skip when on any other library to avoid spurious
  // refetches.
  useEffect(() => {
    if (id !== 'trakt:watchlist') return;
    const off = window.api.trakt.onWatchlistUpdated(() => {
      setItems([]);
      setTotalCount(0);
      fetchPage(0, true);
    });
    return off;
  }, [id, fetchPage]);

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
            <MediaCard
              key={item.Id}
              item={item as ExternalItem}
              onExternalClick={(ext) => setExternalItem(ext)}
            />
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

      {externalItem && externalItem.traktType && (
        <TraktExternalItemModal
          title={externalItem.Name}
          year={externalItem.ProductionYear ?? null}
          overview={externalItem.Overview ?? null}
          tmdbId={null}
          imdbId={null}
          traktType={externalItem.traktType}
          traktKey={externalItem.traktKey}
          onClose={() => setExternalItem(null)}
          onRemoved={() => {
            // Item was removed from Trakt — drop it from the visible list.
            setItems((prev) => prev.filter((p) => p.Id !== externalItem.Id));
            setTotalCount((c) => Math.max(0, c - 1));
          }}
        />
      )}
    </div>
  );
}
