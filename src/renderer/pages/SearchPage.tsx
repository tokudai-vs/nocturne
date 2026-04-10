import { useEffect, useState, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Search } from 'lucide-react';
import type { BaseItemDto } from '../api/types';
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
    window.api.search.query(query, { Limit: 100 }).then((res) => {
      if (res.success && res.data) {
        setResults(res.data.Items);
      } else {
        setResults([]);
      }
      setLoading(false);
      setSearched(true);
    });
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
