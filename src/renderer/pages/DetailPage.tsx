import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Play, Heart, Check } from 'lucide-react';
import { buildImageUrl } from '../utils/image-url';
import { formatRuntime, formatFileSize, formatBitrate, formatEpisodeNumber } from '../utils/format';
import { usePlay } from '../hooks/use-play';
import HeroBackdrop from '../components/ui/HeroBackdrop';
import RatingBadge from '../components/ui/RatingBadge';
import PersonCard from '../components/ui/PersonCard';
import MediaRow from '../components/ui/MediaRow';
import MediaSourcePicker from '../components/ui/MediaSourcePicker';
import LoadingSpinner from '../components/ui/LoadingSpinner';
import type { BaseItemDto, ItemsResult, MediaSource as MediaSourceType } from '../api/types';
import styles from './DetailPage.module.css';

export default function DetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { play } = usePlay();
  const [item, setItem] = useState<BaseItemDto | null>(null);
  const [similar, setSimilar] = useState<BaseItemDto[]>([]);
  const [loading, setLoading] = useState(true);

  // Series-specific
  const [seasons, setSeasons] = useState<BaseItemDto[]>([]);
  const [selectedSeasonId, setSelectedSeasonId] = useState<string | null>(null);
  const [episodes, setEpisodes] = useState<BaseItemDto[]>([]);
  const [episodesLoading, setEpisodesLoading] = useState(false);

  // Toggle states
  const [isFavorite, setIsFavorite] = useState(false);
  const [isPlayed, setIsPlayed] = useState(false);
  const [overviewExpanded, setOverviewExpanded] = useState(false);
  const [favBounce, setFavBounce] = useState(false);
  const [playedBounce, setPlayedBounce] = useState(false);

  // Media source picker
  const [pickerSources, setPickerSources] = useState<MediaSourceType[] | null>(null);
  const [pickerItem, setPickerItem] = useState<BaseItemDto | null>(null);

  useEffect(() => {
    if (!id) return;
    setLoading(true);
    setItem(null);
    setSeasons([]);
    setEpisodes([]);
    setSimilar([]);
    setSelectedSeasonId(null);

    (async () => {
      const res = await window.api.library.getItem(id);
      if (!res.success) { setLoading(false); return; }
      const data = res.data as BaseItemDto;
      setItem(data);
      setIsFavorite(data.UserData?.IsFavorite ?? false);
      setIsPlayed(data.UserData?.Played ?? false);
      setLoading(false);

      window.api.library.getSimilar(id).then((r) => {
        if (r.success) setSimilar((r.data as ItemsResult).Items);
      });

      if (data.Type === 'Series') {
        const sRes = await window.api.library.getSeasons(id);
        if (sRes.success) {
          const sList = (sRes.data as ItemsResult).Items;
          setSeasons(sList);
          const unwatched = sList.find((s) => !s.UserData?.Played);
          setSelectedSeasonId(unwatched?.Id ?? sList[0]?.Id ?? null);
        }
      }
    })();
  }, [id]);

  useEffect(() => {
    if (!selectedSeasonId || !item || item.Type !== 'Series') return;
    setEpisodesLoading(true);
    window.api.library.getEpisodes(item.Id, selectedSeasonId).then((r) => {
      if (r.success) setEpisodes((r.data as ItemsResult).Items);
      setEpisodesLoading(false);
    });
  }, [selectedSeasonId, item]);

  const toggleFavorite = useCallback(async () => {
    if (!item) return;
    const next = !isFavorite;
    setIsFavorite(next);
    setFavBounce(true);
    setTimeout(() => setFavBounce(false), 250);
    await window.api.user.updateFavorite(item.Id, next);
  }, [item, isFavorite]);

  const togglePlayed = useCallback(async () => {
    if (!item) return;
    const next = !isPlayed;
    setIsPlayed(next);
    setPlayedBounce(true);
    setTimeout(() => setPlayedBounce(false), 250);
    if (next) await window.api.user.markPlayed(item.Id);
    else await window.api.user.markUnplayed(item.Id);
  }, [item, isPlayed]);

  const handlePlay = useCallback(async (target: BaseItemDto) => {
    // Check for multiple media sources
    const pbRes = await window.api.media.getPlaybackInfo(target.Id);
    if (!pbRes.success || !pbRes.data) return;
    const sources = pbRes.data.MediaSources;
    if (sources.length > 1) {
      setPickerSources(sources);
      setPickerItem(target);
    } else {
      play(target, sources[0]);
    }
  }, [play]);

  const handleSourcePick = useCallback((source: MediaSourceType) => {
    if (pickerItem) play(pickerItem, source);
    setPickerSources(null);
    setPickerItem(null);
  }, [pickerItem, play]);

  if (loading) return <LoadingSpinner size={48} />;
  if (!item) return <div className={styles.empty}>Item not found</div>;

  const isEpisode = item.Type === 'Episode';
  const isSeries = item.Type === 'Series';
  const runtime = formatRuntime(item.RunTimeTicks);
  const source = item.MediaSources?.[0];

  const backdropId = item.BackdropImageTags?.[0]
    ? item.Id
    : item.ParentBackdropItemId ?? item.SeriesId ?? item.Id;
  const backdropTag = item.BackdropImageTags?.[0]
    ?? item.ParentBackdropImageTags?.[0]
    ?? undefined;

  return (
    <div className={`${styles.page} fade-in`}>
      <button className={styles.backBtn} onClick={() => navigate(-1)}>
        <ArrowLeft size={20} />
      </button>

      <HeroBackdrop itemId={backdropId} tag={backdropTag} height="55vh">
        <div className={styles.heroInner}>
          <div className={styles.poster}>
            {isEpisode ? (
              <img
                src={buildImageUrl(item.Id, item.ImageTags?.['Primary'] ? 'Primary' : 'Thumb', { maxWidth: 400, tag: item.ImageTags?.['Primary'] ?? item.ImageTags?.['Thumb'] })}
                alt={item.Name}
                className={styles.posterImg}
                style={{ aspectRatio: '16/9', borderRadius: 8 }}
              />
            ) : (
              <img
                src={buildImageUrl(item.Id, 'Primary', { maxWidth: 400, tag: item.ImageTags?.['Primary'] })}
                alt={item.Name}
                className={styles.posterImg}
              />
            )}
          </div>

          <div className={styles.heroInfo}>
            {isEpisode && item.SeriesName && (
              <button className={styles.seriesLink} onClick={() => navigate(`/detail/${item.SeriesId}`)}>
                {item.SeriesName}
              </button>
            )}

            <h1 className={styles.title}>
              {isEpisode
                ? `${formatEpisodeNumber(item.ParentIndexNumber, item.IndexNumber)} \u2014 ${item.Name}`
                : item.Name}
            </h1>

            <div className={styles.metaRow}>
              {item.ProductionYear && <span>{item.ProductionYear}</span>}
              {runtime && <span>{runtime}</span>}
              <RatingBadge
                communityRating={item.CommunityRating}
                officialRating={item.OfficialRating}
                criticRating={item.CriticRating}
              />
              {isSeries && item.ChildCount != null && (
                <span>{item.ChildCount} Season{item.ChildCount !== 1 ? 's' : ''}</span>
              )}
            </div>

            {item.Genres && item.Genres.length > 0 && (
              <div className={styles.genres}>
                {item.Genres.map((g) => (
                  <span key={g} className={styles.genrePill}>{g}</span>
                ))}
              </div>
            )}

            <div className={styles.actions}>
              {!isSeries && (
                <button className={styles.playBtn} onClick={() => handlePlay(item)}>
                  <Play size={16} fill="currentColor" /> Play
                </button>
              )}
              <button
                className={`${styles.actionBtn} ${isFavorite ? styles.actionActive : ''} ${favBounce ? styles.bounce : ''}`}
                onClick={toggleFavorite}
              >
                <Heart size={16} fill={isFavorite ? 'currentColor' : 'none'} /> Favorite
              </button>
              <button
                className={`${styles.actionBtn} ${isPlayed ? styles.actionActive : ''} ${playedBounce ? styles.bounce : ''}`}
                onClick={togglePlayed}
              >
                <Check size={16} /> {isPlayed ? 'Played' : 'Mark Played'}
              </button>
            </div>
          </div>
        </div>
      </HeroBackdrop>

      <div className={styles.body}>
        {item.Overview && (
          <section className={styles.section}>
            <p className={`${styles.overview} ${overviewExpanded ? styles.overviewExpanded : ''}`}>
              {item.Overview}
            </p>
            {item.Overview.length > 300 && (
              <button className={styles.readMore} onClick={() => setOverviewExpanded(!overviewExpanded)}>
                {overviewExpanded ? 'Show less' : 'Read more'}
              </button>
            )}
          </section>
        )}

        {isEpisode && (
          <div className={styles.episodeNav}>
            {item.IndexNumber != null && item.IndexNumber > 1 && (
              <button className={styles.episodeNavBtn} onClick={() => {/* TODO: prev episode */}}>
                <ArrowLeft size={14} /> Previous Episode
              </button>
            )}
            <div className={styles.episodeNavSpacer} />
            <button className={styles.episodeNavBtn} onClick={() => {/* TODO: next episode */}}>
              Next Episode
            </button>
          </div>
        )}

        {isSeries && seasons.length > 0 && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>Seasons</h2>
            <div className={styles.seasonTabs}>
              {seasons.map((s) => (
                <button
                  key={s.Id}
                  className={`${styles.seasonTab} ${s.Id === selectedSeasonId ? styles.seasonTabActive : ''}`}
                  onClick={() => setSelectedSeasonId(s.Id)}
                >
                  {s.Name}
                </button>
              ))}
            </div>
            <div className={styles.episodeList}>
              {episodesLoading ? (
                <LoadingSpinner size={32} />
              ) : (
                episodes.map((ep) => (
                  <EpisodeRow key={ep.Id} episode={ep} navigate={navigate} onPlay={() => handlePlay(ep)} />
                ))
              )}
            </div>
          </section>
        )}

        {item.People && item.People.length > 0 && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>Cast &amp; Crew</h2>
            <div className={styles.castScroll}>
              {item.People.map((p) => (
                <PersonCard key={p.Id + (p.Role ?? '')} id={p.Id} name={p.Name} role={p.Role ?? p.Type} imageTag={p.PrimaryImageTag} />
              ))}
            </div>
          </section>
        )}

        {source && <MediaInfo source={source} />}

        {similar.length > 0 && (
          <MediaRow title={`Similar ${isSeries ? 'Shows' : isEpisode ? 'Episodes' : 'Movies'}`} items={similar} orientation="portrait" />
        )}
      </div>

      {/* Media source picker */}
      {pickerSources && (
        <MediaSourcePicker
          sources={pickerSources}
          onSelect={handleSourcePick}
          onCancel={() => { setPickerSources(null); setPickerItem(null); }}
        />
      )}
    </div>
  );
}

// ── Sub-components ──────────────────────────────────────

function EpisodeRow({ episode, navigate, onPlay }: { episode: BaseItemDto; navigate: (path: string) => void; onPlay: () => void }) {
  const [imgLoaded, setImgLoaded] = useState(false);
  const pct = episode.UserData?.PlayedPercentage ?? 0;
  const played = episode.UserData?.Played ?? false;

  const thumbSrc = episode.ImageTags?.['Primary']
    ? buildImageUrl(episode.Id, 'Primary', { maxWidth: 400, tag: episode.ImageTags['Primary'] })
    : episode.ImageTags?.['Thumb']
      ? buildImageUrl(episode.Id, 'Thumb', { maxWidth: 400, tag: episode.ImageTags['Thumb'] })
      : '';

  return (
    <div className={styles.epRow} onClick={() => navigate(`/detail/${episode.Id}`)}>
      <div className={styles.epThumb}>
        {thumbSrc ? (
          <img
            src={thumbSrc}
            alt=""
            className={`${styles.epThumbImg} ${imgLoaded ? styles.epThumbLoaded : ''}`}
            loading="lazy"
            onLoad={() => setImgLoaded(true)}
          />
        ) : (
          <div className={styles.epThumbFallback}>{episode.IndexNumber ?? '?'}</div>
        )}
        {pct > 0 && pct < 100 && (
          <div className={styles.epProgress}>
            <div className={styles.epProgressFill} style={{ width: `${pct}%` }} />
          </div>
        )}
        {played && <div className={styles.epPlayed}><Check size={12} strokeWidth={3} /></div>}
      </div>
      <div className={styles.epInfo}>
        <div className={styles.epTitle}>
          {formatEpisodeNumber(episode.ParentIndexNumber, episode.IndexNumber)} &mdash; {episode.Name}
        </div>
        <div className={styles.epMeta}>
          {formatRuntime(episode.RunTimeTicks)}
          {episode.CommunityRating ? ` \u00b7 \u2605 ${episode.CommunityRating.toFixed(1)}` : ''}
        </div>
        {episode.Overview && (
          <p className={styles.epOverview}>{episode.Overview}</p>
        )}
      </div>
      <button className={styles.epPlayBtn} onClick={(e) => { e.stopPropagation(); onPlay(); }}>
        <Play size={16} fill="currentColor" />
      </button>
    </div>
  );
}

function MediaInfo({ source }: { source: MediaSourceType }) {
  const video = source.MediaStreams?.find((s) => s.Type === 'Video');
  const audio = source.MediaStreams?.find((s) => s.Type === 'Audio');

  return (
    <section className={styles.section}>
      <h2 className={styles.sectionTitle}>Media Info</h2>
      <div className={styles.mediaInfo}>
        {video && (
          <div className={styles.mediaLine}>
            <span className={styles.mediaLabel}>Video</span>
            <span>{video.DisplayTitle ?? video.Codec}</span>
          </div>
        )}
        {audio && (
          <div className={styles.mediaLine}>
            <span className={styles.mediaLabel}>Audio</span>
            <span>{audio.DisplayTitle ?? `${audio.Codec}${audio.Language ? ` (${audio.Language})` : ''}`}</span>
          </div>
        )}
        <div className={styles.mediaLine}>
          <span className={styles.mediaLabel}>File</span>
          <span>{source.Container?.toUpperCase()} &middot; {formatFileSize(source.Size)} &middot; {formatBitrate(source.Bitrate)}</span>
        </div>
      </div>
    </section>
  );
}
