import { useEffect, useState, useCallback, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { Play, RefreshCw } from 'lucide-react';
import { useLibraryStore } from '../stores/library-store';
import { useSettingsStore } from '../stores/settings-store';
import { useSyncStore } from '../stores/sync-store';
import { usePlay } from '../hooks/use-play';
import { useAppVisibility } from '../hooks/use-app-visibility';
import { buildImageUrl } from '../utils/image-url';
import { cachedToBaseItem, cachedToBaseItems } from '../utils/cache-adapter';
import { formatRuntime } from '../utils/format';
import MediaRow from '../components/ui/MediaRow';
import RatingBadge from '../components/ui/RatingBadge';
import Skeleton from '../components/ui/Skeleton';
import type { BaseItemDto, CachedItem, VirtualLibrary } from '../api/types';
import styles from './HomePage.module.css';

export default function HomePage() {
  const navigate = useNavigate();
  const {
    virtualLibraries, vlibsLoaded, fetchVirtualLibraries,
    views, fetchViews,
    resumeItems, nextUpItems, fetchResume, fetchNextUp,
    resumeLoading, nextUpLoading,
  } = useLibraryStore();
  const { completed: syncCompleted, running: syncRunning } = useSyncStore();
  const settings = useSettingsStore((s) => s.settings);
  const isCombined = settings?.libraryMode === 'combined';
  const { play } = usePlay();
  const { visible: appVisible } = useAppVisibility();
  const powerMode = settings?.powerMode || 'balanced';

  const [latestByVlib, setLatestByVlib] = useState<Record<string, BaseItemDto[]>>({});
  const [latestLoading, setLatestLoading] = useState(true);
  const [heroItems, setHeroItems] = useState<BaseItemDto[]>([]);
  const [heroIndex, setHeroIndex] = useState(0);
  const [heroImgLoaded, setHeroImgLoaded] = useState(false);
  const [firstSyncBanner, setFirstSyncBanner] = useState(false);
  const heroTimer = useRef<ReturnType<typeof setInterval>>(undefined);
  const readyFired = useRef(false);
  const heroLoaded = useRef(false);

  // Initial data load — wait for settings so mode-aware fetches take the right path
  const settingsLoaded = settings !== null;
  useEffect(() => {
    if (!settingsLoaded) return;
    fetchVirtualLibraries();
    fetchViews();
    fetchResume();
    loadHeroItems();
    if (!isCombined) {
      fetchNextUp();
    }
  }, [settingsLoaded]);

  // Refresh when sync completes — debounced so back-to-back complete events
  // (rare but possible during a partial→full transition or two consecutive
  // incrementals) coalesce into a single refetch fan-out instead of
  // re-firing vlib:get-all + cache:get-resume + vlib:get-heroes per event.
  const syncRefetchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => {
    if (!syncCompleted) return;
    if (syncRefetchTimer.current) clearTimeout(syncRefetchTimer.current);
    syncRefetchTimer.current = setTimeout(() => {
      fetchVirtualLibraries();
      fetchResume();
      loadHeroItems();
      if (!isCombined) fetchNextUp();
    }, 200);
    return () => {
      if (syncRefetchTimer.current) clearTimeout(syncRefetchTimer.current);
    };
  }, [syncCompleted]);

  // Load latest items when vlibs are available
  useEffect(() => {
    if (!vlibsLoaded) return;
    if (isCombined) {
      // Combined mode: trust cache ONLY when sync reports 'complete'. A partial
      // cache (first server done, second in progress) would otherwise render as
      // "only active server's content". While partial/in-progress, fan out to
      // every server so all libraries populate.
      (async () => {
        const statusRes = await window.api.sync.getStatus();
        const complete = statusRes.success && statusRes.data?.syncStatus === 'complete';
        if (complete && virtualLibraries.length > 0) {
          loadLatestFromVlibs(virtualLibraries);
          setFirstSyncBanner(false);
        } else {
          loadLatestFromAllServers(virtualLibraries);
          if (syncRunning) setFirstSyncBanner(true);
        }
      })();
    } else if (virtualLibraries.length > 0) {
      loadLatestFromVlibs(virtualLibraries);
      setFirstSyncBanner(false);
    } else if (views.length > 0) {
      // Separate mode, cache empty — fall back to active server's API
      loadLatestFromViews(views);
      if (syncRunning) setFirstSyncBanner(true);
    }
  }, [vlibsLoaded, virtualLibraries, views, isCombined]);

  async function loadHeroItems() {
    if (heroLoaded.current) return;
    // Try cache first
    const res = await window.api.vlib.getHeroes(undefined, 6);
    if (res.success) {
      const cached = res.data as CachedItem[];
      if (cached.length > 0) {
        setHeroItems(cachedToBaseItems(cached));
        heroLoaded.current = true;
        fireReady();
        return;
      }
    }
    // Will be populated after sync or by latest items fallback
  }

  async function loadLatestFromVlibs(vlibs: VirtualLibrary[]) {
    setLatestLoading(true);
    const results = await Promise.all(
      vlibs.map(async (vlib) => {
        const res = await window.api.vlib.getLatest(vlib.id, 20);
        const items = res.success ? cachedToBaseItems(res.data as CachedItem[]) : [];
        return { id: vlib.id, name: vlib.name, items };
      }),
    );

    const map: Record<string, BaseItemDto[]> = {};
    const heroCandidates: BaseItemDto[] = [];
    for (const r of results) {
      map[r.id] = r.items;
      for (const it of r.items) {
        if (it.BackdropImageTags && it.BackdropImageTags.length > 0) {
          heroCandidates.push(it);
        }
      }
    }
    setLatestByVlib(map);
    setLatestLoading(false);

    // If hero items haven't loaded from cache, use latest candidates
    if (!heroLoaded.current && heroCandidates.length > 0) {
      const shuffled = heroCandidates.sort(() => Math.random() - 0.5).slice(0, 6);
      setHeroItems(shuffled);
      heroLoaded.current = true;
    }

    fireReady();
  }

  async function loadLatestFromViews(viewsList: BaseItemDto[]) {
    setLatestLoading(true);
    const results = await Promise.all(
      viewsList.map(async (v) => {
        const res = await window.api.library.getLatest(v.Id, 20);
        return { id: v.Id, name: v.Name, items: res.success ? (res.data as BaseItemDto[]) : [] };
      }),
    );

    const map: Record<string, BaseItemDto[]> = {};
    const heroCandidates: BaseItemDto[] = [];
    for (const r of results) {
      map[r.id] = r.items;
      for (const it of r.items) {
        if (it.BackdropImageTags && it.BackdropImageTags.length > 0) {
          heroCandidates.push(it);
        }
      }
    }
    setLatestByVlib(map);
    setLatestLoading(false);

    if (!heroLoaded.current && heroCandidates.length > 0) {
      const shuffled = heroCandidates.sort(() => Math.random() - 0.5).slice(0, 6);
      setHeroItems(shuffled);
      heroLoaded.current = true;
    }

    fireReady();
  }

  async function loadLatestFromAllServers(vlibs: VirtualLibrary[]) {
    setLatestLoading(true);
    const res = await window.api.library.getAllServersLatest(20);
    if (!res.success || !res.data) {
      setLatestLoading(false);
      return;
    }

    // Index multi-server response by raw libraryId
    const byLibrary: Record<string, BaseItemDto[]> = {};
    for (const lib of res.data.libraries) {
      byLibrary[lib.libraryId] = lib.items;
    }

    // Project onto vlibs — each vlib aggregates its constituent library IDs.
    // Works for Option B per-server fallback (single libraryId) and for real
    // group mappings (multiple libraryIds).
    const map: Record<string, BaseItemDto[]> = {};
    const heroCandidates: BaseItemDto[] = [];
    for (const vlib of vlibs) {
      const items: BaseItemDto[] = [];
      for (const lid of vlib.libraryIds) {
        if (byLibrary[lid]) items.push(...byLibrary[lid]);
      }
      map[vlib.id] = items;
      for (const it of items) {
        if (it.BackdropImageTags && it.BackdropImageTags.length > 0) {
          heroCandidates.push(it);
        }
      }
    }
    setLatestByVlib(map);
    setLatestLoading(false);

    if (!heroLoaded.current && heroCandidates.length > 0) {
      const shuffled = heroCandidates.sort(() => Math.random() - 0.5).slice(0, 6);
      setHeroItems(shuffled);
      heroLoaded.current = true;
    }

    fireReady();
  }

  function fireReady() {
    if (!readyFired.current) {
      readyFired.current = true;
      window.dispatchEvent(new Event('nocturne:ready'));
    }
  }

  // Auto-rotate hero (pause when hidden in balanced/efficiency mode)
  useEffect(() => {
    if (heroItems.length <= 1) return;
    if (powerMode !== 'performance' && !appVisible) return;
    heroTimer.current = setInterval(() => {
      setHeroIndex((i) => (i + 1) % heroItems.length);
      setHeroImgLoaded(false);
    }, 10000);
    return () => clearInterval(heroTimer.current);
  }, [heroItems, appVisible, powerMode]);

  const hero = heroItems[heroIndex];

  // Determine display libraries: virtual if available, else raw views
  const useVlibs = vlibsLoaded && virtualLibraries.length > 0;
  const displayLibs = useVlibs
    ? virtualLibraries.map((v) => ({ id: v.id, name: v.name }))
    : views.map((v) => ({ id: v.Id, name: v.Name }));

  const handlePlayHero = () => {
    if (hero) play(hero);
  };

  const handleContinueWatchingClick = (item: BaseItemDto) => {
    play(item);
  };

  return (
    <div className={`${styles.page} fade-in`}>
      {/* First sync banner */}
      {firstSyncBanner && (
        <div className={styles.syncBanner}>
          <RefreshCw size={14} className={styles.syncBannerIcon} />
          Building your library index... Browse normally while this completes.
        </div>
      )}

      {/* Hero Banner */}
      {hero ? (
        <div className={styles.hero}>
          <div className={styles.heroBackdrop}>
            <img
              src={buildImageUrl(hero.Id, 'Backdrop', { maxWidth: 1920, tag: hero.BackdropImageTags?.[0] })}
              alt=""
              className={`${styles.heroImg} ${heroImgLoaded ? styles.heroImgLoaded : ''}`}
              key={hero.Id}
              onLoad={() => setHeroImgLoaded(true)}
            />
          </div>
          <div className={styles.heroGradient} />
          <div className={styles.heroContent}>
            <h1 className={styles.heroTitle}>{hero.Name}</h1>
            <div className={styles.heroMeta}>
              {hero.ProductionYear && <span>{hero.ProductionYear}</span>}
              {hero.RunTimeTicks && <span>{formatRuntime(hero.RunTimeTicks)}</span>}
              <RatingBadge
                communityRating={hero.CommunityRating}
                officialRating={hero.OfficialRating}
              />
              {hero.Genres?.slice(0, 3).map((g) => (
                <span key={g} className={styles.genreTag}>{g}</span>
              ))}
            </div>
            {hero.Overview && <p className={styles.heroOverview}>{hero.Overview}</p>}
            <div className={styles.heroActions}>
              <button className={styles.playBtn} onClick={handlePlayHero}>
                <Play size={16} fill="currentColor" /> Play
              </button>
              <button className={styles.infoBtn} onClick={() => navigate(`/detail/${hero.Id}`)}>
                More Info
              </button>
            </div>
          </div>
          {heroItems.length > 1 && (
            <div className={styles.heroDots}>
              {heroItems.map((_, i) => (
                <button
                  key={i}
                  className={`${styles.dot} ${i === heroIndex ? styles.dotActive : ''}`}
                  onClick={() => { setHeroIndex(i); setHeroImgLoaded(false); }}
                />
              ))}
            </div>
          )}
        </div>
      ) : (
        <div className={styles.heroSkeleton}>
          <Skeleton width="100%" height="100%" borderRadius={0} />
        </div>
      )}

      {/* Content Rows */}
      <div className={styles.rows}>
        <MediaRow
          title="Continue Watching"
          items={resumeItems}
          orientation="landscape"
          loading={resumeLoading}
          onItemClick={handleContinueWatchingClick}
        />
        {!isCombined && (
          <MediaRow title="Next Up" items={nextUpItems} orientation="landscape" loading={nextUpLoading} />
        )}
        {displayLibs.map((lib) =>
          (latestByVlib[lib.id]?.length ?? 0) > 0 ? (
            <MediaRow
              key={lib.id}
              title={`Latest ${lib.name}`}
              items={latestByVlib[lib.id] ?? []}
              orientation="portrait"
              loading={latestLoading}
              onSeeAll={() => navigate(`/library/${lib.id}`)}
            />
          ) : null,
        )}
      </div>
    </div>
  );
}
