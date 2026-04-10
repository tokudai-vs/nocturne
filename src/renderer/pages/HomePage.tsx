import { useEffect, useState, useCallback, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { Play } from 'lucide-react';
import { useLibraryStore } from '../stores/library-store';
import { usePlay } from '../hooks/use-play';
import { buildImageUrl } from '../utils/image-url';
import { formatRuntime } from '../utils/format';
import MediaRow from '../components/ui/MediaRow';
import RatingBadge from '../components/ui/RatingBadge';
import Skeleton from '../components/ui/Skeleton';
import type { BaseItemDto } from '../api/types';
import styles from './HomePage.module.css';

export default function HomePage() {
  const navigate = useNavigate();
  const { views, resumeItems, nextUpItems, fetchViews, fetchResume, fetchNextUp, resumeLoading, nextUpLoading } =
    useLibraryStore();
  const { play } = usePlay();

  const [latestByLib, setLatestByLib] = useState<Record<string, BaseItemDto[]>>({});
  const [latestLoading, setLatestLoading] = useState(true);
  const [heroItems, setHeroItems] = useState<BaseItemDto[]>([]);
  const [heroIndex, setHeroIndex] = useState(0);
  const [heroImgLoaded, setHeroImgLoaded] = useState(false);
  const heroTimer = useRef<ReturnType<typeof setInterval>>(undefined);
  const readyFired = useRef(false);

  useEffect(() => {
    fetchViews();
    fetchResume();
    fetchNextUp();
  }, [fetchViews, fetchResume, fetchNextUp]);

  useEffect(() => {
    if (views.length === 0) return;
    setLatestLoading(true);
    Promise.all(
      views.map(async (v) => {
        const res = await window.api.library.getLatest(v.Id, 20);
        return { id: v.Id, items: res.success ? (res.data as BaseItemDto[]) : [] };
      }),
    ).then((results) => {
      const map: Record<string, BaseItemDto[]> = {};
      const candidates: BaseItemDto[] = [];
      for (const r of results) {
        map[r.id] = r.items;
        for (const it of r.items) {
          if (it.BackdropImageTags && it.BackdropImageTags.length > 0) {
            candidates.push(it);
          }
        }
      }
      setLatestByLib(map);
      setLatestLoading(false);
      const shuffled = candidates.sort(() => Math.random() - 0.5);
      setHeroItems(shuffled.slice(0, 6));

      // Signal splash screen that data is ready
      if (!readyFired.current) {
        readyFired.current = true;
        window.dispatchEvent(new Event('nocturne:ready'));
      }
    });
  }, [views]);

  // Auto-rotate hero
  useEffect(() => {
    if (heroItems.length <= 1) return;
    heroTimer.current = setInterval(() => {
      setHeroIndex((i) => (i + 1) % heroItems.length);
      setHeroImgLoaded(false);
    }, 10000);
    return () => clearInterval(heroTimer.current);
  }, [heroItems]);

  const hero = heroItems[heroIndex];

  const findViewId = useCallback(
    (type: string) => views.find((v) => v.Name.toLowerCase().includes(type.toLowerCase()))?.Id,
    [views],
  );

  const moviesId = findViewId('movie');
  const tvId = findViewId('tv') ?? findViewId('series') ?? findViewId('show');

  const handlePlayHero = () => {
    if (hero) play(hero);
  };

  const handleContinueWatchingClick = (item: BaseItemDto) => {
    play(item);
  };

  return (
    <div className={`${styles.page} fade-in`}>
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
        <MediaRow title="Next Up" items={nextUpItems} orientation="landscape" loading={nextUpLoading} />
        {moviesId && (
          <MediaRow
            title="Latest Movies"
            items={latestByLib[moviesId] ?? []}
            orientation="portrait"
            loading={latestLoading}
            onSeeAll={() => navigate(`/library/${moviesId}`)}
          />
        )}
        {tvId && (
          <MediaRow
            title="Latest TV Shows"
            items={latestByLib[tvId] ?? []}
            orientation="portrait"
            loading={latestLoading}
            onSeeAll={() => navigate(`/library/${tvId}`)}
          />
        )}
        {views
          .filter((v) => v.Id !== moviesId && v.Id !== tvId)
          .map((v) =>
            (latestByLib[v.Id]?.length ?? 0) > 0 ? (
              <MediaRow
                key={v.Id}
                title={`Latest ${v.Name}`}
                items={latestByLib[v.Id] ?? []}
                orientation="portrait"
                loading={latestLoading}
                onSeeAll={() => navigate(`/library/${v.Id}`)}
              />
            ) : null,
          )}
      </div>
    </div>
  );
}
