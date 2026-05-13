import axios, { AxiosInstance } from 'axios';

// TheIntroDB — community-curated intro/recap/credits/preview timestamps.
// Reads are unauthenticated. Cached for the entire session (segments don't
// change often) to keep this off the playback hot path on rewatches.
//
// API requires `imdb_id` (with the "tt" prefix) as the primary identifier.
// Items lacking an IMDB id can't be looked up.

const INTRODB_BASE = 'https://api.introdb.app';
const TIMEOUT_MS = 5000;

// Each segment type is a single object (or null), not an array. Fields seen
// in real responses include start_sec/end_sec/start_ms/end_ms/confidence/
// submission_count/updated_at; we only consume start_sec / end_sec.
export interface IntroDBSegment {
  start_sec: number | null;
  end_sec: number | null;
  start_ms?: number | null;
  end_ms?: number | null;
  confidence?: number;
  submission_count?: number;
}

export interface IntroDBResponse {
  imdb_id?: string;
  tmdb_id?: number;
  season?: number;
  episode?: number;
  intro: IntroDBSegment | null;
  recap: IntroDBSegment | null;
  // The API calls credits "outro". We keep the API field name here and map
  // to "credits" at the consumer (Lua / settings key skipCreditsMode).
  outro: IntroDBSegment | null;
}

const cache = new Map<string, IntroDBResponse>();
let http: AxiosInstance | null = null;

function client(): AxiosInstance {
  if (!http) http = axios.create({ baseURL: INTRODB_BASE, timeout: TIMEOUT_MS });
  return http;
}

function cacheKey(
  imdbId: string,
  type: 'movie' | 'show',
  season?: number,
  episode?: number,
): string {
  if (type === 'show') return `imdb-${imdbId}-s${season ?? 0}e${episode ?? 0}`;
  return `imdb-${imdbId}-movie`;
}

export async function fetchSegments(
  imdbId: string,
  type: 'movie' | 'show',
  season?: number,
  episode?: number,
  tmdbId?: number,
): Promise<IntroDBResponse | null> {
  if (!imdbId) return null;
  const key = cacheKey(imdbId, type, season, episode);
  const hit = cache.get(key);
  if (hit) return hit;

  const params: Record<string, string | number> = { imdb_id: imdbId, type };
  if (tmdbId && !Number.isNaN(tmdbId)) params.tmdb_id = tmdbId;
  if (type === 'show') {
    if (season != null) params.season = season;
    if (episode != null) params.episode = episode;
  }

  try {
    const res = await client().get<IntroDBResponse>('/segments', { params });
    console.log('[introdb] raw response:', JSON.stringify(res.data));
    if (res.status === 200 && res.data) {
      cache.set(key, res.data);
      return res.data;
    }
    return null;
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    console.warn(`[introdb] fetch failed for ${key}:`, msg);
    return null;
  }
}
