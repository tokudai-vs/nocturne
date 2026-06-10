export type Encoder = 'h264_nvenc' | 'h264_qsv' | 'h264_amf' | 'libx264';

export interface EncoderResult {
  preferred: Encoder;
  available: Encoder[];
  probedAt: number;
}

// Source context threaded from DetailPage through WatchPartyButton →
// PreFlightModal → (later) session manager. We carry identifiers, not a
// resolved stream URL: tokens live in main, and the cross-server lookup
// reuses the existing embyClient.getStreamUrlForServer path.
export interface WatchPartyVersion {
  serverId: string;
  itemId: string;
  mediaSourceId: string;
  widthPx: number;       // drives 1080p-vs-4K selection
  qualityLabel: string;  // display, e.g. "1080p"
}

export interface WatchPartySource {
  title: string;
  versions: WatchPartyVersion[];
  /** Source runtime in seconds; 0 when unknown (older cache rows). */
  durationSec?: number;
  /**
   * Saved resume position in seconds (Emby PlaybackPositionTicks ÷ 1e7).
   * 0 / undefined when there's no resume to offer. The pre-flight modal
   * shows a "Start from" radio only when this is > 0.
   */
  resumeSec?: number;
}

// Default picks the 1080p version; falls back to 4K if no 1080p exists,
// then to the first version (covers 720p-only and unknown-width=0 cases).
// prefer4kSource=true (piece 9) flips the order.
export function selectWatchPartySource(
  versions: WatchPartyVersion[],
  opts: { prefer4kSource?: boolean } = {},
): WatchPartyVersion {
  if (versions.length === 0) throw new Error('selectWatchPartySource: no versions');
  const is1080 = (v: WatchPartyVersion) => v.widthPx >= 1920 && v.widthPx < 3840;
  const is4k = (v: WatchPartyVersion) => v.widthPx >= 3840;
  if (opts.prefer4kSource) {
    return versions.find(is4k) ?? versions.find(is1080) ?? versions[0];
  }
  return versions.find(is1080) ?? versions.find(is4k) ?? versions[0];
}
