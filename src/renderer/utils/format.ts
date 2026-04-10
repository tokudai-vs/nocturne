const TICKS_PER_MS = 10000;
const TICKS_PER_SEC = TICKS_PER_MS * 1000;
const TICKS_PER_MIN = TICKS_PER_SEC * 60;
const TICKS_PER_HOUR = TICKS_PER_MIN * 60;

export function formatRuntime(ticks: number | undefined): string {
  if (!ticks) return '';
  const hours = Math.floor(ticks / TICKS_PER_HOUR);
  const mins = Math.floor((ticks % TICKS_PER_HOUR) / TICKS_PER_MIN);
  if (hours > 0) return `${hours}h ${mins}m`;
  return `${mins}m`;
}

export function formatFileSize(bytes: number | undefined): string {
  if (!bytes) return '';
  if (bytes >= 1_073_741_824) return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
  if (bytes >= 1_048_576) return `${(bytes / 1_048_576).toFixed(0)} MB`;
  return `${(bytes / 1024).toFixed(0)} KB`;
}

export function formatBitrate(bps: number | undefined): string {
  if (!bps) return '';
  if (bps >= 1_000_000) return `${(bps / 1_000_000).toFixed(1)} Mbps`;
  return `${(bps / 1000).toFixed(0)} Kbps`;
}

export function formatDate(dateString: string | undefined): string {
  if (!dateString) return '';
  const d = new Date(dateString);
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

export function formatEpisodeNumber(parentIndex?: number, index?: number): string {
  if (parentIndex == null || index == null) return '';
  return `S${String(parentIndex).padStart(2, '0')}E${String(index).padStart(2, '0')}`;
}

export function formatPlayerTime(totalSeconds: number): string {
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = Math.floor(totalSeconds % 60);
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}
