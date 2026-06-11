// Static guest HTML for the watch-party tunnel origin. Self-contained
// except for a same-origin hls.min.js fetch (served by the HTTP server in
// the same module). No CDN — CSP-clean and offline-safe (well, offline at
// the guest's end at least; the tunnel still needs network).
//
// Layout: cinema-style. The <video> fills the viewport with object-fit:
// contain, on black; thin auto-hiding overlays at top + bottom; waiting
// and ended states use a full-overlay card centered on top of the black.
//
// The page is one big template literal so it can be served without an
// extra fs read. JS implements the deadzone-ladder drift corrector from
// docs/watch-party-session-architecture.md §7.

export const GUEST_PAGE_HTML = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no" />
  <title>Watch Party</title>
  <style>
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    html, body { height: 100%; margin: 0; padding: 0; }
    body {
      background: #000;
      color: #eee;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif;
      overflow: hidden;
      position: relative;
    }
    video {
      position: absolute;
      inset: 0;
      width: 100vw;
      height: 100vh;
      object-fit: contain;
      background: #000;
      display: block;
    }

    /* Top + bottom overlay bars. Auto-hide on mouse idle via the .hidden class. */
    .topBar, .bottomBar {
      position: absolute;
      left: 0;
      right: 0;
      padding: 16px 24px;
      display: flex;
      align-items: center;
      gap: 16px;
      color: #fff;
      transition: opacity 250ms ease, transform 250ms ease;
      z-index: 5;
    }
    .topBar {
      top: 0;
      background: linear-gradient(to bottom, rgba(0,0,0,0.75), rgba(0,0,0,0));
    }
    .bottomBar {
      bottom: 0;
      background: linear-gradient(to top, rgba(0,0,0,0.85), rgba(0,0,0,0));
    }
    .topBar.hidden { opacity: 0; pointer-events: none; transform: translateY(-8px); }
    .bottomBar.hidden { opacity: 0; pointer-events: none; transform: translateY(8px); }

    .title {
      flex: 1;
      font-size: 16px;
      font-weight: 600;
      color: #e5b85b;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      margin: 0;
    }

    .conn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      color: rgba(255,255,255,0.8);
    }
    .conn .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: #888;
    }
    .conn .dot.connected { background: #4ade80; }
    .conn .dot.disconnected { background: #ef4444; }

    .hostHint {
      flex: 1;
      text-align: center;
      font-size: 12px;
      color: rgba(255,255,255,0.6);
      letter-spacing: 0.02em;
    }

    .volumeWrap {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .volumeBtn {
      width: 36px;
      height: 36px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      background: transparent;
      border: 0;
      color: rgba(255,255,255,0.85);
      cursor: pointer;
      border-radius: 999px;
      transition: background 150ms ease, color 150ms ease;
    }
    .volumeBtn:hover { background: rgba(255,255,255,0.12); color: #fff; }
    .volumeBtn svg { display: block; }
    input[type="range"] {
      width: 110px;
      height: 4px;
      accent-color: #e5b85b;
      cursor: pointer;
    }

    /* Center overlay — used for waiting + ended states (no video frame yet
       or no more video). Sits above the (still-mounted) <video>. */
    .centerOverlay {
      position: absolute;
      inset: 0;
      background: rgba(0,0,0,0.92);
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 16px;
      padding: 24px;
      text-align: center;
      z-index: 10;
    }
    .centerOverlay h1 {
      font-size: 22px;
      font-weight: 600;
      color: #e5b85b;
      margin: 0;
    }
    .centerOverlay p {
      font-size: 14px;
      color: #aaa;
      margin: 0;
      max-width: 480px;
      line-height: 1.5;
    }
    .spinner {
      width: 32px;
      height: 32px;
      border: 3px solid rgba(229,184,91,0.25);
      border-top-color: #e5b85b;
      border-radius: 50%;
      animation: spin 0.9s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }

    /* Cursor hides with the controls so the picture stays clean. */
    body.idle { cursor: none; }
    .hidden { display: none !important; }
  </style>
</head>
<body>
  <video id="video" playsinline></video>

  <div class="topBar" id="topBar">
    <h1 class="title" id="title">Watch Party</h1>
    <span class="conn">
      <span class="dot disconnected" id="connDot"></span>
      <span id="connText">Connecting…</span>
    </span>
  </div>

  <div class="bottomBar" id="bottomBar">
    <div class="volumeWrap">
      <button class="volumeBtn" id="volumeBtn" title="Mute / unmute">
        <svg id="volumeIconHigh" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"></polygon><path d="M19.07 4.93a10 10 0 0 1 0 14.14"></path><path d="M15.54 8.46a5 5 0 0 1 0 7.07"></path></svg>
        <svg id="volumeIconMute" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="display:none"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"></polygon><line x1="23" y1="9" x2="17" y2="15"></line><line x1="17" y1="9" x2="23" y2="15"></line></svg>
      </button>
      <input type="range" id="volume" min="0" max="100" value="100" />
    </div>
    <div class="hostHint">Host controls playback</div>
    <span style="width: 154px"></span><!-- right-side balancer -->
  </div>

  <div class="centerOverlay" id="centerOverlay">
    <div class="spinner" id="centerSpinner"></div>
    <h1 id="centerTitle">Connecting…</h1>
    <p id="centerMsg">Reaching the host.</p>
  </div>

  <script src="/hls.min.js"></script>
  <script>
  (function () {
    var video = document.getElementById('video');
    var topBar = document.getElementById('topBar');
    var bottomBar = document.getElementById('bottomBar');
    var centerOverlay = document.getElementById('centerOverlay');
    var centerSpinner = document.getElementById('centerSpinner');
    var centerTitle = document.getElementById('centerTitle');
    var centerMsg = document.getElementById('centerMsg');
    var titleEl = document.getElementById('title');
    var connDot = document.getElementById('connDot');
    var connText = document.getElementById('connText');
    var volumeBtn = document.getElementById('volumeBtn');
    var volumeIconHigh = document.getElementById('volumeIconHigh');
    var volumeIconMute = document.getElementById('volumeIconMute');
    var volume = document.getElementById('volume');

    var hls = null;
    var ws = null;
    var sessionState = 'WAITING';
    var hostPlaying = false;
    var lastHeartbeat = null; // { position, serverTime, localTime }
    var lastKnownPosition = 0; // freshest host position from any message
    var hlsAttached = false;
    var hlsRetryTimer = null;
    var ended = false;
    var reconnectTimer = null;
    var lastStallReportAt = 0; // throttle: starving guests report once per 5s
    var lowReadySince = null;  // when readyState first dropped below 3

    // ── Guest → host diagnostics ──────────────────────────
    // The host can't see this browser's console; post hls.js fatals and
    // starvation back over the sync socket so they land in session.log.
    function reportClientError(kind, details, readyState) {
      var now = Date.now();
      if (kind === 'stall') {
        if (now - lastStallReportAt < 5000) return;
        lastStallReportAt = now;
      }
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      try {
        ws.send(JSON.stringify({
          type: 'client_error',
          kind: kind,
          details: details,
          readyState: typeof readyState === 'number' ? readyState : undefined,
        }));
      } catch (e) { /* diagnostics only — never break playback over them */ }
    }
    // Movie-time offset where the transcode starts. Resume = positive;
    // populated from session_info. Guest has no progress bar today, so
    // this is currently informational only — keep it for protocol parity
    // and future "host is at 32:17" style indicators.
    var sessionStartOffsetSec = 0;
    var sessionDurationSec = null;

    // ── Center overlay (waiting / ended states) ───────────
    function showCenter(title, msg, spinner) {
      centerTitle.textContent = title;
      centerMsg.textContent = msg;
      centerOverlay.classList.remove('hidden');
      centerSpinner.style.display = spinner ? 'block' : 'none';
    }
    function hideCenter() {
      centerOverlay.classList.add('hidden');
    }

    // ── Volume + mute ─────────────────────────────────────
    function setMuted(muted) {
      video.muted = muted;
      if (muted) {
        volumeIconHigh.style.display = 'none';
        volumeIconMute.style.display = 'block';
      } else {
        volumeIconHigh.style.display = 'block';
        volumeIconMute.style.display = 'none';
      }
    }
    volume.addEventListener('input', function () {
      var v = Math.max(0, Math.min(1, volume.valueAsNumber / 100));
      video.volume = v;
      setMuted(v === 0);
    });
    volumeBtn.addEventListener('click', function () {
      setMuted(!video.muted);
    });
    video.volume = 1;

    // ── Bars autohide on mouse idle ───────────────────────
    var hideTimer = null;
    function revealBars() {
      topBar.classList.remove('hidden');
      bottomBar.classList.remove('hidden');
      document.body.classList.remove('idle');
      if (hideTimer) clearTimeout(hideTimer);
      hideTimer = setTimeout(hideBars, 3000);
    }
    function hideBars() {
      // Don't hide while the center overlay is visible — there's no video
      // to hide for, and hiding mid-instruction is confusing.
      if (!centerOverlay.classList.contains('hidden')) return;
      topBar.classList.add('hidden');
      bottomBar.classList.add('hidden');
      document.body.classList.add('idle');
    }
    document.addEventListener('mousemove', revealBars, { passive: true });
    document.addEventListener('keydown', revealBars);
    document.addEventListener('touchstart', revealBars, { passive: true });

    // ── HLS attach ────────────────────────────────────────
    // Where the host is *right now* — heartbeat-extrapolated when playing,
    // else the last discrete position we heard. Feeds startPosition on
    // (re)attach so late joiners and retry paths land on the host, not 0.
    function currentTargetPosition() {
      if (sessionState === 'LIVE' && hostPlaying && lastHeartbeat) {
        return lastHeartbeat.position + (Date.now() - lastHeartbeat.localTime) / 1000;
      }
      return lastKnownPosition;
    }

    function attachHls(startPos) {
      if (hlsAttached || ended) return;
      hlsAttached = true;
      var pos = typeof startPos === 'number' && isFinite(startPos) && startPos > 0 ? startPos : 0;
      if (window.Hls && window.Hls.isSupported()) {
        // startPosition pins the first frame: the playlist is a growing
        // EVENT playlist and hls.js would otherwise park at the live edge
        // (the transcoded frontier) instead of where the host is.
        hls = new window.Hls({ enableWorker: true, lowLatencyMode: false, startPosition: pos });
        hls.on(window.Hls.Events.ERROR, function (_, data) {
          if (!data) return;
          // bufferStalledError is non-fatal but is exactly the "guest can't
          // keep up with the stream bitrate" signal — report it (throttled).
          if (data.details === 'bufferStalledError' && !ended) {
            reportClientError('stall', 'bufferStalledError', video.readyState);
          }
          if (!data.fatal) return;
          console.warn('[watchparty] HLS fatal:', data.type, data.details);
          if (ended) return;
          reportClientError('hls_fatal', (data.type || '?') + '/' + (data.details || '?'), video.readyState);
          if (data.type === window.Hls.ErrorTypes.MEDIA_ERROR) {
            try { hls.recoverMediaError(); } catch (e) { /* fall through to retry */ }
            return;
          }
          // Network-fatal. Most common cause: the playlist isn't on disk yet
          // (guest opened the link seconds after the host started the
          // session, before ffmpeg wrote stream.m3u8). Tear down and retry;
          // the retry re-anchors to wherever the host is by then.
          try { hls.destroy(); } catch (e) { /* already dead */ }
          hls = null;
          hlsAttached = false;
          if (hlsRetryTimer) clearTimeout(hlsRetryTimer);
          hlsRetryTimer = setTimeout(function () {
            attachHls(currentTargetPosition());
            if (sessionState === 'LIVE' && hostPlaying) playVideo();
          }, 3000);
        });
        hls.loadSource('/stream.m3u8');
        hls.attachMedia(video);
      } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        video.src = '/stream.m3u8';
        if (pos > 0) {
          video.addEventListener('loadedmetadata', function () { seekTo(pos); }, { once: true });
        }
      } else {
        showCenter('Unsupported browser', 'This browser cannot play HLS streams. Try Chrome, Firefox, Edge, or Safari.', false);
      }
    }

    function applyState(msg) {
      sessionState = msg.state;
      hostPlaying = !!msg.playing;
      if (typeof msg.position === 'number' && isFinite(msg.position)) {
        lastKnownPosition = msg.position;
      }
      if (msg.state === 'WAITING') {
        showCenter('Waiting for the host', 'The session will start shortly.', true);
        // Preload while everyone waits: attach hls.js now (video stays
        // paused — autoStartLoad buffers from t=0 without playing). The
        // transcode head-start doubles as the guests' buffer head-start,
        // so Start the Show begins in ~a second instead of paying 10-15s
        // of HLS startup through the tunnel.
        attachHls(0);
        return;
      }
      if (msg.state === 'ENDED') {
        ended = true;
        showCenter('Session ended', 'The host has ended this watch party.', false);
        return;
      }
      // LIVE — late joiners attach directly at the host's position;
      // preloaded waiters are already buffered at 0 and just seek/play.
      attachHls(typeof msg.position === 'number' ? msg.position : 0);
      hideCenter();
      revealBars();
      if (typeof msg.position === 'number' && isFinite(msg.position)) {
        seekTo(msg.position);
      }
      if (hostPlaying) {
        playVideo();
      } else {
        video.pause();
      }
    }

    function seekTo(target) {
      try {
        if (Math.abs(video.currentTime - target) > 0.05) {
          video.currentTime = target;
        }
      } catch (e) { /* ignore */ }
    }

    function playVideo() {
      var p = video.play();
      if (p && typeof p.catch === 'function') {
        p.catch(function () {
          // Most browsers block unmuted autoplay; mute and retry, then
          // surface the volume control so the user can unmute.
          setMuted(true);
          video.play().catch(function () { /* user-gesture wall, ignore */ });
        });
      }
    }

    // ── Deadzone-ladder drift corrector ───────────────────
    function applyDriftCorrection() {
      if (sessionState !== 'LIVE') return;
      if (!hostPlaying || video.paused) return;
      if (!lastHeartbeat) return;
      var elapsed = (Date.now() - lastHeartbeat.localTime) / 1000;
      var expected = lastHeartbeat.position + elapsed;
      var local = video.currentTime;
      var drift = expected - local;
      var abs = Math.abs(drift);
      if (abs < 0.5) {
        if (video.playbackRate !== 1.0) video.playbackRate = 1.0;
      } else if (abs < 3.0) {
        video.playbackRate = drift > 0 ? 1.05 : 0.95;
      } else {
        seekTo(expected);
        video.playbackRate = 1.0;
      }
    }
    setInterval(applyDriftCorrection, 500);

    // ── Starvation watchdog ───────────────────────────────
    // readyState < 3 (HAVE_FUTURE_DATA) means the element can't play
    // forward from here. Sitting there >2s while the show is live is
    // starvation — report it (reportClientError throttles to one per 5s).
    setInterval(function () {
      if (sessionState !== 'LIVE' || !hostPlaying || ended || !hlsAttached) {
        lowReadySince = null;
        return;
      }
      if (video.readyState >= 3) {
        lowReadySince = null;
        return;
      }
      var now = Date.now();
      if (lowReadySince === null) {
        lowReadySince = now;
        return;
      }
      if (now - lowReadySince >= 2000) {
        reportClientError(
          'stall',
          'readyState=' + video.readyState + ' for ' + Math.round((now - lowReadySince) / 1000) + 's',
          video.readyState
        );
      }
    }, 1000);

    // ── WS message dispatch ───────────────────────────────
    function handleMessage(msg) {
      switch (msg.type) {
        case 'session_info':
          titleEl.textContent = msg.title || 'Watch Party';
          sessionStartOffsetSec = typeof msg.startOffsetSec === 'number' ? msg.startOffsetSec : 0;
          sessionDurationSec = typeof msg.durationSec === 'number' ? msg.durationSec : null;
          break;
        case 'state':
          applyState(msg);
          break;
        case 'play':
          hostPlaying = true;
          lastKnownPosition = msg.position;
          if (sessionState !== 'LIVE') applyState({ type: 'state', state: 'LIVE', position: msg.position, playing: true, serverTime: msg.serverTime });
          else {
            seekTo(msg.position);
            playVideo();
          }
          break;
        case 'pause':
          hostPlaying = false;
          lastKnownPosition = msg.position;
          seekTo(msg.position);
          video.pause();
          break;
        case 'seek':
          lastKnownPosition = msg.position;
          seekTo(msg.position);
          break;
        case 'heartbeat':
          lastKnownPosition = msg.position;
          lastHeartbeat = { position: msg.position, serverTime: msg.serverTime, localTime: Date.now() };
          break;
        case 'session_end':
          ended = true;
          showCenter('Session ended', 'The host has ended this watch party.', false);
          if (hlsRetryTimer) clearTimeout(hlsRetryTimer);
          if (ws) try { ws.close(1000); } catch (e) {}
          if (hls) try { hls.destroy(); } catch (e) {}
          break;
      }
    }

    function setConn(ok) {
      if (ok) {
        connDot.classList.remove('disconnected');
        connDot.classList.add('connected');
        connText.textContent = 'Connected';
      } else {
        connDot.classList.add('disconnected');
        connDot.classList.remove('connected');
        connText.textContent = ended ? 'Disconnected' : 'Reconnecting…';
      }
    }

    function connect() {
      if (ended) return;
      var scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
      ws = new WebSocket(scheme + '//' + location.host + '/ws');
      ws.addEventListener('open', function () {
        setConn(true);
        try { ws.send(JSON.stringify({ type: 'join' })); } catch (e) {}
      });
      ws.addEventListener('message', function (ev) {
        try {
          var msg = JSON.parse(ev.data);
          handleMessage(msg);
        } catch (e) { /* ignore */ }
      });
      ws.addEventListener('close', function () {
        setConn(false);
        if (ended) return;
        if (reconnectTimer) clearTimeout(reconnectTimer);
        reconnectTimer = setTimeout(connect, 2000);
      });
      ws.addEventListener('error', function () { /* close handler runs after */ });
    }

    showCenter('Connecting…', 'Reaching the host.', true);
    connect();
  }());
  </script>
</body>
</html>`;
