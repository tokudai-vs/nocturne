import { EventEmitter } from 'events';
import { randomUUID } from 'crypto';
import { app } from 'electron';
import * as fs from 'fs';
import * as path from 'path';

import { embyClient } from './emby-client';
import { serverManager } from './server-manager';
import { getSettingValue } from './settings';
import { watchPartyBinaryManager } from './watchparty-binary-manager';
import { watchPartyEncoderProbe } from './watchparty-encoder-probe';
import { WatchPartyTranscoder } from './watchparty-transcoder';
import { startHttpServer, type HttpServerHandle } from './watchparty-http-server';
import { WatchPartySyncServer } from './watchparty-sync-server';
import { WatchPartyTunnel } from './watchparty-tunnel';
import { watchPartyLogger } from './watchparty-logger';
import { traktScrobbler } from './trakt-scrobbler';
import { getItem as dbGetItem, getGroupVersions as dbGetGroupVersions, updateItemUserData } from './database';
import {
  selectWatchPartySource,
  type WatchPartySource,
} from '../shared/watchparty-types';

export type SessionState = 'IDLE' | 'INITIALIZING' | 'WAITING' | 'LIVE' | 'ENDED';

export interface StartSessionOptions {
  source: WatchPartySource;
  /** Source-side video duration in seconds. 0 if unknown. */
  durationSec: number;
  /** UI cap; enforced server-side to drop overflow connections. */
  maxGuests: number | 'unlimited';
  /**
   * Output ceiling. Actual output = min(source height, ceiling) — the
   * scale filter never upscales. 2160 requires the 4K-output Danger Zone
   * toggle (enforced by the IPC handler before this is constructed).
   */
  qualityHeight: 720 | 1080 | 2160;
  /** Movie-time offset where the transcode begins (Resume). 0 = beginning. */
  startOffsetSec: number;
  /** When true, report progress to Emby + scrobble to Trakt during LIVE. */
  trackHistory: boolean;
}

export interface PublicSessionState {
  state: SessionState;
  sessionId: string | null;
  title: string | null;
  /** Cloudflared public URL — what the host shares with friends. */
  tunnelUrl: string | null;
  /** Localhost URL for the host's embedded hls.js video. */
  localUrl: string | null;
  durationSec: number | null;
  transcodedSeconds: number;
  canStart: boolean;
  guestCount: number;
  maxGuests: number | 'unlimited' | null;
  errorMessage: string | null;
  startedAt: number | null;
  /** Movie-time offset of the transcode's t=0. Read by the host progress bar. */
  startOffsetSec: number;
  /** Whether this session is reporting to Emby + Trakt. */
  trackHistory: boolean;
  /**
   * True when a Resume transcode (-ss on the source URL) produced no
   * progress within the watchdog window — the Emby server is probably
   * refusing HTTP range requests and ffmpeg is scanning from the file
   * start, which can take minutes. The waiting room surfaces this.
   */
  slowSeekWarning: boolean;
}

// Minimum transcoded head-start before "Start the Show" enables. Spec §3:
// protects the CPU-host case where transcode is slow.
const MIN_START_BUFFER_SEC = 60;

// Host time-update events throttle to one heartbeat broadcast per N ms. The
// host renderer already throttles its timeupdate→IPC pumps to ~1s, this is
// a defence-in-depth ceiling on the wire rate.
const HEARTBEAT_MIN_INTERVAL_MS = 1000;

// Emby progress report cadence. Matches solo (mpv) playback, which polls
// + reports every 10s — keeps the server's "currently watching" state
// fresh and bounds how much progress is lost on a crash.
const EMBY_PROGRESS_INTERVAL_MS = 10_000;

class WatchPartySessionManager extends EventEmitter {
  private state: SessionState = 'IDLE';
  private sessionId: string | null = null;
  private sessionDir: string | null = null;
  private title: string | null = null;
  private durationSec: number | null = null;
  private tunnelUrl: string | null = null;
  private localUrl: string | null = null;
  private maxGuests: number | 'unlimited' | null = null;
  private errorMessage: string | null = null;
  private startedAt: number | null = null;
  private startOffsetSec = 0;
  private trackHistory = true;

  private transcoder: WatchPartyTranscoder | null = null;
  private httpServer: HttpServerHandle | null = null;
  private syncServer: WatchPartySyncServer | null = null;
  private tunnel: WatchPartyTunnel | null = null;

  private transcodedSeconds = 0;
  private guestCount = 0;
  private hostPosition = 0;
  private hostPlaying = false;
  private lastHeartbeatAt = 0;
  private slowSeekWarning = false;
  private slowSeekTimer: ReturnType<typeof setTimeout> | null = null;
  // In-flight endSession. Lets a second caller (e.g. before-quit racing the
  // host's End click) await the same teardown instead of returning early
  // because the state already left LIVE.
  private endPromise: Promise<void> | null = null;

  // History-reporting target (the picked version's server). null when
  // trackHistory is off or we never started a show.
  private historyTarget: {
    serverId: string;
    serverUrl: string;
    accessToken: string;
    itemId: string;
    mediaSourceId: string;
    playSessionId: string;
  } | null = null;
  private progressTimer: ReturnType<typeof setInterval> | null = null;
  // Track the last pause/play edge we sent to Trakt so we don't spam
  // scrobble:pause/start on rapid play/pause toggles. Matches the solo
  // path's edge-detect inside PlaybackSession.start.
  private lastScrobbleAction: 'start' | 'pause' | null = null;
  // True once startHistoryReporting has fired (i.e. the show actually
  // started). Ending a session from the WAITING room must NOT send a
  // playback-stopped report or a Trakt scrobble:stop — a Resume offset
  // >= 80% would otherwise credit the whole watch without a second played.
  private historyStarted = false;

  /** Public state snapshot for the renderer / IPC. */
  getPublicState(): PublicSessionState {
    return {
      state: this.state,
      sessionId: this.sessionId,
      title: this.title,
      tunnelUrl: this.tunnelUrl,
      localUrl: this.localUrl,
      durationSec: this.durationSec,
      transcodedSeconds: this.transcodedSeconds,
      canStart:
        this.state === 'WAITING' && this.transcodedSeconds >= MIN_START_BUFFER_SEC,
      guestCount: this.guestCount,
      maxGuests: this.maxGuests,
      errorMessage: this.errorMessage,
      startedAt: this.startedAt,
      startOffsetSec: this.startOffsetSec,
      trackHistory: this.trackHistory,
      slowSeekWarning: this.slowSeekWarning,
    };
  }

  isActive(): boolean {
    return this.state !== 'IDLE' && this.state !== 'ENDED';
  }

  // ── Lifecycle ─────────────────────────────────────────────

  async startSession(opts: StartSessionOptions): Promise<void> {
    if (this.isActive()) throw new Error('Watch Party session already active');
    if (!watchPartyBinaryManager.isReady()) {
      throw new Error('Watch Party binaries not ready');
    }

    this.errorMessage = null;
    this.transcodedSeconds = 0;
    this.guestCount = 0;
    this.hostPosition = 0;
    this.hostPlaying = false;
    this.lastHeartbeatAt = 0;
    this.slowSeekWarning = false;
    this.title = opts.source.title;
    this.durationSec = opts.durationSec > 0 ? opts.durationSec : null;
    this.maxGuests = opts.maxGuests;
    this.startedAt = Date.now();
    this.startOffsetSec = opts.startOffsetSec > 0 ? opts.startOffsetSec : 0;
    this.trackHistory = opts.trackHistory;
    this.historyTarget = null;
    this.lastScrobbleAction = null;
    this.historyStarted = false;
    if (this.progressTimer) {
      clearInterval(this.progressTimer);
      this.progressTimer = null;
    }

    // Mint the id up-front so the log filename can include it and the
    // sweep call below can preserve our dir.
    const id = randomUUID();
    this.sessionId = id;

    // Open the log file first — pre-INITIALIZING failures (probe, server
    // lookup) need to be recorded too. startSessionLog truncates.
    watchPartyLogger.startSessionLog(id);
    watchPartyLogger.info(
      'session',
      `startSession id=${id} title=${JSON.stringify(opts.source.title)} duration=${opts.durationSec}s maxGuests=${opts.maxGuests} qualityHeight=${opts.qualityHeight} startOffsetSec=${opts.startOffsetSec} trackHistory=${opts.trackHistory}`,
    );

    this.setState('INITIALIZING');

    try {
      // Probe (cached if already done) so we know which encoder args to use.
      const probe = await watchPartyEncoderProbe.probe();
      watchPartyLogger.info('session', `Encoder probe: preferred=${probe.preferred} available=[${probe.available.join(',')}]`);

      // CPU-only block. The pre-flight modal already shows the blocked
      // state, but the gate lives here too — main never trusts the
      // renderer on a safety check.
      if (probe.preferred === 'libx264' && !getSettingValue('watchPartyAllowCpuEncoder')) {
        throw new Error(
          'Watch Party requires a hardware encoder (NVIDIA, Intel, or AMD). ' +
            'Software encoding cannot reliably keep pace with playback. ' +
            'To run a session anyway, enable "Watch Party on CPU-only systems" in Settings → Danger Zone.',
        );
      }

      // Pick the source version. Default prefers 1080p (lightest correct
      // path); the 4K-source Danger Zone toggle flips the preference, and a
      // 2160p output ceiling implies it (a 4K output needs the 4K master).
      const prefer4kSource =
        getSettingValue('watchPartyPrefer4kSource') || opts.qualityHeight === 2160;
      const pick = selectWatchPartySource(opts.source.versions, { prefer4kSource });
      const server = serverManager.getServer(pick.serverId);
      if (!server) throw new Error(`No server config for ${pick.serverId}`);
      const sourceUrl = embyClient.getStreamUrlForServer(
        server.url,
        server.accessToken,
        pick.itemId,
        pick.mediaSourceId,
      );
      watchPartyLogger.info(
        'session',
        `Source picked: server=${server.name} itemId=${pick.itemId} quality=${pick.qualityLabel} widthPx=${pick.widthPx} sourceUrl=${watchPartyLogger.redactUrl(sourceUrl)}`,
      );

      // Capture the history-reporting target so startShow / endSession can
      // hit the picked version's server (NOT necessarily the active one).
      if (this.trackHistory) {
        this.historyTarget = {
          serverId: server.id,
          serverUrl: server.url,
          accessToken: server.accessToken,
          itemId: pick.itemId,
          mediaSourceId: pick.mediaSourceId,
          playSessionId: `nocturne-wp-${Date.now()}`,
        };
      }

      // Session dir + stale sweep. Sweep BEFORE creating the new dir, with
      // the new id preserved so we don't nuke our own freshly-created tree.
      const baseDir = watchPartyBaseDir();
      sweepStaleSessions(baseDir, id);
      const dir = path.join(baseDir, `session-${id}`);
      fs.mkdirSync(dir, { recursive: true });
      this.sessionDir = dir;
      watchPartyLogger.info('session', `Session dir: ${dir}`);

      // Sync server first — the HTTP server needs it during upgrade routing.
      const syncServer = new WatchPartySyncServer();
      this.syncServer = syncServer;
      syncServer.onConnection(({ client, req }) => {
        watchPartyLogger.info(
          'sync',
          `Guest connected id=${client.id} from=${req.socket.remoteAddress ?? '-'} ua=${(req.headers['user-agent'] || '').slice(0, 80)}`,
        );
        // Hand the new guest current state immediately. Late-joiners during
        // LIVE drop in at host's current position.
        syncServer.sendTo(client, {
          type: 'session_info',
          title: this.title ?? 'Watch Party',
          durationSec: this.durationSec,
          sessionStartedAt: this.startedAt ?? Date.now(),
          startOffsetSec: this.startOffsetSec,
        });
        syncServer.sendTo(client, this.buildStateMessage());
      });
      syncServer.onDisconnection(({ client }) => {
        watchPartyLogger.info('sync', `Guest disconnected id=${client.id}`);
      });
      syncServer.onMessage(({ client, msg }) => {
        // Guest-side diagnostics → session.log. The guest browser's console
        // is invisible in packaged builds; this is the only window into a
        // guest that can't play. Wire payload is untrusted: clamp the
        // details string, ignore everything else.
        if (msg.type !== 'client_error') return;
        const guest = client.id.slice(0, 5);
        const details = typeof msg.details === 'string' ? msg.details.slice(0, 200) : '';
        if (msg.kind === 'hls_fatal') {
          watchPartyLogger.warn('sync', `guest ${guest} hls fatal: ${details}`);
        } else if (msg.kind === 'stall') {
          watchPartyLogger.warn('sync', `guest ${guest} stalled ${details}`);
        }
      });
      syncServer.onCountChanged((n) => {
        watchPartyLogger.info('sync', `Guest count=${n}`);
        // Server-side cap enforcement. If the host set "5 guests" we close
        // the 6th's socket cleanly. The spec defers reconnection/backoff —
        // overflow guests just see "Session ended."
        if (this.maxGuests !== 'unlimited' && typeof this.maxGuests === 'number' && n > this.maxGuests) {
          // The most recent connection is the one to drop; the sync server
          // doesn't expose individual sockets, so broadcast nothing here
          // and rely on the next connect path to refuse. For batch 2 the
          // overflow is rare; piece-N polish builds an explicit refusal.
          watchPartyLogger.warn('sync', `Guest count ${n} exceeds cap ${this.maxGuests}`);
        }
        this.guestCount = n;
        this.emitStateChange();
      });

      // HTTP server (HLS + guest page + hls.min.js) with WS upgrade routing.
      const http = await startHttpServer({ sessionDir: dir, syncServer });
      this.httpServer = http;
      this.localUrl = http.url;
      watchPartyLogger.info('http', `Listening ${http.url} (port ${http.port})`);

      // Transcoder. 1080p ceiling default; piece 9 will wire 4K opt-in.
      const transcoder = new WatchPartyTranscoder();
      this.transcoder = transcoder;
      let lastProgressLogSec = -10;
      transcoder.onProgress(({ transcodedSeconds }) => {
        // First sign of life cancels the slow-seek watchdog.
        if (this.slowSeekTimer) {
          clearTimeout(this.slowSeekTimer);
          this.slowSeekTimer = null;
        }
        if (this.slowSeekWarning) {
          this.slowSeekWarning = false;
          this.emitStateChange();
        }
        // Only forward forward-progress to avoid spurious renderer churn.
        if (transcodedSeconds <= this.transcodedSeconds) return;
        const prevCanStart = this.transcodedSeconds >= MIN_START_BUFFER_SEC;
        this.transcodedSeconds = transcodedSeconds;
        const nextCanStart = this.transcodedSeconds >= MIN_START_BUFFER_SEC;
        // Emit on every tick is too chatty; emit on canStart crossing or
        // every 2s otherwise.
        if (prevCanStart !== nextCanStart || Math.floor(transcodedSeconds) % 2 === 0) {
          this.emitStateChange();
        }
        // Log every 10s of transcoded content + on canStart crossing.
        if (transcodedSeconds - lastProgressLogSec >= 10 || prevCanStart !== nextCanStart) {
          lastProgressLogSec = transcodedSeconds;
          watchPartyLogger.info(
            'transcoder',
            `progress=${transcodedSeconds.toFixed(1)}s canStart=${nextCanStart}`,
          );
        }
      });
      transcoder.onError(({ message }) => {
        watchPartyLogger.error('transcoder', `error: ${message}`);
        void this.endSession('error', `Transcoder error: ${message}`);
      });
      transcoder.onComplete(() => {
        // Natural ffmpeg exit (EOF). Margin already past playback; LIVE
        // continues until host clicks End or playback reaches EOF.
        watchPartyLogger.info('transcoder', 'Completed (full VOD ready)');
      });
      // Effective ceiling = min(selected ceiling, source resolution). The
      // ffmpeg scale filter already refuses to upscale; clamping here too
      // keeps the bitrate ladder honest — a 1080p source under a 2160
      // ceiling encodes at the 1080p bitrate, not 20 Mbps of upscaled air.
      // Unknown width (0, older cache rows) skips the clamp and lets the
      // scale filter decide alone.
      const sourceHeightEstimate =
        pick.widthPx >= 3840 ? 2160 : pick.widthPx >= 1920 ? 1080 : pick.widthPx > 0 ? 720 : null;
      const targetHeight = sourceHeightEstimate
        ? (Math.min(opts.qualityHeight, sourceHeightEstimate) as 720 | 1080 | 2160)
        : opts.qualityHeight;
      if (targetHeight !== opts.qualityHeight) {
        watchPartyLogger.info(
          'transcoder',
          `Output ceiling clamped ${opts.qualityHeight} → ${targetHeight} (source widthPx=${pick.widthPx})`,
        );
      }

      transcoder.start({
        sourceUrl,
        encoder: probe.preferred,
        sessionDir: dir,
        targetHeight,
        startOffsetSec: this.startOffsetSec,
      });
      // The transcoder logs its own ffmpeg argv via its onSpawn hook (added
      // below); this top-level line gives correlation with the session.
      watchPartyLogger.info(
        'transcoder',
        `spawned encoder=${probe.preferred} targetHeight=${targetHeight} startOffsetSec=${this.startOffsetSec} sessionDir=${dir}`,
      );

      // Resume watchdog. -ss before -i relies on the Emby server honouring
      // HTTP range requests; a server that refuses forces ffmpeg into a
      // sequential scan to the offset, which looks like a hung transcode.
      // 20s with zero progress on a cold start is our tell.
      if (this.startOffsetSec > 0) {
        this.slowSeekTimer = setTimeout(() => {
          this.slowSeekTimer = null;
          if (this.transcodedSeconds > 0 || !this.isActive()) return;
          this.slowSeekWarning = true;
          watchPartyLogger.warn(
            'transcoder',
            `No progress 20s after a Resume start (offset=${this.startOffsetSec}s) — the Emby server may not support HTTP range requests; ffmpeg is likely scanning from the file start. This can take minutes. Consider starting from the beginning.`,
          );
          this.emitStateChange();
        }, 20_000);
      }

      // Tunnel — parse the public URL from cloudflared stderr.
      const tunnel = new WatchPartyTunnel();
      this.tunnel = tunnel;
      tunnel.onExit(({ code, signal }) => {
        if (!this.isActive()) return;
        watchPartyLogger.warn(
          'tunnel',
          `cloudflared exited unexpectedly code=${code} signal=${signal}`,
        );
        void this.endSession('error', 'Tunnel process exited unexpectedly');
      });
      watchPartyLogger.info('tunnel', `Starting cloudflared → localhost:${http.port}`);
      const tunnelUrl = await tunnel.start({ localPort: http.port });
      this.tunnelUrl = tunnelUrl;
      watchPartyLogger.info('tunnel', `URL resolved: ${tunnelUrl}`);

      this.setState('WAITING');
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      watchPartyLogger.error('session', `startSession failed: ${message}`);
      this.errorMessage = message;
      // Make sure partial subsystems are torn down even on init failure.
      await this.teardown();
      this.setState('IDLE');
      throw err;
    }
  }

  async startShow(): Promise<void> {
    if (this.state !== 'WAITING') throw new Error(`Cannot start show from state ${this.state}`);
    if (this.transcodedSeconds < MIN_START_BUFFER_SEC) {
      throw new Error(
        `Need at least ${MIN_START_BUFFER_SEC}s of transcoded buffer (have ${this.transcodedSeconds.toFixed(1)}s)`,
      );
    }
    this.hostPlaying = true;
    this.hostPosition = 0;
    watchPartyLogger.info('session', `startShow (buffer=${this.transcodedSeconds.toFixed(1)}s guests=${this.guestCount})`);
    this.setState('LIVE');
    // Broadcast a fresh state so any waiting guests start playing.
    this.syncServer?.broadcast(this.buildStateMessage());

    // Kick off Emby + Trakt reporting if the host opted in. Mirrors the
    // solo-playback path (player:play in ipc-handlers.ts): reportStart
    // immediately, periodic reportProgress, scrobble:start once.
    if (this.trackHistory && this.historyTarget) {
      this.startHistoryReporting();
    }
  }

  async endSession(reason: 'host' | 'eof' | 'error' = 'host', errorMessage?: string): Promise<void> {
    if (this.state === 'IDLE') return;
    // Re-entrant call while a teardown is already running (End click racing
    // app quit, transcoder error racing tunnel exit, …) — await the same
    // teardown rather than skipping it or double-running it.
    if (this.endPromise) return this.endPromise;
    this.endPromise = this.doEndSession(reason, errorMessage).finally(() => {
      this.endPromise = null;
    });
    return this.endPromise;
  }

  private async doEndSession(reason: 'host' | 'eof' | 'error', errorMessage?: string): Promise<void> {
    if (errorMessage) this.errorMessage = errorMessage;
    watchPartyLogger.info('session', `endSession reason=${reason}${errorMessage ? ` errorMessage=${errorMessage}` : ''}`);
    // Tell guests first, then tear down.
    try {
      this.syncServer?.broadcast({ type: 'session_end' });
    } catch {
      /* ignore */
    }
    // Flush a final Emby progress + Trakt scrobble:stop before teardown.
    // History reporting only runs if the host opted in AND the show actually
    // started — historyStarted is set by startHistoryReporting (LIVE); a
    // session ended from the WAITING room must not report a stop.
    if (this.trackHistory && this.historyTarget && this.historyStarted) {
      await this.stopHistoryReporting();
    }
    this.setState('ENDED');
    await this.teardown();
    watchPartyLogger.endSessionLog(reason);
    this.setState('IDLE');
  }

  // ── Host-renderer events ──────────────────────────────────

  recordHostEvent(ev: { type: 'play' | 'pause' | 'seek' | 'time-update'; position: number }): void {
    if (!this.syncServer) return;
    if (this.state !== 'LIVE') return;
    if (typeof ev.position !== 'number' || !isFinite(ev.position)) return;
    this.hostPosition = ev.position;
    const serverTime = Date.now();
    switch (ev.type) {
      case 'play':
        this.hostPlaying = true;
        this.syncServer.broadcast({ type: 'play', position: ev.position, serverTime });
        this.scrobbleEdge('start');
        break;
      case 'pause':
        this.hostPlaying = false;
        this.syncServer.broadcast({ type: 'pause', position: ev.position, serverTime });
        this.scrobbleEdge('pause');
        // Snap Emby's "currently watching" to reflect the pause without
        // waiting for the next interval tick.
        void this.reportProgressOnce(true);
        break;
      case 'seek':
        this.syncServer.broadcast({ type: 'seek', position: ev.position, serverTime });
        // Snap Emby's idea of where we are to reflect the seek immediately.
        void this.reportProgressOnce(!this.hostPlaying);
        break;
      case 'time-update':
        // Throttled heartbeat — guests use it for drift correction.
        if (serverTime - this.lastHeartbeatAt < HEARTBEAT_MIN_INTERVAL_MS) return;
        this.lastHeartbeatAt = serverTime;
        this.syncServer.broadcast({
          type: 'heartbeat',
          position: ev.position,
          serverTime,
        });
        break;
    }
  }

  // ── History reporting (Emby + Trakt) ──────────────────────

  /** Movie-time seconds = transcode-input offset + host's video-local clock. */
  private movieTimeSec(): number {
    return this.startOffsetSec + this.hostPosition;
  }

  private startHistoryReporting(): void {
    const target = this.historyTarget;
    if (!target) return;
    this.historyStarted = true;
    const positionSec = this.movieTimeSec();
    const positionTicks = Math.floor(positionSec * 10_000_000);
    const durationSec = this.durationSec ?? 0;
    watchPartyLogger.info(
      'history',
      `reportPlaybackStart server=${target.serverId} itemId=${target.itemId} positionSec=${positionSec.toFixed(1)} durationSec=${durationSec.toFixed(0)}`,
    );
    embyClient
      .reportPlaybackStartToServer(target.serverUrl, target.accessToken, {
        ItemId: target.itemId,
        MediaSourceId: target.mediaSourceId,
        PlaySessionId: target.playSessionId,
        PositionTicks: positionTicks,
        CanSeek: true,
        PlayMethod: 'Transcode',
      })
      .catch((err) =>
        watchPartyLogger.warn('history', `reportPlaybackStart failed: ${err instanceof Error ? err.message : String(err)}`),
      );
    // scrobble:start. Records the edge so we don't double-fire if the
    // host's first onPlay races us here.
    void traktScrobbler.scrobble('start', target.itemId, positionSec, durationSec);
    this.lastScrobbleAction = 'start';

    // 10s progress poller. Matches solo (mpv) cadence.
    this.progressTimer = setInterval(() => {
      void this.reportProgressOnce(!this.hostPlaying);
    }, EMBY_PROGRESS_INTERVAL_MS);
  }

  private async reportProgressOnce(isPaused: boolean): Promise<void> {
    const target = this.historyTarget;
    if (!target) return;
    const positionTicks = Math.floor(this.movieTimeSec() * 10_000_000);
    try {
      await embyClient.reportPlaybackProgressToServer(target.serverUrl, target.accessToken, {
        ItemId: target.itemId,
        MediaSourceId: target.mediaSourceId,
        PlaySessionId: target.playSessionId,
        PositionTicks: positionTicks,
        IsPaused: isPaused,
        CanSeek: true,
        PlayMethod: 'Transcode',
      });
      // Mirror the solo path — keep the local cache's position current so
      // Continue Watching reflects the party without waiting for a sync.
      updateItemUserData(target.itemId, {
        playback_position_ticks: positionTicks,
        last_played_date: new Date().toISOString(),
      });
    } catch (err) {
      watchPartyLogger.warn('history', `reportProgress failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  /** Fire scrobble only on edges (start ↔ pause) to avoid duplicate events. */
  private scrobbleEdge(action: 'start' | 'pause'): void {
    if (!this.trackHistory || !this.historyTarget) return;
    if (this.lastScrobbleAction === action) return;
    this.lastScrobbleAction = action;
    void traktScrobbler.scrobble(
      action,
      this.historyTarget.itemId,
      this.movieTimeSec(),
      this.durationSec ?? 0,
    );
  }

  private async stopHistoryReporting(): Promise<void> {
    const target = this.historyTarget;
    if (!target) return;
    if (this.progressTimer) {
      clearInterval(this.progressTimer);
      this.progressTimer = null;
    }
    const positionSec = this.movieTimeSec();
    const positionTicks = Math.floor(positionSec * 10_000_000);
    const durationSec = this.durationSec ?? 0;
    watchPartyLogger.info(
      'history',
      `reportPlaybackStopped server=${target.serverId} itemId=${target.itemId} positionSec=${positionSec.toFixed(1)} durationSec=${durationSec.toFixed(0)}`,
    );
    try {
      await embyClient.reportPlaybackStoppedToServer(target.serverUrl, target.accessToken, {
        ItemId: target.itemId,
        MediaSourceId: target.mediaSourceId,
        PlaySessionId: target.playSessionId,
        PositionTicks: positionTicks,
      });
    } catch (err) {
      watchPartyLogger.warn('history', `reportPlaybackStopped failed: ${err instanceof Error ? err.message : String(err)}`);
    }
    // Trakt scrobble:stop — handles the >= 80% "watched" credit on Trakt's
    // side. Below 80% it's discarded silently.
    void traktScrobbler.scrobble('stop', target.itemId, positionSec, durationSec);
    // Mirror the final position into the local cache, applying the same
    // thresholds Emby uses server-side (>= 90% = played, position cleared).
    // Guarded: on the app-quit teardown path the DB may already be closed —
    // a throw here must not abort the process teardown that follows.
    const watchedToEnd = durationSec > 0 && positionSec / durationSec >= 0.9;
    try {
      if (watchedToEnd) {
        updateItemUserData(target.itemId, { played: 1, playback_position_ticks: 0, played_percentage: 0, last_played_date: new Date().toISOString() });
      } else {
        updateItemUserData(target.itemId, { playback_position_ticks: positionTicks, last_played_date: new Date().toISOString() });
      }
    } catch (err) {
      watchPartyLogger.warn('history', `cache stop-update failed: ${err instanceof Error ? err.message : String(err)}`);
    }

    // Cross-server cascade in combined mode — same shape as the solo
    // end-file handler. Marks the dedup-sibling versions played on their
    // own servers so the UserData reflects "I watched this" across the
    // fleet. Only when the party actually finished the item — cascading on
    // every End click marked siblings watched after a two-minute session.
    if (watchedToEnd && serverManager.isCombinedMode()) {
      try {
        const cached = dbGetItem(target.itemId);
        if (cached?.dedup_group_id) {
          const versions = dbGetGroupVersions(cached.dedup_group_id, cached);
          for (const version of versions) {
            if (version.server_id !== cached.server_id) {
              const otherServer = serverManager.getServer(version.server_id);
              if (otherServer) {
                embyClient
                  .markPlayedOnServer(
                    otherServer.url,
                    otherServer.accessToken,
                    otherServer.userId,
                    version.emby_id,
                  )
                  .catch((err) =>
                    watchPartyLogger.warn('history', `cross-server markPlayed failed for ${version.server_id}: ${err instanceof Error ? err.message : String(err)}`),
                  );
                updateItemUserData(version.emby_id, {
                  played: 1, playback_position_ticks: 0, played_percentage: 0,
                  last_played_date: new Date().toISOString(),
                });
              }
            }
          }
        }
      } catch (err) {
        watchPartyLogger.warn('history', `cross-server cascade error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }

    this.historyTarget = null;
    this.lastScrobbleAction = null;
    this.historyStarted = false;
  }

  // ── Internals ─────────────────────────────────────────────

  private buildStateMessage(): {
    type: 'state';
    state: 'WAITING' | 'LIVE' | 'ENDED';
    position: number;
    playing: boolean;
    serverTime: number;
  } {
    const wireState =
      this.state === 'LIVE' ? 'LIVE' : this.state === 'ENDED' ? 'ENDED' : 'WAITING';
    return {
      type: 'state',
      state: wireState,
      position: this.hostPosition,
      playing: this.hostPlaying,
      serverTime: Date.now(),
    };
  }

  private setState(next: SessionState): void {
    if (this.state === next) {
      this.emitStateChange();
      return;
    }
    const prev = this.state;
    this.state = next;
    watchPartyLogger.info('session', `state ${prev} → ${next}`);
    this.emitStateChange();
  }

  private emitStateChange(): void {
    this.emit('state', this.getPublicState());
  }

  /** Idempotent best-effort cleanup. Order: tunnel → transcoder → http → fs. */
  private async teardown(): Promise<void> {
    const sessionDir = this.sessionDir;
    const tunnel = this.tunnel;
    const transcoder = this.transcoder;
    const syncServer = this.syncServer;
    const httpServer = this.httpServer;

    // If the progress poller is still armed (init failure mid-LIVE or a
    // forced error path that skipped stopHistoryReporting), kill it now.
    if (this.progressTimer) {
      clearInterval(this.progressTimer);
      this.progressTimer = null;
    }
    if (this.slowSeekTimer) {
      clearTimeout(this.slowSeekTimer);
      this.slowSeekTimer = null;
    }

    this.tunnel = null;
    this.transcoder = null;
    this.syncServer = null;
    this.httpServer = null;
    this.sessionDir = null;
    this.tunnelUrl = null;
    this.localUrl = null;

    if (tunnel) {
      try {
        await tunnel.stop();
        watchPartyLogger.info('teardown', 'tunnel stopped');
      } catch (err) {
        watchPartyLogger.warn('teardown', `tunnel.stop error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    if (transcoder) {
      try {
        await transcoder.stop();
        watchPartyLogger.info('teardown', 'transcoder stopped');
      } catch (err) {
        watchPartyLogger.warn('teardown', `transcoder.stop error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    if (syncServer) {
      try {
        await syncServer.close();
        watchPartyLogger.info('teardown', 'syncServer closed');
      } catch (err) {
        watchPartyLogger.warn('teardown', `syncServer.close error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    if (httpServer) {
      try {
        await httpServer.stop();
        watchPartyLogger.info('teardown', 'httpServer stopped');
      } catch (err) {
        watchPartyLogger.warn('teardown', `httpServer.stop error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    if (sessionDir) {
      try {
        fs.rmSync(sessionDir, { recursive: true, force: true });
        watchPartyLogger.info('teardown', `session dir deleted: ${sessionDir}`);
      } catch (err) {
        watchPartyLogger.warn('teardown', `session dir cleanup error: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  }
}

function watchPartyBaseDir(): string {
  return path.join(app.getPath('userData'), 'watch-party');
}

function sweepStaleSessions(baseDir: string, keepId: string | null): void {
  try {
    if (!fs.existsSync(baseDir)) return;
    for (const entry of fs.readdirSync(baseDir, { withFileTypes: true })) {
      if (!entry.isDirectory() || !entry.name.startsWith('session-')) continue;
      if (keepId && entry.name === `session-${keepId}`) continue;
      const full = path.join(baseDir, entry.name);
      try {
        fs.rmSync(full, { recursive: true, force: true });
      } catch (err) {
        // Through the logger, not console — a partially-swept husk dir was
        // invisible in packaged builds (GUI subsystem swallows stdout).
        watchPartyLogger.warn(
          'session',
          `failed to sweep stale dir ${full}: ${err instanceof Error ? err.message : String(err)}`,
        );
      }
    }
  } catch (err) {
    watchPartyLogger.warn(
      'session',
      `stale-session sweep walk failed: ${err instanceof Error ? err.message : String(err)}`,
    );
  }
}

export const watchPartySessionManager = new WatchPartySessionManager();
