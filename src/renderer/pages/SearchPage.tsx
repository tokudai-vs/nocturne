import { useEffect, useState, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Search } from 'lucide-react';
import { cachedToBaseItems } from '../utils/cache-adapter';
import type { BaseItemDto, CachedItem } from '../api/types';
import MediaCard from '../components/ui/MediaCard';
import styles from './SearchPage.module.css';

type SectionKey = 'Movie' | 'Series' | 'Episode';

const SECTION_LABELS: Record<SectionKey, string> = {
  Movie: 'Movies',
  Series: 'Series',
  Episode: 'Episodes',
};

const SECTION_ORDER: SectionKey[] = ['Movie', 'Series', 'Episode'];

export default function SearchPage() {
  const [params] = useSearchParams();
  const query = params.get('q') ?? '';
  const [results, setResults] = useState<BaseItemDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [searched, setSearched] = useState(false);
  const prevQuery = useRef('');

  useEffect(() => {
    if (!query.trim() || query === prevQuery.current) return;
    prevQuery.current = query;
    setLoading(true);
    setSearched(false);

    // Search SQLite first for instant results, then enrich with API
    (async () => {
      // 1. SQLite instant results (already deduped by group)
      let cachedItems: CachedItem[] = [];
      const cacheRes = await window.api.cache.search(query);
      if (cacheRes.success) {
        cachedItems = cacheRes.data as CachedItem[];
        if (cachedItems.length > 0) {
          setResults(cachedToBaseItems(cachedItems));
          setLoading(false);
          setSearched(true);
        }
      }

      // 2. Emby API for items not yet synced — filter out anything already
      //    represented by a cache primary (same Id OR same dedup_group_id).
      const apiRes = await window.api.search.query(query, { Limit: 100 });
      if (apiRes.success && apiRes.data) {
        const apiItems: BaseItemDto[] = apiRes.data.Items;
        const cachedIds = new Set(cachedItems.map((c) => c.emby_id));
        const cachedGroups = new Set(
          cachedItems.map((c) => c.dedup_group_id).filter((g): g is string => !!g),
        );

        // Resolve dedup_group_id for any api item we haven't already filtered out
        const probeIds = apiItems.map((i) => i.Id).filter((id) => !cachedIds.has(id));
        let apiGroupMap: Record<string, string> = {};
        if (probeIds.length > 0) {
          const groupsRes = await window.api.cache.resolveDedupGroups(probeIds);
          if (groupsRes.success && groupsRes.data) apiGroupMap = groupsRes.data;
        }

        const filteredApi = apiItems.filter((i) => {
          if (cachedIds.has(i.Id)) return false;
          const g = apiGroupMap[i.Id];
          if (g && cachedGroups.has(g)) return false;
          return true;
        });

        setResults((prev) => {
          const vcMap = new Map<string, number>();
          for (const p of prev) {
            const vc = (p as BaseItemDto & { versionCount?: number }).versionCount;
            if (vc && vc > 1) vcMap.set(p.Id, vc);
          }
          const apiSeen = new Set(filteredApi.map((i) => i.Id));
          const unique = prev.filter((p) => !apiSeen.has(p.Id));
          const enriched = filteredApi.map((i) => {
            const vc = vcMap.get(i.Id);
            return vc ? { ...i, versionCount: vc } : i;
          });
          return [...unique, ...enriched];
        });
      }

      setLoading(false);
      setSearched(true);
    })();
  }, [query]);

  const grouped = SECTION_ORDER.map((key) => ({
    key,
    label: SECTION_LABELS[key],
    items: results.filter((r) => r.Type === key),
  })).filter((g) => g.items.length > 0);

  return (
    <div className={`${styles.page} fade-in`}>
      <h1 className={styles.heading}>
        {query ? `Results for "${query}"` : 'Search'}
      </h1>
      {query && searched && !loading && (
        <p className={styles.subtext}>
          {results.length} result{results.length !== 1 ? 's' : ''} found
        </p>
      )}

      {loading && (
        <div className={styles.loading}>
          <div className={styles.spinner} />
          Searching...
        </div>
      )}

      {!loading && searched && results.length === 0 && (
        <div className={styles.empty}>
          <div className={styles.emptyIcon}><Search size={48} /></div>
          <div className={styles.emptyText}>No results found for &ldquo;{query}&rdquo;</div>
        </div>
      )}

      {!loading && grouped.map((section) => (
        <div key={section.key} className={styles.section}>
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>{section.label}</h2>
            <span className={styles.sectionCount}>({section.items.length})</span>
          </div>
          <div className={section.key === 'Episode' ? styles.landscapeGrid : styles.grid}>
            {section.items.map((item) => (
              <MediaCard
                key={item.Id}
                item={item}
                orientation={section.key === 'Episode' ? 'landscape' : 'portrait'}
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
