import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useBlocker, useNavigate } from 'react-router-dom';
import Hls from 'hls.js';
import {
  ArrowLeft, Check, Copy, Home, Loader2, Maximize2, Minimize2, Pause, Play,
  Popcorn, RotateCcw, Square, Users, Volume2, VolumeX,
} from 'lucide-react';
import { useToastStore } from '../stores/toast-store';
import { useUiStore } from '../stores/ui-store';
import ConfirmDialog from '../components/ui/ConfirmDialog';
import type { WatchPartyPublicState } from '../api/types';
import styles from './WatchPartyHostPage.module.css';

// Time-update events fire continuously; we throttle the IPC round-trip so
// the heartbeat stream doesn't drown the main process. The session manager
// also enforces its own ceiling — this is the renderer-side floor.
const TIME_UPDATE_THROTTLE_MS = 1000;

// Minimum buffer the WAITING screen shows as the goal. Mirrors the
// session-manager constant; if they drift the user just sees a slightly
// off progress bar (no functional break).
const MIN_START_BUFFER_SEC = 60;

// Auto-hide window for the cinema overlays. Resets on every mousemove.
const CONTROLS_AUTOHIDE_MS = 3000;

function formatTime(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) return '0:00';
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

export default function WatchPartyHostPage() {
  const navigate = useNavigate();
  const addToast = useToastStore((s) => s.addToast);
  const setCinemaMode = useUiStore((s) => s.setCinemaMode);
  const [state, setState] = useState<WatchPartyPublicState | null>(null);
  const [copied, setCopied] = useState(false);
  const [isPaused, setIsPaused] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  // Local UI states for the End Session dance. Main keeps the session
  // alive until handleRealEnd actually fires — so Resume just drops the
  // overlay; nothing in main has to be told.
  const [confirmingEnd, setConfirmingEnd] = useState(false);
  const [endingGrace, setEndingGrace] = useState(false);
  const [graceCountdown, setGraceCountdown] = useState(10);
  // Cinema overlay state: visible on idle-reset, hides after CONTROLS_AUTOHIDE_MS.
  const [controlsVisible, setControlsVisible] = useState(true);
  const [hostVolume, setHostVolume] = useState(100);
  const [hostMuted, setHostMuted] = useState(false);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const realEndInFlightRef = useRef(false);
  // Route-leave guard plumbing. Refs (not state) because useBlocker's
  // shouldBlock runs at navigation time and must see the *current* truth,
  // not the value captured at the last render — the post-End navigate()
  // fires in the same tick as the state flip and would otherwise be
  // blocked by its own stale snapshot.
  const sessionActiveRef = useRef(false);
  const allowLeaveRef = useRef(false);
  const blockerActiveRef = useRef(false);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const hlsRef = useRef<Hls | null>(null);
  const lastTimeUpdateAtRef = useRef(0);
  const hideTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const cinemaContainerRef = useRef<HTMLDivElement | null>(null);
  // Suppress the synthetic "play" event mpv-style — when we transition into
  // LIVE the renderer triggers video.play() programmatically; the resulting
  // 'play' DOM event would re-broadcast play on top of the state-change
  // broadcast already done by main. The first play after attach is silent.
  const suppressFirstPlayRef = useRef(true);

  const isLive = state?.state === 'LIVE';

  const isActiveState = (s: WatchPartyPublicState['state']) =>
    s === 'INITIALIZING' || s === 'WAITING' || s === 'LIVE';

  // Subscribe to session state updates.
  useEffect(() => {
    let cancelled = false;
    void window.api.watchparty.getState().then((res) => {
      if (cancelled) return;
      if (res.success && res.data) {
        sessionActiveRef.current = isActiveState(res.data.state);
        if (res.data.state === 'IDLE') {
          // No active session — user reached this page directly. Bounce.
          navigate('/');
          return;
        }
        setState(res.data);
      }
    });
    const unsub = window.api.watchparty.onState((s) => {
      const next = s as WatchPartyPublicState;
      sessionActiveRef.current = isActiveState(next.state);
      setState(next);
      if (next.state === 'IDLE' && !blockerActiveRef.current) {
        // Don't navigate over a blocked navigation — the leave-guard's
        // proceed() is about to take the user where they asked to go.
        navigate('/');
      }
    });
    return () => {
      cancelled = true;
      unsub();
    };
  }, [navigate]);

  // ── Route-leave guard ─────────────────────────────────────
  // Anything that isn't End Session (back button, sidebar click, a stray
  // navigate) during an active session stops here. The session itself
  // lives in main, so navigating away wouldn't kill it — it would strand
  // it with no UI attached. Confirm-or-end instead.
  const blocker = useBlocker(
    useCallback(
      () => sessionActiveRef.current && !allowLeaveRef.current,
      [],
    ),
  );
  blockerActiveRef.current = blocker.state === 'blocked';

  // "End for everyone & leave" from the leave-guard dialog: tear the
  // session down, then let the original navigation through.
  const handleLeaveAndEnd = useCallback(async () => {
    if (realEndInFlightRef.current) return;
    realEndInFlightRef.current = true;
    const res = await window.api.watchparty.endSession();
    realEndInFlightRef.current = false;
    if (!res.success) {
      addToast(`End failed: ${res.error ?? 'unknown'}`, 'error');
      if (blocker.state === 'blocked') blocker.reset();
      return;
    }
    addToast('Session ended', 'info');
    allowLeaveRef.current = true;
    if (blocker.state === 'blocked') blocker.proceed();
  }, [blocker, addToast]);

  // Wire up hls.js once we know the local URL and transition to LIVE.
  useEffect(() => {
    if (!state || state.state !== 'LIVE' || !state.localUrl) return;
    const video = videoRef.current;
    if (!video) return;
    if (hlsRef.current) return; // already attached

    const streamUrl = `${state.localUrl}/stream.m3u8`;
    if (Hls.isSupported()) {
      // startPosition: 0 — the playlist is a growing EVENT playlist and
      // hls.js treats those like a live stream, parking the start point at
      // the live edge (measured: host began at ~1:02 instead of 0:00).
      // The show always starts at the top of the transcoded timeline.
      const hls = new Hls({ enableWorker: true, lowLatencyMode: false, startPosition: 0 });
      hlsRef.current = hls;
      hls.loadSource(streamUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        suppressFirstPlayRef.current = true;
        video.play().catch(() => {
          // Renderer is in a focused Electron window with user-gesture
          // permission already granted by the click that brought us here;
          // a failure here is unusual. Surface via toast.
          addToast('Could not start playback automatically — press Play.', 'error');
        });
      });
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (data?.fatal) {
          console.error('[watchparty-host] HLS fatal:', data.type, data.details);
          addToast(`Playback error: ${data.details}`, 'error');
        }
      });
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.src = streamUrl;
      suppressFirstPlayRef.current = true;
      video.play().catch(() => { /* ignore */ });
    } else {
      addToast('This build does not support HLS playback', 'error');
    }

    return () => {
      if (hlsRef.current) {
        try { hlsRef.current.destroy(); } catch { /* ignore */ }
        hlsRef.current = null;
      }
    };
  }, [state, addToast]);

  // Forward host player events to main → guests.
  const handlePlay = useCallback(() => {
    if (suppressFirstPlayRef.current) {
      suppressFirstPlayRef.current = false;
      return;
    }
    const v = videoRef.current;
    if (!v) return;
    setIsPaused(false);
    void window.api.watchparty.hostEvent({ type: 'play', position: v.currentTime });
  }, []);

  const handlePause = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    // Pausing at EOF fires the same 'pause' event; let it through — guests
    // will be informed that the host stopped, and the EOF affordance lives
    // on the host's own End button.
    setIsPaused(true);
    void window.api.watchparty.hostEvent({ type: 'pause', position: v.currentTime });
  }, []);

  const handleSeeked = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    void window.api.watchparty.hostEvent({ type: 'seek', position: v.currentTime });
  }, []);

  const handleTimeUpdate = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    setCurrentTime(v.currentTime);
    const now = Date.now();
    if (now - lastTimeUpdateAtRef.current < TIME_UPDATE_THROTTLE_MS) return;
    lastTimeUpdateAtRef.current = now;
    void window.api.watchparty.hostEvent({ type: 'time-update', position: v.currentTime });
  }, []);

  const handleLoadedMetadata = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    setDuration(v.duration);
  }, []);

  // Action handlers
  const handleStartShow = useCallback(async () => {
    const res = await window.api.watchparty.startShow();
    if (!res.success) {
      addToast(`Start failed: ${res.error ?? 'unknown'}`, 'error');
    }
  }, [addToast]);

  // End Session opens the confirm dialog. The actual teardown is gated
  // behind the grace screen — main keeps the session running through both.
  const handleEndSessionRequest = useCallback(() => {
    setConfirmingEnd(true);
  }, []);

  const handleConfirmEnd = useCallback(() => {
    setConfirmingEnd(false);
    setGraceCountdown(10);
    setEndingGrace(true);
  }, []);

  const handleCancelEnd = useCallback(() => {
    setConfirmingEnd(false);
  }, []);

  // Resume — drop the grace overlay. The session in main never moved off
  // LIVE, the tunnel is up, the transcoder is running, guests are still
  // connected. Nothing to broadcast or rebuild.
  const handleResume = useCallback(() => {
    setEndingGrace(false);
    setGraceCountdown(10);
  }, []);

  // Real teardown — Go to Home or countdown=0. Idempotent via the ref so
  // a Go-to-Home click that races the countdown doesn't double-fire.
  const handleRealEnd = useCallback(async () => {
    if (realEndInFlightRef.current) return;
    realEndInFlightRef.current = true;
    setEndingGrace(false);
    const res = await window.api.watchparty.endSession();
    if (!res.success) {
      addToast(`End failed: ${res.error ?? 'unknown'}`, 'error');
    } else {
      addToast('Session ended', 'info');
    }
    realEndInFlightRef.current = false;
    // Main flips state to IDLE; the onState listener in this component
    // catches that and navigates to /. Belt-and-suspenders: jump directly
    // so the user doesn't see a flash of the page if main is slow. The
    // session is down (or as down as it gets), so wave the leave-guard off.
    allowLeaveRef.current = true;
    navigate('/');
  }, [addToast, navigate]);

  // Countdown tick. Tied to endingGrace so Resume cleanly cancels by
  // unmounting the effect.
  useEffect(() => {
    if (!endingGrace) return;
    if (graceCountdown <= 0) {
      void handleRealEnd();
      return;
    }
    const t = setTimeout(() => setGraceCountdown((c) => c - 1), 1000);
    return () => clearTimeout(t);
  }, [endingGrace, graceCountdown, handleRealEnd]);

  const handleTogglePause = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    if (v.paused) {
      void v.play();
    } else {
      v.pause();
    }
  }, []);

  // ── Cinema mode + overlay autohide + fullscreen ──────────────

  // Toggle AppShell chrome off while LIVE. Cleared on unmount or state
  // change away from LIVE (ENDED / IDLE / back to WAITING all show chrome).
  useEffect(() => {
    setCinemaMode(isLive);
    return () => setCinemaMode(false);
  }, [isLive, setCinemaMode]);

  // Track browser-level fullscreen so the toggle button can flip its icon.
  useEffect(() => {
    const handler = () => setIsFullscreen(Boolean(document.fullscreenElement));
    document.addEventListener('fullscreenchange', handler);
    return () => document.removeEventListener('fullscreenchange', handler);
  }, []);

  // Reveal the overlay bars and arm the autohide timer. The timer is
  // re-armed on every mousemove via onMouseMove on the cinema container.
  const revealControls = useCallback(() => {
    setControlsVisible(true);
    if (hideTimerRef.current) clearTimeout(hideTimerRef.current);
    hideTimerRef.current = setTimeout(
      () => setControlsVisible(false),
      CONTROLS_AUTOHIDE_MS,
    );
  }, []);

  // Always show controls during WAITING/ENDED. In LIVE, start visible then
  // autohide; clear the timer on cleanup.
  useEffect(() => {
    if (!isLive) {
      setControlsVisible(true);
      if (hideTimerRef.current) {
        clearTimeout(hideTimerRef.current);
        hideTimerRef.current = null;
      }
      return;
    }
    revealControls();
    return () => {
      if (hideTimerRef.current) {
        clearTimeout(hideTimerRef.current);
        hideTimerRef.current = null;
      }
    };
  }, [isLive, revealControls]);

  // Always show controls while the grace overlay or confirm dialog is up
  // — those interactions belong with visible chrome.
  useEffect(() => {
    if (endingGrace || confirmingEnd) {
      setControlsVisible(true);
      if (hideTimerRef.current) {
        clearTimeout(hideTimerRef.current);
        hideTimerRef.current = null;
      }
    }
  }, [endingGrace, confirmingEnd]);

  // The bar's value is in MOVIE time. Clamp to [startOffset, bufferedEnd]
  // — startOffset is the lower bound because the transcoder skipped the
  // earlier range; bufferedEnd is the upper bound because anything past
  // it hasn't been transcoded yet. Map movie time back to video-local time
  // for the actual seek (output timeline starts at 0).
  const handleSeekChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = videoRef.current;
    if (!v) return;
    const movieT = Number(e.target.value);
    const startOffset = state?.startOffsetSec ?? 0;
    const bufferedEnd = startOffset + (v.duration || 0);
    const clamped = Math.max(startOffset, Math.min(movieT, bufferedEnd));
    v.currentTime = clamped - startOffset;
    // The video element's onSeeked handler broadcasts to guests.
  }, [state?.startOffsetSec]);

  const handleVolumeChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const n = Number(e.target.value);
    setHostVolume(n);
    setHostMuted(n === 0);
    const v = videoRef.current;
    if (v) {
      v.volume = Math.max(0, Math.min(1, n / 100));
      v.muted = n === 0;
    }
  }, []);

  const handleToggleMute = useCallback(() => {
    const v = videoRef.current;
    if (!v) return;
    const next = !v.muted;
    v.muted = next;
    setHostMuted(next);
  }, []);

  const handleToggleFullscreen = useCallback(async () => {
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await cinemaContainerRef.current?.requestFullscreen();
      }
    } catch (err) {
      console.warn('[watchparty-host] fullscreen toggle failed:', err);
    }
  }, []);

  // Cinema keyboard shortcuts — Space/K play-pause, ←/→ ±10s, M mute,
  // F fullscreen. LIVE only; form fields keep their own key handling.
  useEffect(() => {
    if (!isLive) return;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (
        target &&
        (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)
      ) {
        return;
      }
      const v = videoRef.current;
      if (!v) return;
      switch (e.key) {
        case ' ':
        case 'k':
          e.preventDefault();
          if (v.paused) void v.play();
          else v.pause();
          break;
        case 'ArrowLeft':
        case 'ArrowRight': {
          e.preventDefault();
          const delta = e.key === 'ArrowLeft' ? -10 : 10;
          // Video-local clamp: 0 is the transcode start (movie-time
          // startOffset) and duration is the transcoded frontier.
          const max = isFinite(v.duration) ? v.duration : v.currentTime;
          v.currentTime = Math.max(0, Math.min(v.currentTime + delta, max));
          break;
        }
        case 'm':
          v.muted = !v.muted;
          setHostMuted(v.muted);
          break;
        case 'f':
          void handleToggleFullscreen();
          break;
        default:
          return;
      }
      revealControls();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isLive, handleToggleFullscreen, revealControls]);

  const handleCopy = useCallback(async () => {
    if (!state?.tunnelUrl) return;
    try {
      await navigator.clipboard.writeText(state.tunnelUrl);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      addToast('Copy failed — copy manually from the field', 'error');
    }
  }, [state?.tunnelUrl, addToast]);

  const headerTitle = state?.title ?? 'Watch Party';
  const guestLabel = useMemo(() => {
    if (!state) return '0 guests';
    const n = state.guestCount;
    const cap = state.maxGuests;
    if (cap === 'unlimited' || cap == null) return `${n} guest${n === 1 ? '' : 's'}`;
    return `${n} / ${cap} guests`;
  }, [state]);

  const bufferPercent = useMemo(() => {
    if (!state) return 0;
    const pct = Math.min(100, (state.transcodedSeconds / MIN_START_BUFFER_SEC) * 100);
    return Math.round(pct);
  }, [state]);

  if (!state) {
    return (
      <div className={styles.page}>
        <div className={styles.loading}>
          <Loader2 className={styles.spinner} size={32} />
          <div>Loading session…</div>
        </div>
      </div>
    );
  }

  return (
    <div className={`${styles.page} ${isLive ? styles.pageCinema : ''}`}>
      {!isLive && (
        <div className={styles.header}>
          <button className={styles.backBtn} onClick={() => navigate('/')}>
            <ArrowLeft size={18} /> Home
          </button>
          <div className={styles.headerTitle}>
            <Popcorn size={18} className={styles.headerIcon} />
            {headerTitle}
          </div>
          <div className={styles.headerRight}>
            <span className={styles.guestPill} title="Connected guests">
              <Users size={14} /> {guestLabel}
            </span>
            <button className={styles.endBtn} onClick={handleEndSessionRequest}>
              <Square size={14} /> End Session
            </button>
          </div>
        </div>
      )}

      <div className={styles.body}>
        {(state.state === 'INITIALIZING' || state.state === 'WAITING') && (
          <div className={styles.waitingRoom}>
            <div className={styles.waitingTitle}>
              {state.state === 'INITIALIZING' ? 'Preparing session…' : 'Waiting Room'}
            </div>

            <div className={styles.shareCard}>
              <div className={styles.shareLabel}>Share this link with your guests</div>
              <div className={styles.shareRow}>
                <input
                  className={styles.shareInput}
                  readOnly
                  value={state.tunnelUrl ?? 'Resolving tunnel…'}
                  onFocus={(e) => e.currentTarget.select()}
                />
                <button
                  className={styles.copyBtn}
                  onClick={handleCopy}
                  disabled={!state.tunnelUrl}
                  title="Copy invite URL"
                >
                  {copied ? <Check size={14} /> : <Copy size={14} />}
                  {copied ? 'Copied' : 'Copy'}
                </button>
              </div>
            </div>

            <div className={styles.bufferCard}>
              <div className={styles.bufferHead}>
                <span>Transcode buffer</span>
                <span>
                  {state.transcodedSeconds < MIN_START_BUFFER_SEC
                    ? `${state.transcodedSeconds.toFixed(0)}s / ${MIN_START_BUFFER_SEC}s`
                    : 'Ready'}
                </span>
              </div>
              <div className={styles.bufferTrack}>
                <div className={styles.bufferFill} style={{ width: `${bufferPercent}%` }} />
              </div>
              <div className={styles.bufferCaption}>
                {state.transcodedSeconds < MIN_START_BUFFER_SEC
                  ? 'Building a head start so playback never stalls.'
                  : `Ready to start. ${state.transcodedSeconds.toFixed(0)}s buffered.`}
              </div>
              {state.slowSeekWarning && state.transcodedSeconds === 0 && (
                <div className={styles.slowSeekNote}>
                  Still preparing — your Emby server may not support fast seeking to the
                  resume point, so the transcoder is scanning the file from the start.
                  This can take several minutes. Ending the session and starting from the
                  beginning will be much faster.
                </div>
              )}
            </div>

            <div className={styles.waitingFooter}>
              <button
                className={styles.startBtn}
                onClick={handleStartShow}
                disabled={!state.canStart}
              >
                <Play size={16} fill="currentColor" /> Start the Show
              </button>
            </div>

            {state.errorMessage && (
              <div className={styles.errorBox}>{state.errorMessage}</div>
            )}
          </div>
        )}

        {state.state === 'LIVE' && (
          <div
            className={`${styles.cinema} ${!controlsVisible ? styles.cinemaIdle : ''}`}
            ref={cinemaContainerRef}
            onMouseMove={revealControls}
            onMouseLeave={() => setControlsVisible(false)}
          >
            <video
              ref={videoRef}
              className={styles.cinemaVideo}
              playsInline
              controls={false}
              onPlay={handlePlay}
              onPause={handlePause}
              onSeeked={handleSeeked}
              onTimeUpdate={handleTimeUpdate}
              onLoadedMetadata={handleLoadedMetadata}
              onClick={handleTogglePause}
            />

            <div
              className={`${styles.cinemaTopBar} ${!controlsVisible ? styles.cinemaOverlayHidden : ''}`}
            >
              <div className={styles.cinemaTitleGroup}>
                <Popcorn size={16} className={styles.cinemaTitleIcon} />
                <span className={styles.cinemaTitleText}>{headerTitle}</span>
              </div>
              <span className={styles.cinemaGuests} title="Connected guests">
                <Users size={14} /> {guestLabel}
              </span>
            </div>

            <div
              className={`${styles.cinemaBottomBar} ${!controlsVisible ? styles.cinemaOverlayHidden : ''}`}
            >
              {(() => {
                // Movie-time math. The bar shows the full movie duration
                // even though only [startOffset, startOffset + video.duration]
                // is currently seekable — the user gets a visual cue for
                // where they are in the movie, not in the transcode.
                const startOffset = state.startOffsetSec ?? 0;
                const movieDuration = (state.durationSec ?? 0) > 0
                  ? state.durationSec ?? 0
                  : (startOffset + (duration || 0));
                const moviePosition = startOffset + currentTime;
                const bufferedEnd = startOffset + (duration || 0);
                const pct = (n: number) =>
                  movieDuration > 0 ? Math.max(0, Math.min(100, (n / movieDuration) * 100)) : 0;
                const offsetPct = pct(startOffset);
                const playedPct = pct(moviePosition);
                const bufferedPct = pct(bufferedEnd);
                return (
                  <div className={styles.cinemaSeekRow}>
                    <span className={styles.cinemaTime}>{formatTime(moviePosition)}</span>
                    <div className={styles.cinemaSeekStack}>
                      {/* Base — full movie span (dimmed, "not yet transcoded") */}
                      <div className={styles.cinemaTrackBase} />
                      {/* Buffered — the transcoded extent (startOffset .. bufferedEnd) */}
                      <div
                        className={styles.cinemaTrackBuffered}
                        style={{ left: `${offsetPct}%`, width: `${Math.max(0, bufferedPct - offsetPct)}%` }}
                      />
                      {/* Played — startOffset .. moviePosition */}
                      <div
                        className={styles.cinemaTrackPlayed}
                        style={{ left: `${offsetPct}%`, width: `${Math.max(0, playedPct - offsetPct)}%` }}
                      />
                      <input
                        type="range"
                        className={styles.cinemaSeek}
                        min={0}
                        max={movieDuration}
                        step={0.1}
                        value={moviePosition}
                        onChange={handleSeekChange}
                        disabled={!movieDuration}
                      />
                    </div>
                    <span className={styles.cinemaTime}>{formatTime(movieDuration)}</span>
                  </div>
                );
              })()}

              <div className={styles.cinemaActionsRow}>
                <button
                  className={styles.cinemaPlayBtn}
                  onClick={handleTogglePause}
                  title={isPaused ? 'Play' : 'Pause'}
                >
                  {isPaused
                    ? <Play size={22} fill="currentColor" />
                    : <Pause size={22} fill="currentColor" />}
                </button>

                <div className={styles.cinemaVolumeWrap}>
                  <button
                    className={styles.cinemaIconBtn}
                    onClick={handleToggleMute}
                    title={hostMuted ? 'Unmute' : 'Mute'}
                  >
                    {hostMuted ? <VolumeX size={18} /> : <Volume2 size={18} />}
                  </button>
                  <input
                    type="range"
                    className={styles.cinemaVolume}
                    min={0}
                    max={100}
                    step={1}
                    value={hostMuted ? 0 : hostVolume}
                    onChange={handleVolumeChange}
                  />
                </div>

                <div className={styles.cinemaSpacer} />

                <button
                  className={styles.cinemaIconBtn}
                  onClick={handleToggleFullscreen}
                  title={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
                >
                  {isFullscreen ? <Minimize2 size={18} /> : <Maximize2 size={18} />}
                </button>

                <button
                  className={styles.cinemaEndBtn}
                  onClick={handleEndSessionRequest}
                  title="End the session for everyone"
                >
                  <Square size={14} /> End Session
                </button>
              </div>
            </div>
          </div>
        )}

        {state.state === 'ENDED' && (
          <div className={styles.endedCard}>
            <div className={styles.endedTitle}>Session ended</div>
            <button className={styles.startBtn} onClick={() => navigate('/')}>Back to Home</button>
          </div>
        )}

        {endingGrace && (
          <div className={styles.graceOverlay}>
            <div className={styles.graceCard}>
              <div className={styles.graceTitle}>Ending session…</div>
              <div className={styles.graceCountdown}>{graceCountdown}</div>
              <div className={styles.graceCaption}>
                The watch party will end automatically. Resume to keep going, or go home now.
              </div>
              <div className={styles.graceActions}>
                <button className={styles.resumeBtn} onClick={handleResume}>
                  <RotateCcw size={16} /> Resume
                </button>
                <button className={styles.goHomeBtn} onClick={handleRealEnd}>
                  <Home size={16} /> Go to Home
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {confirmingEnd && (
        <ConfirmDialog
          title="End Watch Party?"
          message="End the watch party for everyone? Guests will be disconnected."
          confirmLabel="End"
          danger
          onConfirm={handleConfirmEnd}
          onCancel={handleCancelEnd}
        />
      )}

      {blocker.state === 'blocked' && (
        <ConfirmDialog
          title="Leave Watch Party?"
          message="Leaving this page ends the watch party for everyone — guests will be disconnected. Stay on this page to keep the session running."
          confirmLabel="End for Everyone & Leave"
          danger
          onConfirm={handleLeaveAndEnd}
          onCancel={() => blocker.reset()}
        />
      )}
    </div>
  );
}
