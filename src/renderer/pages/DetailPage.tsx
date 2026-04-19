import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, ArrowRight, Play, Heart, Check } from 'lucide-react';
import { buildImageUrl } from '../utils/image-url';
import { formatRuntime, formatFileSize, formatBitrate, formatEpisodeNumber } from '../utils/format';
import { cachedToBaseItem } from '../utils/cache-adapter';
import { usePlay } from '../hooks/use-play';
import HeroBackdrop from '../components/ui/HeroBackdrop';
import RatingBadge from '../components/ui/RatingBadge';
import PersonCard from '../components/ui/PersonCard';
import MediaRow from '../components/ui/MediaRow';
import MediaSourcePicker from '../components/ui/MediaSourcePicker';
import LoadingSpinner from '../components/ui/LoadingSpinner';
import type { BaseItemDto, CachedItem, EpisodeVersionGroup, ItemsResult, MediaSource as MediaSourceType, ServerConfig } from '../api/types';
import styles from './DetailPage.module.css';
import pickerStyles from '../components/ui/MediaSourcePicker.module.css';

interface ParsedMediaSource {
  Id?: string;
  Container?: string;
  Size?: number;
  Bitrate?: number;
  VideoCodec?: string;
  AudioCodec?: string;
  Width?: number;
  Height?: number;
}

function getQualityLabel(cachedItem: CachedItem): string {
  try {
    const sources: ParsedMediaSource[] = JSON.parse(cachedItem.media_sources || '[]');
    if (sources.length > 0) {
      const s = sources[0];
      const w = s.Width || 0;
      if (w >= 3840) return '4K';
      if (w >= 1920) return '1080p';
      if (w >= 1280) return '720p';
      if (w > 0) return `${w}p`;
    }
  } catch { /* ignore */ }
  // Fallback: detect from library name
  const name = (cachedItem.library_name || '').toLowerCase();
  if (name.includes('4k') || name.includes('uhd')) return '4K';
  return 'HD';
}

function getVersionCodecInfo(cachedItem: CachedItem): string {
  try {
    const sources: ParsedMediaSource[] = JSON.parse(cachedItem.media_sources || '[]');
    if (sources.length > 0) {
      const s = sources[0];
      const parts: string[] = [];
      if (s.VideoCodec) parts.push(s.VideoCodec.toUpperCase());
      if (s.AudioCodec) parts.push(s.AudioCodec.toUpperCase());
      return parts.join(' / ');
    }
  } catch { /* ignore */ }
  return '';
}

function getVersionFileSize(cachedItem: CachedItem): string {
  try {
    const sources: ParsedMediaSource[] = JSON.parse(cachedItem.media_sources || '[]');
    if (sources.length > 0 && sources[0].Size) {
      return formatFileSize(sources[0].Size);
    }
  } catch { /* ignore */ }
  return '';
}

function getVersionResolution(cachedItem: CachedItem): number {
  try {
    const sources: ParsedMediaSource[] = JSON.parse(cachedItem.media_sources || '[]');
    if (sources.length > 0) return sources[0].Width || 0;
  } catch { /* ignore */ }
  return 0;
}

function pickPreferredVersion(versions: CachedItem[], preference: 'highest' | 'lowest'): CachedItem {
  const sorted = [...versions].sort((a, b) => getVersionResolution(a) - getVersionResolution(b));
  return preference === 'highest' ? sorted[sorted.length - 1] : sorted[0];
}

export default function DetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { play } = usePlay();
  const [item, setItem] = useState<BaseItemDto | null>(null);
  const [similar, setSimilar] = useState<BaseItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [enriched, setEnriched] = useState(false);

  // Dedup versions
  const [versions, setVersions] = useState<CachedItem[]>([]);
  const [selectedVersionId, setSelectedVersionId] = useState<string | null>(null);
  const [preferredQuality, setPreferredQuality] = useState<'highest' | 'lowest'>('highest');
  const [serverNames, setServerNames] = useState<Record<string, string>>({});

  // Series-specific
  const [seasons, setSeasons] = useState<BaseItemDto[]>([]);
  const [selectedSeasonId, setSelectedSeasonId] = useState<string | null>(null);
  const [episodes, setEpisodes] = useState<BaseItemDto[]>([]);
  const [episodesLoading, setEpisodesLoading] = useState(false);
  const [epVersionsByNumber, setEpVersionsByNumber] = useState<Map<number, CachedItem[]>>(new Map());
  const [epPickerSiblings, setEpPickerSiblings] = useState<CachedItem[] | null>(null);

  // Episode nav (cross-version via dedup group)
  const [adjacent, setAdjacent] = useState<{ prev: CachedItem | null; next: CachedItem | null }>({ prev: null, next: null });

  // Toggle states
  const [isFavorite, setIsFavorite] = useState(false);
  const [isPlayed, setIsPlayed] = useState(false);
  const [overviewExpanded, setOverviewExpanded] = useState(false);
  const [favBounce, setFavBounce] = useState(false);
  const [playedBounce, setPlayedBounce] = useState(false);

  // Media source picker
  const [pickerSources, setPickerSources] = useState<MediaSourceType[] | null>(null);
  const [pickerItem, setPickerItem] = useState<BaseItemDto | null>(null);

  // Derived (safe before item loads — item?. access)
  const isEpisode = item?.Type === 'Episode';
  const isSeries = item?.Type === 'Series';
  const activeSeriesId: string | null = selectedVersionId ?? id ?? null;

  useEffect(() => {
    if (!id) return;
    setLoading(true);
    setItem(null);
    setEnriched(false);
    setVersions([]);
    setSelectedVersionId(null);
    setSeasons([]);
    setEpisodes([]);
    setSimilar([]);
    setSelectedSeasonId(null);
    setEpVersionsByNumber(new Map());

    (async () => {
      // Load preferred quality setting
      const settingsRes = await window.api.settings.getValue('preferredQuality');
      if (settingsRes.success && settingsRes.data) {
        setPreferredQuality(settingsRes.data as 'highest' | 'lowest');
      }

      // 1. Try SQLite cache for instant render of basic info
      const cacheRes = await window.api.cache.getItem(id);
      if (cacheRes.success && cacheRes.data) {
        const cached = cachedToBaseItem(cacheRes.data as CachedItem);
        setItem(cached);
        setIsFavorite(cached.UserData?.IsFavorite ?? false);
        setIsPlayed(cached.UserData?.Played ?? false);
        setLoading(false);
      }

      // 2. Fetch versions (dedup) + server names for cross-server display
      window.api.dedup.getVersions(id).then((vRes) => {
        if (vRes.success && vRes.data) {
          setVersions(vRes.data as CachedItem[]);
        }
      });
      window.api.servers.getAll().then((sRes) => {
        if (sRes.success && sRes.data) {
          const map: Record<string, string> = {};
          for (const s of sRes.data as ServerConfig[]) map[s.id] = s.name;
          setServerNames(map);
        }
      });

      // 3. Fetch full item from Emby API for rich data (cast, media sources, etc.)
      const res = await window.api.library.getItem(id);
      if (res.success) {
        const data = res.data as BaseItemDto;
        setItem(data);
        setIsFavorite(data.UserData?.IsFavorite ?? false);
        setIsPlayed(data.UserData?.Played ?? false);
        setEnriched(true);
      }

      setLoading(false);

      // 4. Similar items in background
      window.api.library.getSimilar(id).then((r) => {
        if (r.success) setSimilar((r.data as ItemsResult).Items);
      });
    })();
  }, [id]);

  // Seasons fetch — re-runs when active series changes (version pill click)
  useEffect(() => {
    if (!isSeries || !activeSeriesId) return;
    window.api.library.getSeasons(activeSeriesId).then((sRes) => {
      if (!sRes.success) return;
      const sList = (sRes.data as ItemsResult).Items;
      setSeasons(sList);
      const unwatched = sList.find((s) => !s.UserData?.Played);
      setSelectedSeasonId(unwatched?.Id ?? sList[0]?.Id ?? null);
    });
  }, [activeSeriesId, isSeries]);

  // Episodes + per-episode dedup versions
  useEffect(() => {
    if (!selectedSeasonId || !isSeries || !activeSeriesId) return;
    setEpisodesLoading(true);
    window.api.library.getEpisodes(activeSeriesId, selectedSeasonId).then((r) => {
      if (r.success) setEpisodes((r.data as ItemsResult).Items);
      setEpisodesLoading(false);
    });
    const seasonNumber = seasons.find((s) => s.Id === selectedSeasonId)?.IndexNumber;
    if (seasonNumber != null) {
      window.api.dedup.getEpisodes(activeSeriesId, seasonNumber).then((r) => {
        if (!r.success || !r.data) {
          setEpVersionsByNumber(new Map());
          return;
        }
        const map = new Map<number, CachedItem[]>();
        for (const g of r.data as EpisodeVersionGroup[]) {
          if (g.items.length > 1) map.set(g.episode_number, g.items);
        }
        setEpVersionsByNumber(map);
      });
    } else {
      setEpVersionsByNumber(new Map());
    }
  }, [selectedSeasonId, activeSeriesId, isSeries, seasons]);

  // Load adjacent episodes (dedup-aware, crosses series versions) when viewing an Episode
  useEffect(() => {
    setAdjacent({ prev: null, next: null });
    if (!id || !item || item.Type !== 'Episode') return;
    window.api.dedup.getAdjacentEpisodes(id).then((r) => {
      if (r.success && r.data) setAdjacent(r.data);
    });
  }, [id, item]);

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

  /** Play the preferred quality version of the current item */
  const handlePlayPreferred = useCallback(async () => {
    if (!item) return;
    if (versions.length > 1) {
      const preferred = pickPreferredVersion(versions, preferredQuality);
      const targetId = preferred.emby_id;
      const pbRes = await window.api.media.getPlaybackInfo(targetId);
      if (!pbRes.success || !pbRes.data) return;
      const sources = pbRes.data.MediaSources;
      const target: BaseItemDto = { ...item, Id: targetId };
      if (sources.length > 1) {
        setPickerSources(sources);
        setPickerItem(target);
      } else {
        play(target, sources[0]);
      }
    } else {
      handlePlay(item);
    }
  }, [item, versions, preferredQuality, play, handlePlay]);

  /** Play a specific version by its emby_id */
  const handlePlayVersion = useCallback(async (embyId: string) => {
    const pbRes = await window.api.media.getPlaybackInfo(embyId);
    if (!pbRes.success || !pbRes.data) return;
    const sources = pbRes.data.MediaSources;
    const target: BaseItemDto = { ...(item || {} as BaseItemDto), Id: embyId };
    if (sources.length > 1) {
      setPickerSources(sources);
      setPickerItem(target);
    } else {
      play(target, sources[0]);
    }
  }, [item, play]);

  const handleSourcePick = useCallback((source: MediaSourceType) => {
    if (pickerItem) play(pickerItem, source);
    setPickerSources(null);
    setPickerItem(null);
  }, [pickerItem, play]);

  const handleEpisodePlay = useCallback((ep: BaseItemDto) => {
    const siblings = epVersionsByNumber.get(ep.IndexNumber ?? -1) ?? [];
    if (siblings.length > 1) {
      setEpPickerSiblings(siblings);
    } else {
      handlePlay(ep);
    }
  }, [epVersionsByNumber, handlePlay]);

  const handleEpisodeVersionPick = useCallback((sibling: CachedItem) => {
    setEpPickerSiblings(null);
    handlePlay(cachedToBaseItem(sibling));
  }, [handlePlay]);

  if (loading) return <LoadingSpinner size={48} />;
  if (!item) return <div className={styles.empty}>Item not found</div>;

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
                <button className={styles.playBtn} onClick={versions.length > 1 ? handlePlayPreferred : () => handlePlay(item)}>
                  <Play size={16} fill="currentColor" /> Play
                  {versions.length > 1 && (
                    <span className={styles.playQualityHint}>{getQualityLabel(pickPreferredVersion(versions, preferredQuality))}</span>
                  )}
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

        {versions.length > 1 && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>Versions</h2>
            <div className={styles.versionPills}>
              {[...versions]
                .sort((a, b) => isSeries
                  ? (a.library_name || '').localeCompare(b.library_name || '')
                  : getVersionResolution(b) - getVersionResolution(a))
                .map((v) => {
                  const active = isSeries && v.emby_id === activeSeriesId;
                  const isPreferred = !isSeries && v.emby_id === pickPreferredVersion(versions, preferredQuality).emby_id;
                  const serverLabel = v.server_id && serverNames[v.server_id] ? serverNames[v.server_id] : null;
                  const libLabel = v.library_name || 'Unknown';
                  const quality = isSeries ? null : getQualityLabel(v);
                  const codec = isSeries ? null : getVersionCodecInfo(v);
                  const size = isSeries ? null : getVersionFileSize(v);
                  return (
                    <button
                      key={v.emby_id}
                      className={`${styles.versionPill} ${active ? styles.versionPillActive : ''}`}
                      onClick={() => isSeries ? setSelectedVersionId(v.emby_id) : handlePlayVersion(v.emby_id)}
                      title={libLabel}
                    >
                      {isPreferred && <span className={styles.versionPillDot} />}
                      {quality && <span className={styles.versionPillQuality}>{quality}</span>}
                      {codec && <span className={styles.versionPillMeta}>{'\u00b7'} {codec}</span>}
                      {size && <span className={styles.versionPillMeta}>{'\u00b7'} {size}</span>}
                      <span className={styles.versionPillMeta}>
                        {serverLabel ? `[${serverLabel}] ` : ''}{libLabel}
                      </span>
                    </button>
                  );
                })}
            </div>
          </section>
        )}

        {isEpisode && (adjacent.prev || adjacent.next) && (
          <div className={styles.episodeNav}>
            {adjacent.prev && (
              <button
                className={styles.episodeNavBtn}
                onClick={() => navigate(`/detail/${adjacent.prev!.emby_id}`)}
                title={`S${adjacent.prev.season_number} · E${adjacent.prev.episode_number} — ${adjacent.prev.name}`}
              >
                <ArrowLeft size={14} /> Previous Episode
              </button>
            )}
            <div className={styles.episodeNavSpacer} />
            {adjacent.next && (
              <button
                className={styles.episodeNavBtn}
                onClick={() => navigate(`/detail/${adjacent.next!.emby_id}`)}
                title={`S${adjacent.next.season_number} · E${adjacent.next.episode_number} — ${adjacent.next.name}`}
              >
                Next Episode <ArrowRight size={14} />
              </button>
            )}
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
                  <EpisodeRow
                    key={ep.Id}
                    episode={ep}
                    versions={epVersionsByNumber.get(ep.IndexNumber ?? -1) ?? []}
                    navigate={navigate}
                    onPlay={() => handleEpisodePlay(ep)}
                  />
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

      {/* Episode version picker (per-episode dedup siblings) */}
      {epPickerSiblings && (
        <EpisodeVersionPicker
          siblings={epPickerSiblings}
          serverNames={serverNames}
          onSelect={handleEpisodeVersionPick}
          onCancel={() => setEpPickerSiblings(null)}
        />
      )}
    </div>
  );
}

// ── Sub-components ──────────────────────────────────────

function EpisodeRow({ episode, versions, navigate, onPlay }: {
  episode: BaseItemDto;
  versions: CachedItem[];
  navigate: (path: string) => void;
  onPlay: () => void;
}) {
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
      {versions.length > 1 && (
        <span className={styles.epVersionBadge}>{versions.length}&times;</span>
      )}
      <button className={styles.epPlayBtn} onClick={(e) => { e.stopPropagation(); onPlay(); }}>
        <Play size={16} fill="currentColor" />
      </button>
    </div>
  );
}

function EpisodeVersionPicker({ siblings, serverNames, onSelect, onCancel }: {
  siblings: CachedItem[];
  serverNames: Record<string, string>;
  onSelect: (sibling: CachedItem) => void;
  onCancel: () => void;
}) {
  return (
    <div className={pickerStyles.backdrop} onClick={onCancel}>
      <div className={pickerStyles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={pickerStyles.header}>
          <div className={pickerStyles.title}>Choose Episode Version</div>
          <div className={pickerStyles.subtitle}>{siblings.length} versions available</div>
        </div>
        <div className={styles.versionPills}>
          {siblings.map((s) => {
            const codec = getVersionCodecInfo(s);
            const size = getVersionFileSize(s);
            const serverLabel = s.server_id && serverNames[s.server_id] ? serverNames[s.server_id] : null;
            return (
              <button key={s.emby_id} className={styles.versionPill} onClick={() => onSelect(s)}>
                <span className={styles.versionPillQuality}>{getQualityLabel(s)}</span>
                {codec && <span className={styles.versionPillMeta}>{'\u00b7'} {codec}</span>}
                {size && <span className={styles.versionPillMeta}>{'\u00b7'} {size}</span>}
                <span className={styles.versionPillMeta}>
                  {serverLabel ? `[${serverLabel}] ` : ''}{s.library_name || 'Unknown'}
                </span>
              </button>
            );
          })}
        </div>
        <button className={pickerStyles.cancelBtn} onClick={onCancel}>Cancel</button>
      </div>
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
