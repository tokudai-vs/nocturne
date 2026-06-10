import { spawn, type ChildProcess } from 'child_process';
import path from 'path';
import type { Encoder } from '../shared/watchparty-types';
import { watchPartyBinaryManager } from './watchparty-binary-manager';
import { watchPartyLogger } from './watchparty-logger';

const SEGMENT_DURATION_SEC = 4;
const TARGET_HEIGHT_DEFAULT = 1080;
const TARGET_AUDIO_BITRATE = '192k';
const STOP_TIMEOUT_MS = 5_000;
const STDERR_BUF_CAP_BYTES = 64 * 1024;
const STDERR_BUF_KEEP_BYTES = 32 * 1024;

export interface TranscoderOptions {
  sourceUrl: string;
  encoder: Encoder;
  sessionDir: string;
  targetHeight?: number; // optional override; defaults to 1080
  /**
   * Movie-time offset (seconds) where the transcode starts. > 0 for
   * Resume. ffmpeg gets `-ss N` BEFORE `-i` (input-seek, fast, relies on
   * HTTP range support — Emby static streams do support it; flag any
   * server that refuses range and falls back to whole-file scan).
   */
  startOffsetSec?: number;
}

type ProgressData = { transcodedSeconds: number };
type ErrorData = { message: string };

// Regex matches ffmpeg's stats line "frame=… time=HH:MM:SS.mmm …" written to
// stderr under -stats. Global so matchAll() walks every occurrence; we keep
// only the latest per chunk for monotonic progress emission.
const TIME_RE = /time=(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,3}))?/g;

export class WatchPartyTranscoder {
  private proc: ChildProcess | null = null;
  private progressCbs: Array<(d: ProgressData) => void> = [];
  private errorCbs: Array<(d: ErrorData) => void> = [];
  private completeCbs: Array<() => void> = [];
  private stderrTail = '';
  private stopping = false;

  onProgress(fn: (d: ProgressData) => void): void {
    this.progressCbs.push(fn);
  }
  onError(fn: (d: ErrorData) => void): void {
    this.errorCbs.push(fn);
  }
  onComplete(fn: () => void): void {
    this.completeCbs.push(fn);
  }

  private dispatch<T>(list: Array<(d: T) => void>, data: T): void {
    for (const fn of list) {
      try {
        fn(data);
      } catch (err) {
        console.error('[watchparty-transcoder] listener threw:', err);
      }
    }
  }

  start(opts: TranscoderOptions): void {
    if (this.proc) throw new Error('Transcoder already running');
    const ffmpegPath = watchPartyBinaryManager.getFfmpegPath();
    const playlistPath = path.join(opts.sessionDir, 'stream.m3u8');
    const segmentPattern = path.join(opts.sessionDir, 'segment_%05d.ts');
    const targetHeight = opts.targetHeight ?? TARGET_HEIGHT_DEFAULT;
    const startOffsetSec =
      typeof opts.startOffsetSec === 'number' && opts.startOffsetSec > 0
        ? opts.startOffsetSec
        : 0;
    const args = buildArgs(
      opts.sourceUrl,
      opts.encoder,
      playlistPath,
      segmentPattern,
      targetHeight,
      startOffsetSec,
    );

    // Log the full argv with the Emby api_key redacted — the logger
    // strips it from any token-bearing arg. ffmpeg path is logged too so
    // we can correlate against the binary manifest if it ever drifts.
    watchPartyLogger.info(
      'transcoder',
      `spawn ffmpeg=${ffmpegPath} encoder=${opts.encoder} targetHeight=${targetHeight} argv=${JSON.stringify(watchPartyLogger.redactArgs(args))}`,
    );

    const proc = spawn(ffmpegPath, args, {
      stdio: ['pipe', 'ignore', 'pipe'],
      windowsHide: true,
    });
    this.proc = proc;
    this.stopping = false;
    this.stderrTail = '';
    if (proc.pid) watchPartyLogger.info('transcoder', `ffmpeg pid=${proc.pid}`);

    proc.stderr?.on('data', (chunk: Buffer) => {
      const text = chunk.toString();
      this.stderrTail += text;
      if (this.stderrTail.length > STDERR_BUF_CAP_BYTES) {
        this.stderrTail = this.stderrTail.slice(-STDERR_BUF_KEEP_BYTES);
      }
      this.emitProgressFrom(text);
    });

    proc.on('error', (err) => {
      watchPartyLogger.error('transcoder', `ffmpeg spawn error: ${err.message}`);
      this.dispatch(this.errorCbs, { message: err.message });
    });

    proc.on('exit', (code, signal) => {
      const wasStopping = this.stopping;
      this.proc = null;
      if (wasStopping) {
        watchPartyLogger.info('transcoder', `ffmpeg exited (graceful stop) code=${code} signal=${signal ?? '-'}`);
        return;
      }
      if (code === 0) {
        watchPartyLogger.info('transcoder', 'ffmpeg exited 0 (EOF reached)');
        this.dispatch(this.completeCbs, undefined);
      } else {
        const tail = this.stderrTail.slice(-2000);
        watchPartyLogger.error(
          'transcoder',
          `ffmpeg exited code=${code} signal=${signal ?? '-'} stderr-tail:\n${tail}`,
        );
        this.dispatch(this.errorCbs, {
          message: `ffmpeg exited code=${code} signal=${signal ?? '-'}\nstderr tail:\n${tail}`,
        });
      }
    });
  }

  // Graceful stop: write 'q' on stdin so ffmpeg finalizes the muxer (writes
  // #EXT-X-ENDLIST). If the process hasn't exited within STOP_TIMEOUT_MS,
  // hard-kill — Windows has no SIGTERM, so .kill() = TerminateProcess. The
  // playlist may be missing ENDLIST in that case, which is acceptable for a
  // forced abort.
  async stop(): Promise<void> {
    if (!this.proc) return;
    this.stopping = true;
    const proc = this.proc;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          /* already exited */
        }
      }, STOP_TIMEOUT_MS);
      proc.once('exit', () => {
        clearTimeout(timer);
        resolve();
      });
      try {
        proc.stdin?.write('q');
        proc.stdin?.end();
      } catch {
        /* stdin closed already — forced kill on timer */
      }
    });
  }

  isRunning(): boolean {
    return this.proc !== null;
  }

  // ffmpeg writes stats with \r overwrites — the same chunk often contains
  // several ticks. Emit only the latest so consumers see monotonic progress.
  private emitProgressFrom(text: string): void {
    let last: RegExpMatchArray | null = null;
    for (const m of text.matchAll(TIME_RE)) {
      last = m;
    }
    if (!last) return;
    const hours = Number(last[1]);
    const minutes = Number(last[2]);
    const seconds = Number(last[3]);
    const frac = last[4] ? Number('0.' + last[4]) : 0;
    const transcodedSeconds = hours * 3600 + minutes * 60 + seconds + frac;
    this.dispatch(this.progressCbs, { transcodedSeconds });
  }
}

// ── Arg builders ──────────────────────────────────────────────

function buildArgs(
  sourceUrl: string,
  encoder: Encoder,
  playlistPath: string,
  segmentPattern: string,
  targetHeight: number,
  startOffsetSec: number,
): string[] {
  // Input-seek (-ss before -i) is fast — ffmpeg issues an HTTP range request
  // to Emby and decodes from a keyframe at the offset, output timeline
  // resets to 0. If a server refuses range, ffmpeg falls back to whole-file
  // scan which is much slower; surface as "transcoder slow to start" in the
  // logs.
  const inputSeek = startOffsetSec > 0 ? ['-ss', String(startOffsetSec)] : [];
  return [
    '-y',
    '-hide_banner',
    '-loglevel', 'error',
    // -stats keeps the time=… lines flowing under -loglevel error so progress
    // parses. -loglevel error alone suppresses them on most builds.
    '-stats',
    // Input-side probe budget. Default probesize=5MB and analyzeduration=5s
    // burn ~30–40s on a remote Emby HTTP source before the first segment
    // appears (measured: ffmpeg pid → first progress took 39s of which the
    // tunnel only owned 5.8s). We know the source is H.264+AAC, so deep
    // probing buys nothing here. Trade-off: if a stream-detection regression
    // surfaces (missing audio, late subs, multi-program TS), back both off
    // to 5M. -fflags +nobuffer skips the initial input-buffering phase.
    '-probesize', '1M',
    '-analyzeduration', '1M',
    '-fflags', '+nobuffer',
    ...inputSeek,
    '-i', sourceUrl,
    ...encoderArgs(encoder, targetHeight),
    // Downscale-only: target height is min(targetHeight, source height).
    // -2 keeps width an even number (required by yuv420p). Backslash escapes
    // the comma inside the expression so ffmpeg's filtergraph parser doesn't
    // split it into two filters.
    '-vf', `scale=-2:min(${targetHeight}\\,ih)`,
    '-pix_fmt', 'yuv420p',
    // Framerate-independent GOP alignment: force a keyframe every
    // SEGMENT_DURATION_SEC of presentation time, regardless of source fps.
    // n_forced advances by 1 each forced keyframe, so the expression yields
    // a keyframe at t=0, 4, 8, … sc_threshold=0 prevents scene-change
    // keyframes mid-segment. The previous -g/-keyint_min approach assumed
    // 30fps and broke on 24fps sources (5s segments).
    '-force_key_frames', `expr:gte(t,n_forced*${SEGMENT_DURATION_SEC})`,
    '-sc_threshold', '0',
    '-c:a', 'aac',
    '-b:a', TARGET_AUDIO_BITRATE,
    '-ac', '2',
    '-f', 'hls',
    '-hls_time', String(SEGMENT_DURATION_SEC),
    // event = #EXT-X-PLAYLIST-TYPE:EVENT — append-only growing, hls.js polls
    // for updates while playing; ENDLIST written on graceful stop or EOF.
    '-hls_playlist_type', 'event',
    '-hls_flags', 'independent_segments',
    '-hls_segment_filename', segmentPattern,
    playlistPath,
  ];
}

// Bitrate ladder keyed off the output ceiling. The pre-flight modal's
// per-guest bandwidth math (MBPS_PER_GUEST) mirrors these numbers — keep
// them in lockstep. bufsize = 2× bitrate (CBR-ish with a 2s window).
function bitrateForHeight(targetHeight: number): { bitrate: string; bufsize: string } {
  if (targetHeight >= 2160) return { bitrate: '20M', bufsize: '40M' };
  if (targetHeight >= 1080) return { bitrate: '5M', bufsize: '10M' };
  return { bitrate: '2.5M', bufsize: '5M' };
}

function encoderArgs(encoder: Encoder, targetHeight: number): string[] {
  const { bitrate, bufsize } = bitrateForHeight(targetHeight);
  const rateArgs = ['-b:v', bitrate, '-maxrate', bitrate, '-bufsize', bufsize];
  switch (encoder) {
    case 'h264_nvenc':
      return ['-c:v', 'h264_nvenc', '-preset', 'p4', '-rc', 'cbr', ...rateArgs];
    case 'libx264':
      // No -tune zerolatency: that's a live-encoder preset; we're
      // transcoding ahead to VOD and the latency tradeoff hurts quality.
      return ['-c:v', 'libx264', '-preset', 'veryfast', ...rateArgs];
    case 'h264_qsv':
      // QSV — spec only, not exercised in batch 1. Real flags depend on
      // driver quirks; revisit once we can probe an Intel iGPU box.
      return ['-c:v', 'h264_qsv', '-preset', 'medium', ...rateArgs];
    case 'h264_amf':
      // AMF — spec only, not exercised in batch 1.
      return ['-c:v', 'h264_amf', '-quality', 'balanced', ...rateArgs];
  }
}
