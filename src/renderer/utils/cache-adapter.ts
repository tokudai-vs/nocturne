import type { BaseItemDto, CachedItem } from '../api/types';

/**
 * Convert a SQLite CachedItem row into a BaseItemDto that existing
 * components (MediaCard, MediaRow, HeroBackdrop, etc.) can render.
 */
export function cachedToBaseItem(item: CachedItem): BaseItemDto {
  let imageTags: Record<string, string> | undefined;
  try {
    imageTags = item.image_tags ? JSON.parse(item.image_tags) : undefined;
  } catch {
    imageTags = undefined;
  }

  let backdropTags: string[] | undefined;
  try {
    backdropTags = item.backdrop_tags ? JSON.parse(item.backdrop_tags) : undefined;
  } catch {
    backdropTags = undefined;
  }

  let genres: string[] | undefined;
  try {
    genres = item.genres ? JSON.parse(item.genres) : undefined;
  } catch {
    genres = undefined;
  }

  let studios: { Name: string }[] | undefined;
  try {
    studios = item.studios ? JSON.parse(item.studios) : undefined;
  } catch {
    studios = undefined;
  }

  const result: BaseItemDto = {
    Id: item.emby_id,
    Name: item.name,
    serverId: item.server_id,
    Type: item.type as BaseItemDto['Type'],
    Overview: item.overview ?? undefined,
    RunTimeTicks: item.runtime_ticks ?? undefined,
    CommunityRating: item.community_rating ?? undefined,
    OfficialRating: item.official_rating ?? undefined,
    ProductionYear: item.production_year ?? undefined,
    PremiereDate: item.premiere_date ?? undefined,
    Genres: genres,
    Studios: studios,
    ImageTags: imageTags,
    BackdropImageTags: backdropTags,
    SeriesId: item.series_id ?? undefined,
    SeriesName: item.series_name ?? undefined,
    SeasonId: item.season_id ?? undefined,
    ParentIndexNumber: item.season_number ?? undefined,
    IndexNumber: item.episode_number ?? undefined,
    UserData: {
      PlaybackPositionTicks: item.playback_position_ticks,
      PlayCount: item.play_count,
      IsFavorite: item.is_favorite === 1,
      Played: item.played === 1,
      PlayedPercentage: item.played_percentage || undefined,
    },
  };

  // Carry through version_count from deduped search results
  if (item.version_count && item.version_count > 1) {
    (result as BaseItemDto & { versionCount?: number }).versionCount = item.version_count;
  }

  return result;
}

/** Convert an array of cached items */
export function cachedToBaseItems(items: CachedItem[]): BaseItemDto[] {
  return items.map(cachedToBaseItem);
}
