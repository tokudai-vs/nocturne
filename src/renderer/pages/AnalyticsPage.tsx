import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ArrowLeft, BarChart3 } from 'lucide-react';
import type { AnalyticsSource, AnalyticsStats, TraktStatus } from '../api/types';
import ConfirmDialog from '../components/ui/ConfirmDialog';
import { useToastStore } from '../stores/toast-store';
import styles from './AnalyticsPage.module.css';

type RangeKey = '30d' | '90d' | '1y' | 'all';

const SOURCE_OPTIONS: Array<{ key: AnalyticsSource; label: string }> = [
  { key: 'local', label: 'Local' },
  { key: 'trakt', label: 'Trakt' },
  { key: 'combined', label: 'Combined' },
];

const RANGE_OPTIONS: Array<{ key: RangeKey; label: string; days: number | null }> = [
  { key: '30d', label: 'Last 30 days', days: 30 },
  { key: '90d', label: 'Last 90 days', days: 90 },
  { key: '1y', label: 'Last year', days: 365 },
  { key: 'all', label: 'All time', days: null },
];

function resolveRange(key: RangeKey): { rangeStart: string; rangeEnd: string } {
  const end = new Date();
  if (key === 'all') {
    return { rangeStart: '1970-01-01T00:00:00.000Z', rangeEnd: end.toISOString() };
  }
  const opt = RANGE_OPTIONS.find((r) => r.key === key);
  const days = opt?.days ?? 30;
  const start = new Date(end.getTime() - days * 24 * 60 * 60 * 1000);
  return { rangeStart: start.toISOString(), rangeEnd: end.toISOString() };
}

function formatHours(sec: number): string {
  if (!sec) return '0h';
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

// ── Heatmap (GitHub contributions style) ──────────────────────
//
// Layout (logical viewBox coordinates):
//   ┌──────────────────────────────────────────┐
//   │   May    Jun    Jul    Aug    Sep        │  ← month labels (top)
//   │ Mon  ▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢ │
//   │ Wed  ▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢ │  ← cell rows × 7 (Sun→Sat)
//   │ Fri  ▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢▢ │
//   │                       Less ▢▢▢▢▢ More    │  ← legend (bottom-right)
//   └──────────────────────────────────────────┘
//
// 12×12 cells with 2px gap. SVG scales via viewBox + width=100%, so cells
// render larger when the card is wider than natural pixel width.

const HEATMAP_GAP = 2;
const HEATMAP_BOTTOM = 22; // legend band

/** Cell + label sizing keyed to column count. Wide ranges (year view) use
 *  small GitHub-style cells; narrow ranges (30d) use fat cells so the
 *  heatmap has visual presence on the card. Returned widths/heights are
 *  rendered as literal pixel dimensions — no viewBox scaling, so the SVG
 *  size doesn't blow up when its parent card is wide. */
function heatmapSizing(cols: number): {
  cell: number;
  fontSize: number;
  leftGutter: number;
  topGutter: number;
} {
  if (cols <= 5)  return { cell: 28, fontSize: 12, leftGutter: 36, topGutter: 18 };
  if (cols <= 13) return { cell: 18, fontSize: 11, leftGutter: 32, topGutter: 16 };
  if (cols <= 27) return { cell: 14, fontSize: 10, leftGutter: 30, topGutter: 14 };
  return            { cell: 12, fontSize: 10, leftGutter: 30, topGutter: 14 };
}

const HEATMAP_BUCKETS = [
  '#1a1a1a', // empty
  'rgba(229, 160, 13, 0.25)',
  'rgba(229, 160, 13, 0.50)',
  'rgba(229, 160, 13, 0.75)',
  'rgba(229, 160, 13, 1.00)',
];
const DOW_LABELS = ['', 'Mon', '', 'Wed', '', 'Fri', '']; // Sun=0..Sat=6
const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Bucket a count into 0..4 using quantile thresholds on the non-zero
 *  values. Pure zero always stays 0; everything else picks the highest
 *  bucket whose threshold it meets. */
function bucketForCount(count: number, thresholds: number[]): number {
  if (count <= 0) return 0;
  for (let i = thresholds.length - 1; i >= 0; i--) {
    if (count >= thresholds[i]) return i + 1;
  }
  return 1;
}

function HeatmapChart({ data, rangeStart, rangeEnd }: { data: AnalyticsStats['activityByDay']; rangeStart: string; rangeEnd: string }) {
  const byDate = useMemo(() => {
    const m = new Map<string, number>();
    for (const d of data) m.set(d.date, d.count);
    return m;
  }, [data]);

  // Build day list aligned to the preceding Sunday so column 0 = a full week.
  // Cap at 1 year for "All time" — beyond that the grid loses readability.
  const days = useMemo(() => {
    const start = new Date(rangeStart);
    const end = new Date(rangeEnd);
    const minStart = new Date(end.getTime() - 365 * 24 * 60 * 60 * 1000);
    const actualStart = start < minStart ? minStart : start;
    const dow = actualStart.getUTCDay();
    const gridStart = new Date(actualStart.getTime() - dow * 24 * 60 * 60 * 1000);
    const out: Array<{ date: string; count: number; dateObj: Date }> = [];
    for (let t = gridStart.getTime(); t <= end.getTime(); t += 24 * 60 * 60 * 1000) {
      const d = new Date(t);
      const iso = d.toISOString().slice(0, 10);
      out.push({ date: iso, count: byDate.get(iso) ?? 0, dateObj: d });
    }
    return out;
  }, [byDate, rangeStart, rangeEnd]);

  // Quartile thresholds across non-zero days — gives a fairer color spread
  // than uniform max-scaling when a few outlier days would crush everything
  // else into the lowest bucket.
  const thresholds = useMemo(() => {
    const nonZero = days.map((d) => d.count).filter((c) => c > 0).sort((a, b) => a - b);
    if (nonZero.length === 0) return [1, 1, 1, 1];
    const q = (p: number) => nonZero[Math.min(nonZero.length - 1, Math.floor(p * nonZero.length))];
    return [Math.max(1, q(0.25)), Math.max(1, q(0.5)), Math.max(1, q(0.75)), Math.max(1, q(0.95))];
  }, [days]);

  const weekCount = Math.ceil(days.length / 7);
  const sizing = heatmapSizing(weekCount);
  const cellStep = sizing.cell + HEATMAP_GAP;
  const gridW = weekCount * cellStep;

  // Legend cells stay GitHub-small regardless of grid cell size. Keeps the
  // bottom-right legend compact even when the main cells are fat (30d view).
  const LEGEND_CELL = 10;
  const LEGEND_GAP = 2;
  const LEGEND_FONT = 10;

  const svgW = sizing.leftGutter + gridW + 4;
  const svgH = sizing.topGutter + 7 * cellStep + HEATMAP_BOTTOM;

  // Month labels: walk columns and emit a label whenever the column's first
  // day is in a different month from the previous column's first day. Tiny
  // de-dup tracks the last x position so labels never crowd each other.
  const monthLabels = useMemo(() => {
    const out: Array<{ x: number; label: string }> = [];
    let lastMonth = -1;
    let lastX = -Infinity;
    const minSpacing = sizing.fontSize * 2.5;
    for (let w = 0; w < weekCount; w++) {
      const colStart = days[w * 7];
      if (!colStart) continue;
      const m = colStart.dateObj.getUTCMonth();
      if (m !== lastMonth) {
        const x = sizing.leftGutter + w * cellStep;
        if (x - lastX >= minSpacing) {
          out.push({ x, label: MONTH_NAMES[m] });
          lastX = x;
        }
        lastMonth = m;
      }
    }
    return out;
  }, [days, weekCount, cellStep, sizing.leftGutter, sizing.fontSize]);

  const legendY = sizing.topGutter + 7 * cellStep + 8;
  const legendW = HEATMAP_BUCKETS.length * (LEGEND_CELL + LEGEND_GAP) - LEGEND_GAP;
  const legendX = sizing.leftGutter + gridW - legendW;

  return (
    <div className={styles.heatmapWrap}>
      <svg
        width={svgW}
        height={svgH}
        role="img"
        aria-label="Activity heatmap"
      >
        {/* Month labels (top band) */}
        {monthLabels.map((m) => (
          <text key={`${m.label}-${m.x}`} x={m.x} y={sizing.topGutter - 4} fontSize={sizing.fontSize} fill="#888">
            {m.label}
          </text>
        ))}
        {/* Day-of-week labels (left gutter, Mon / Wed / Fri only) */}
        {DOW_LABELS.map((label, i) => (label ? (
          <text
            key={label}
            x={sizing.leftGutter - 6}
            y={sizing.topGutter + i * cellStep + sizing.cell - 2}
            fontSize={sizing.fontSize}
            fill="#888"
            textAnchor="end"
          >
            {label}
          </text>
        ) : null))}
        {/* Cells */}
        {days.map((d, i) => {
          const week = Math.floor(i / 7);
          const dow = i % 7;
          const x = sizing.leftGutter + week * cellStep;
          const y = sizing.topGutter + dow * cellStep;
          const bucket = bucketForCount(d.count, thresholds);
          return (
            <rect
              key={d.date}
              x={x}
              y={y}
              width={sizing.cell}
              height={sizing.cell}
              rx={2}
              fill={HEATMAP_BUCKETS[bucket]}
              className={styles.heatmapCell}
            >
              <title>{`${d.date}: ${d.count} item${d.count === 1 ? '' : 's'} watched`}</title>
            </rect>
          );
        })}
        {/* Legend — compact, bottom-right, fixed small size */}
        <g>
          <text x={legendX - 6} y={legendY + LEGEND_CELL - 1} fontSize={LEGEND_FONT} fill="#888" textAnchor="end">Less</text>
          {HEATMAP_BUCKETS.map((fill, i) => (
            <rect
              key={i}
              x={legendX + i * (LEGEND_CELL + LEGEND_GAP)}
              y={legendY}
              width={LEGEND_CELL}
              height={LEGEND_CELL}
              rx={2}
              fill={fill}
            />
          ))}
          <text x={legendX + legendW + 4} y={legendY + LEGEND_CELL - 1} fontSize={LEGEND_FONT} fill="#888">More</text>
        </g>
      </svg>
    </div>
  );
}

// ── Watch time by day (bar chart) ──────────────────────────────
//
// Logical viewBox 0 0 800 280. SVG scales to card width via width=100%.
// Left margin holds hour labels (50px); bottom margin holds rotated date
// labels (40px); the remaining 750×190 is the plot area.
//
// For 30d / 90d ranges, one bar per day. For 1y / All time, aggregate into
// weekly bars (~52 instead of 365). Bar width is set proportional to
// available plot width.

const CHART_VB_W = 800;
const CHART_VB_H = 280;
const CHART_M_LEFT = 50;
const CHART_M_RIGHT = 10;
const CHART_M_TOP = 10;
const CHART_M_BOTTOM = 40;
const CHART_PLOT_W = CHART_VB_W - CHART_M_LEFT - CHART_M_RIGHT;
const CHART_PLOT_H = CHART_VB_H - CHART_M_TOP - CHART_M_BOTTOM;

const MONTH_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function formatBarDateLabel(iso: string): string {
  const d = new Date(iso);
  return `${MONTH_SHORT[d.getUTCMonth()]} ${d.getUTCDate()}`;
}

function formatHoursMinutes(sec: number): string {
  if (sec <= 0) return '0m';
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

/** Pick a "nice" Y-axis step (1, 2, 5, 10, 20, 50, …) so labels are round. */
function niceStep(maxHours: number): number {
  if (maxHours <= 0) return 1;
  const tries = [0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
  for (const s of tries) {
    if (maxHours / s <= 5) return s;
  }
  return Math.ceil(maxHours / 5);
}

function WatchTimeChart({ data, rangeKey, rangeStart, rangeEnd }: { data: AnalyticsStats['activityByDay']; rangeKey: RangeKey; rangeStart: string; rangeEnd: string }) {
  // Aggregate to weekly for year/all ranges; daily otherwise. CRITICAL:
  // buckets must span the full range, including zero-activity days, so bar
  // X positions stay proportional to wall-clock time. data[] only contains
  // active days (SQL GROUP BY), so we backfill.
  const aggregateWeekly = rangeKey === '1y' || rangeKey === 'all';
  const buckets = useMemo(() => {
    const bySrcDate = new Map<string, number>();
    for (const d of data) bySrcDate.set(d.date, d.watchTimeSeconds);

    const end = new Date(rangeEnd);
    let start = new Date(rangeStart);
    // Cap "All time" to 1 year for the bar chart — beyond that bars become
    // invisible noise. The heatmap applies the same cap.
    const minStart = new Date(end.getTime() - 365 * 24 * 60 * 60 * 1000);
    if (start < minStart) start = minStart;

    if (!aggregateWeekly) {
      const out: Array<{ key: string; watchTimeSeconds: number }> = [];
      for (let t = start.getTime(); t <= end.getTime(); t += 24 * 60 * 60 * 1000) {
        const iso = new Date(t).toISOString().slice(0, 10);
        out.push({ key: iso, watchTimeSeconds: bySrcDate.get(iso) ?? 0 });
      }
      return out;
    }

    // Weekly: Monday-anchored week buckets covering [start, end].
    const dow = (start.getUTCDay() + 6) % 7;
    const firstMonday = new Date(start.getTime() - dow * 24 * 60 * 60 * 1000);
    const byWeek = new Map<string, number>();
    for (let t = firstMonday.getTime(); t <= end.getTime(); t += 7 * 24 * 60 * 60 * 1000) {
      byWeek.set(new Date(t).toISOString().slice(0, 10), 0);
    }
    for (const d of data) {
      const date = new Date(d.date);
      if (date < firstMonday) continue;
      const offset = (date.getUTCDay() + 6) % 7;
      const monday = new Date(date.getTime() - offset * 24 * 60 * 60 * 1000);
      const key = monday.toISOString().slice(0, 10);
      byWeek.set(key, (byWeek.get(key) ?? 0) + d.watchTimeSeconds);
    }
    return Array.from(byWeek.entries())
      .map(([key, seconds]) => ({ key, watchTimeSeconds: seconds }))
      .sort((a, b) => a.key.localeCompare(b.key));
  }, [data, aggregateWeekly, rangeStart, rangeEnd]);

  if (buckets.length === 0) return <div className={styles.empty}>No activity in this range.</div>;
  // If every bucket is zero, render the empty state rather than a flat
  // zero-axis chart.
  if (buckets.every((b) => b.watchTimeSeconds === 0)) {
    return <div className={styles.empty}>No activity in this range.</div>;
  }

  const maxHours = buckets.reduce((acc, b) => Math.max(acc, b.watchTimeSeconds / 3600), 0);
  const step = niceStep(maxHours);
  // Top of plot rounded up to the next step so the tallest bar fits cleanly.
  const yMax = Math.max(step, Math.ceil(maxHours / step) * step);
  const ticks: number[] = [];
  for (let v = 0; v <= yMax + 1e-6; v += step) ticks.push(v);

  const barSpan = CHART_PLOT_W / buckets.length;
  const barW = Math.max(2, Math.min(barSpan * 0.8, 24));

  // Sparse X labels.
  const labelEvery = (() => {
    if (aggregateWeekly) return 4;          // ~monthly out of 52 weekly bars
    if (rangeKey === '30d') return 5;
    if (rangeKey === '90d') return 14;
    return Math.max(1, Math.ceil(buckets.length / 10));
  })();

  return (
    <div className={styles.chartWrap}>
      <svg
        viewBox={`0 0 ${CHART_VB_W} ${CHART_VB_H}`}
        width="100%"
        preserveAspectRatio="xMinYMid meet"
        role="img"
        aria-label="Watch time by day"
      >
        {/* Gridlines + Y-axis labels */}
        {ticks.map((t) => {
          const y = CHART_M_TOP + (1 - t / yMax) * CHART_PLOT_H;
          return (
            <g key={`y-${t}`}>
              <line
                x1={CHART_M_LEFT}
                x2={CHART_M_LEFT + CHART_PLOT_W}
                y1={y}
                y2={y}
                stroke="#2a2a2a"
                strokeWidth={1}
              />
              <text x={CHART_M_LEFT - 6} y={y + 3} fontSize="10" fill="#666" textAnchor="end">
                {t < 1 ? `${Math.round(t * 60)}m` : `${t}h`}
              </text>
            </g>
          );
        })}

        {/* Bars */}
        {buckets.map((b, i) => {
          const hours = b.watchTimeSeconds / 3600;
          const h = yMax > 0 ? (hours / yMax) * CHART_PLOT_H : 0;
          const x = CHART_M_LEFT + i * barSpan + (barSpan - barW) / 2;
          const y = CHART_M_TOP + CHART_PLOT_H - h;
          return (
            <rect
              key={b.key}
              x={x}
              y={y}
              width={barW}
              height={h}
              rx={2}
              fill="rgba(229, 160, 13, 0.85)"
            >
              <title>
                {`${b.key}: ${formatHoursMinutes(b.watchTimeSeconds)} watched`}
              </title>
            </rect>
          );
        })}

        {/* X-axis labels (sparse, rotated -45°) */}
        {buckets.map((b, i) => {
          if (i % labelEvery !== 0) return null;
          const cx = CHART_M_LEFT + i * barSpan + barSpan / 2;
          const y = CHART_M_TOP + CHART_PLOT_H + 14;
          return (
            <text
              key={`xl-${b.key}`}
              x={cx}
              y={y}
              fontSize="10"
              fill="#888"
              textAnchor="end"
              transform={`rotate(-45 ${cx} ${y})`}
            >
              {formatBarDateLabel(b.key)}
            </text>
          );
        })}
      </svg>
    </div>
  );
}

// ── Stat cards row ─────────────────────────────────────

function StatCards({ stats }: { stats: AnalyticsStats }) {
  const totalItems = stats.totalWatched.movies + stats.totalWatched.episodes;
  return (
    <div className={styles.statCardRow}>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Total watched</div>
        <div className={styles.statCardValue}>{totalItems}</div>
        <div className={styles.statCardSubtle}>
          {stats.totalWatched.movies} movie{stats.totalWatched.movies === 1 ? '' : 's'} ·{' '}
          {stats.totalWatched.episodes} episode{stats.totalWatched.episodes === 1 ? '' : 's'}
        </div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Total watch time</div>
        <div className={styles.statCardValue}>{formatHours(stats.totalWatchTimeSeconds)}</div>
        <div className={styles.statCardSubtle}>Includes in-progress positions</div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Currently watching</div>
        <div className={styles.statCardValue}>{stats.inProgressSeriesCount}</div>
        <div className={styles.statCardSubtle}>series in progress</div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Average per week</div>
        <div className={styles.statCardValue}>{formatHours(stats.avgPerWeekSeconds)}</div>
        <div className={styles.statCardSubtle}>over selected range</div>
      </div>
    </div>
  );
}

// ── Top series ─────────────────────────────────────────

function PosterImg({ src, fallbackChar, className, fallbackClassName }: { src: string | null; fallbackChar: string; className: string; fallbackClassName: string }) {
  const [errored, setErrored] = useState(false);
  if (!src || errored) {
    return <div className={fallbackClassName}>{fallbackChar.toUpperCase()}</div>;
  }
  return <img src={src} alt="" className={className} onError={() => setErrored(true)} />;
}

function TopSeries({ items }: { items: AnalyticsStats['topSeries'] }) {
  if (items.length === 0) return <div className={styles.empty}>No series watched in this range.</div>;
  const max = Math.max(1, ...items.map((i) => i.episodeCount));
  return (
    <div className={styles.topSeriesList}>
      {items.map((s) => (
        <div key={s.id} className={styles.topSeriesRow}>
          <PosterImg
            src={s.imageUrl}
            fallbackChar={s.name.charAt(0)}
            className={styles.topSeriesPoster}
            fallbackClassName={styles.posterFallback}
          />
          <div className={styles.topSeriesName}>{s.name}</div>
          <div className={styles.topSeriesBarOuter}>
            <div className={styles.topSeriesBarFill} style={{ width: `${(s.episodeCount / max) * 100}%` }} />
          </div>
          <div className={styles.topSeriesCount}>{s.episodeCount} ep{s.episodeCount === 1 ? '' : 's'}</div>
        </div>
      ))}
    </div>
  );
}

// ── Top movies ─────────────────────────────────────────

function TopMovies({ items }: { items: AnalyticsStats['topMovies'] }) {
  if (items.length === 0) return <div className={styles.empty}>No movies watched in this range.</div>;
  return (
    <div className={styles.topMoviesGrid}>
      {items.map((m) => (
        <div key={m.id} className={styles.topMovieCard}>
          <div className={styles.topMoviePosterWrap}>
            <PosterImg
              src={m.imageUrl}
              fallbackChar={m.name.charAt(0)}
              className={styles.topMoviePoster}
              fallbackClassName={styles.topMoviePosterFallback}
            />
          </div>
          <div className={styles.topMovieName} title={m.name}>{m.name}</div>
          {m.lastPlayed && (
            <div className={styles.topMovieSubtle}>{m.lastPlayed.slice(0, 10)}</div>
          )}
        </div>
      ))}
    </div>
  );
}

// ── Genre breakdown ────────────────────────────────────

function GenreBreakdown({ data }: { data: AnalyticsStats['genreBreakdown'] }) {
  if (data.length === 0) return <div className={styles.empty}>No genre data for this range.</div>;
  const max = Math.max(1, ...data.map((d) => d.pct));
  return (
    <div className={styles.genreList}>
      {data.map((g) => (
        <div key={g.genre} className={styles.genreRow}>
          <div className={styles.genreName}>{g.genre}</div>
          <div className={styles.genreBarOuter}>
            <div className={styles.genreBarFill} style={{ width: `${(g.pct / max) * 100}%` }} />
          </div>
          <div className={styles.genrePct}>{g.pct}%</div>
        </div>
      ))}
    </div>
  );
}

// ── Page ───────────────────────────────────────────────

function LifetimeCard({ data }: { data: NonNullable<AnalyticsStats['lifetime']> }) {
  const hours = Math.round(data.watchTimeMinutes / 60);
  return (
    <div className={styles.statCardRow}>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Lifetime movies</div>
        <div className={styles.statCardValue}>{data.movies}</div>
        <div className={styles.statCardSubtle}>watched on Trakt</div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Lifetime episodes</div>
        <div className={styles.statCardValue}>{data.episodes}</div>
        <div className={styles.statCardSubtle}>watched on Trakt</div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Lifetime watch time</div>
        <div className={styles.statCardValue}>{hours.toLocaleString()}h</div>
        <div className={styles.statCardSubtle}>from /users/me/stats</div>
      </div>
      <div className={styles.statCard}>
        <div className={styles.statCardLabel}>Distinct shows</div>
        <div className={styles.statCardValue}>{data.distinctShows}</div>
        <div className={styles.statCardSubtle}>watched on Trakt</div>
      </div>
    </div>
  );
}

export default function AnalyticsPage() {
  const navigate = useNavigate();
  const addToast = useToastStore((s) => s.addToast);
  const [rangeKey, setRangeKey] = useState<RangeKey>('30d');
  const [source, setSource] = useState<AnalyticsSource>('local');
  const [stats, setStats] = useState<AnalyticsStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [traktConnected, setTraktConnected] = useState(false);
  const [backfillStatus, setBackfillStatus] = useState<{ backfilled: boolean; cap: string; eventCount: number } | null>(null);
  const [showBackfillConfirm, setShowBackfillConfirm] = useState(false);
  const [backfillProgress, setBackfillProgress] = useState<{ current: number; total: number } | null>(null);
  const [statsError, setStatsError] = useState<string | null>(null);

  const range = useMemo(() => resolveRange(rangeKey), [rangeKey]);

  // Trakt connection + backfill status on mount; gates source-selector visibility.
  useEffect(() => {
    window.api.trakt.getStatus().then((res) => {
      if (res.success && res.data) setTraktConnected((res.data as TraktStatus).connected);
    });
    window.api.analytics.getBackfillStatus().then((res) => {
      if (res.success && res.data) setBackfillStatus(res.data);
    });
    const unsubProg = window.api.analytics.onBackfillProgress((data) => setBackfillProgress(data));
    const unsubDone = window.api.analytics.onBackfillComplete(({ inserted, total }) => {
      setBackfillProgress(null);
      window.api.analytics.getBackfillStatus().then((res) => {
        if (res.success && res.data) setBackfillStatus(res.data);
      });
      addToast(`Trakt history synced — ${inserted}/${total} events`, 'success');
      // Re-pull stats so the new history is reflected.
      window.api.analytics.getStats({ ...range, source }).then((r) => {
        if (r.success && r.data) setStats(r.data);
      });
    });
    const unsubFail = window.api.analytics.onBackfillFailed((err) => {
      setBackfillProgress(null);
      addToast(`Trakt history sync failed: ${err.message}`, 'error');
    });
    return () => { unsubProg(); unsubDone(); unsubFail(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Force source back to Local when Trakt disconnects.
  useEffect(() => {
    if (!traktConnected && source !== 'local') setSource('local');
  }, [traktConnected, source]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setStatsError(null);
    window.api.analytics.getStats({ ...range, source }).then((res) => {
      if (cancelled) return;
      const ok = res.success && res.data;
      // eslint-disable-next-line no-console
      console.log('[analytics] getStats response', {
        success: res.success,
        source,
        range,
        data: ok ? {
          totalWatched: res.data!.totalWatched,
          totalWatchTimeSeconds: res.data!.totalWatchTimeSeconds,
          activityDays: res.data!.activityByDay?.length,
          topSeries: res.data!.topSeries?.length,
          topMovies: res.data!.topMovies?.length,
          genres: res.data!.genreBreakdown?.length,
          lifetime: res.data!.lifetime,
        } : undefined,
        error: ok ? undefined : res.error,
      });
      if (ok) {
        setStats(res.data!);
      } else {
        setStats(null);
        setStatsError(res.error || 'Unknown error');
      }
      setLoading(false);
    });
    return () => { cancelled = true; };
  }, [range, source]);

  const sourceBadge = source === 'local' ? 'Local' : source === 'trakt' ? 'Trakt' : 'Merged';

  async function startBackfill() {
    setShowBackfillConfirm(false);
    setBackfillProgress({ current: 0, total: 0 });
    const res = await window.api.analytics.triggerBackfill();
    if (!res.success) {
      setBackfillProgress(null);
      addToast(`Backfill failed: ${res.error}`, 'error');
    }
    // Success path lands via the onBackfillComplete listener.
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <button className={styles.backBtn} onClick={() => navigate(-1)} aria-label="Back">
          <ArrowLeft size={20} />
        </button>
        <BarChart3 size={22} />
        <div className={styles.title}>Watch History</div>
        <div className={styles.toolbar}>
          {traktConnected && (
            <>
              <span className={styles.toolbarLabel}>Source</span>
              <div className={styles.segmented}>
                {SOURCE_OPTIONS.map((s) => (
                  <button
                    key={s.key}
                    onClick={() => setSource(s.key)}
                    className={`${styles.segmentedBtn} ${source === s.key ? styles.segmentedBtnActive : ''}`}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
            </>
          )}
          <span className={styles.toolbarLabel}>Range</span>
          <div className={styles.segmented}>
            {RANGE_OPTIONS.map((r) => (
              <button
                key={r.key}
                onClick={() => setRangeKey(r.key)}
                className={`${styles.segmentedBtn} ${rangeKey === r.key ? styles.segmentedBtnActive : ''}`}
              >
                {r.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* Trakt backfill banner */}
      {traktConnected && (source === 'trakt' || source === 'combined') && backfillStatus && !backfillStatus.backfilled && !backfillProgress && (
        <div className={styles.section} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 'var(--space-3)' }}>
          <div>
            <div style={{ fontWeight: 600, marginBottom: 4 }}>Trakt history hasn&rsquo;t been synced yet</div>
            <div style={{ fontSize: 'var(--text-sm)', color: 'var(--text-secondary)' }}>
              Sync your Trakt history (cap: {backfillStatus.cap === 'two-years' ? 'last 2 years' : 'full history'}). Change in Settings → Trakt → Advanced.
            </div>
          </div>
          <button className={styles.segmentedBtnActive + ' ' + styles.segmentedBtn} style={{ background: 'var(--accent)', color: 'var(--bg-primary)', padding: '8px 16px' }} onClick={() => setShowBackfillConfirm(true)}>
            Sync now
          </button>
        </div>
      )}

      {backfillProgress && (
        <div className={styles.section} style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)' }}>
          <div>
            <div style={{ fontWeight: 600, marginBottom: 4 }}>Syncing Trakt history…</div>
            <div style={{ fontSize: 'var(--text-sm)', color: 'var(--text-secondary)' }}>
              {backfillProgress.total > 0
                ? `${backfillProgress.current} / ${backfillProgress.total} events`
                : 'Fetching first page…'}
            </div>
          </div>
        </div>
      )}

      {loading && !stats && <div className={styles.loadingState}>Loading analytics…</div>}

      {!loading && !stats && statsError && (
        <div className={styles.section}>
          <div className={styles.sectionHeader}>Analytics unavailable</div>
          <div className={styles.empty}>Failed to load analytics: {statsError}</div>
        </div>
      )}

      {!loading && stats && stats.totalWatched.movies === 0 && stats.totalWatched.episodes === 0 && stats.activityByDay.length === 0 && (
        <div className={styles.section}>
          <div className={styles.sectionHeader}>No history yet</div>
          <div className={styles.empty}>
            {source === 'local'
              ? 'No watch history yet — start watching to populate this page.'
              : !traktConnected
                ? 'Connect Trakt and trigger a backfill to see history.'
                : !backfillStatus?.backfilled
                  ? 'Trakt history hasn’t been synced yet — click Sync now above to populate this view.'
                  : 'No history in the selected range. Try widening the range.'}
          </div>
        </div>
      )}

      {stats && (
        <>
          {stats.lifetime && (source === 'trakt' || source === 'combined') && (
            <LifetimeCard data={stats.lifetime} />
          )}

          <StatCards stats={stats} />

          <div className={styles.section}>
            <div className={styles.sectionHeader}>
              Activity
              <span className={styles.sectionBadge}>{sourceBadge}</span>
            </div>
            <HeatmapChart data={stats.activityByDay} rangeStart={range.rangeStart} rangeEnd={range.rangeEnd} />
          </div>

          <div className={styles.section}>
            <div className={styles.sectionHeader}>
              Watch frequency by day
              <span className={styles.sectionBadge}>{sourceBadge}</span>
            </div>
            <WatchTimeChart data={stats.activityByDay} rangeKey={rangeKey} rangeStart={range.rangeStart} rangeEnd={range.rangeEnd} />
          </div>

          <div className={styles.section}>
            <div className={styles.sectionHeader}>
              Top series
              <span className={styles.sectionBadge}>{sourceBadge}</span>
            </div>
            <TopSeries items={stats.topSeries} />
          </div>

          <div className={styles.section}>
            <div className={styles.sectionHeader}>
              Top movies
              <span className={styles.sectionBadge}>{sourceBadge}</span>
            </div>
            <TopMovies items={stats.topMovies} />
          </div>

          <div className={styles.section}>
            <div className={styles.sectionHeader}>
              Genre breakdown
              <span className={styles.sectionBadge}>{sourceBadge}</span>
              {stats.unmatchedTraktCount != null && stats.unmatchedTraktCount > 0 && (
                <span className={styles.sectionBadge} title="Trakt history events with no matching local item">
                  {stats.unmatchedTraktCount} unmatched
                </span>
              )}
            </div>
            <GenreBreakdown data={stats.genreBreakdown} />
          </div>
        </>
      )}

      {showBackfillConfirm && backfillStatus && (
        <ConfirmDialog
          title="Sync Trakt history?"
          message={`This will fetch your Trakt watch history (range: ${backfillStatus.cap === 'two-years' ? 'last 2 years' : 'full history'}). May take several minutes for large histories.`}
          confirmLabel="Sync"
          onConfirm={startBackfill}
          onCancel={() => setShowBackfillConfirm(false)}
        />
      )}
    </div>
  );
}
