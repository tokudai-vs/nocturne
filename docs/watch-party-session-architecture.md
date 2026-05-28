# Nocturne Watch Party — Session-Spawn Architecture (v3.5)

*Design spec for the playback half of Watch Party. The setup half (binary
manager, encoder probe, setup modal, pre-flight modal, guest cap, Danger Zone
unlock) is complete and committed in 759b938. This document covers everything
from "host clicks Start Session" onward.*

---

## 1. Design principles

1. **Sync is the top priority.** Everyone — host included — watches the same
   HLS stream through the same player (hls.js). Pipeline symmetry is what makes
   tight sync achievable. The host does **not** watch via mpv during a party.
2. **Pre-transcode to VOD.** ffmpeg transcodes the whole file ahead of
   playback. The two-phase start (Start Session → Start the Show) turns the
   social latency of sharing a link and waiting for friends into the transcode
   head-start. A static VOD playlist makes seeking instant and perfectly
   syncable.
3. **Host plays via embedded hls.js in the Nocturne renderer**, not mpv. Solo
   playback stays on mpv, completely untouched. This also sidesteps the
   window-swap hack — the host's video and session controls live in the same
   window.
4. **Hot-path discipline preserved.** None of this runs at boot or blocks first
   paint. Everything is behind an explicit user action (Start Session), exactly
   like the binary download and encoder probe.
5. **Default to the lightest correct path.** 1080p source → 1080p output by
   default; dedup picks the 1080p version. Heavier paths (4K source, 4K output)
   are opt-in via Danger Zone.

---

## 2. Quality matrix (output is a ceiling — "up to," never upscale)

Output resolution = **min(source resolution, selected ceiling)**. We never
upscale. If the ceiling is 4K but only a 1080p source exists, the output is
1080p.

| Setting | Output ceiling | Source preference | Actual output |
|---|---|---|---|
| default | 1080p | 1080p version | 1080p (or source res if lower) |
| 4K-source toggle ON | 1080p | 4K version | 1080p, downscaled from the 4K master (niche quality bump, heavy transcode) |
| 4K-output toggle ON | up to 4K | 4K version if present | 4K if a 4K source exists, else 1080p |

**Pre-flight quality options:**
- 4K-output toggle off → `1080p (recommended)`, `720p`
- 4K-output toggle on → add `4K (2160p)` as a ceiling option

**Two independent Danger Zone toggles, per your call:**
- **4K source input** — prefer the 4K version as ffmpeg's input even for a
  1080p output (better downscale master). Output unchanged.
- **4K output** — raises the output ceiling to 4K. Capped by source: only
  yields 4K when a 4K version exists; otherwise outputs at the source
  resolution. No upscaling, ever.

If only a 4K version of the media exists (no 1080p in the dedup group), the
transcoder uses the 4K source and downscales to the chosen ceiling regardless
of toggles.

---

## 3. Session lifecycle state machine

```
IDLE
 │  [host clicks Start Session in pre-flight modal]
 ▼
INITIALIZING
 │  • resolve source version from dedup group (1080p preferred)
 │  • create session dir
 │  • start local HTTP server (localhost only)
 │  • spawn ffmpeg → begin VOD transcode
 │  • spawn cloudflared ∥ (parallel) → wait for tunnel URL
 │  [tunnel URL resolved]
 ▼
WAITING                          ← the transcode head-start window
 │  HOST sees:  shareable URL + copy button + guest count
 │             + transcode progress ("Preparing… 45s ready" → "Ready")
 │             + "Start the Show" button (DISABLED until min buffer ≥ 60s)
 │  GUESTS see: "Waiting for the host to start the show" + who's connected
 │  [host clicks Start the Show]
 ▼
LIVE
 │  • host's embedded hls.js player begins playback
 │  • host video.currentTime = master clock, broadcast over WebSocket
 │  • guests play, drift-correct to master
 │  • host pause/play/seek → broadcast → everyone follows
 │  • late joiners drop in at host's current position
 │  [host clicks End  |  playback reaches EOF]
 ▼
TEARDOWN
 │  • broadcast session_end to all WebSocket clients
 │  • kill ffmpeg, close HTTP server, kill cloudflared
 │  • delete session dir
 ▼
IDLE
```

**Why "Start the Show" is buffer-gated:** prevents the host starting before
enough runway exists. In practice the buffer is ready long before the first
friend joins, but the gate protects the CPU-host case where transcode is slow.

---

## 4. Session directory layout

```
{userData}/watch-party/session-{id}/
├── stream.m3u8          ← VOD playlist, grows as transcode progresses
├── segment_00000.ts
├── segment_00001.ts
└── …                    ← all segments retained (VOD, not live window)
```

The guest HTML page is served from app resources, not the session dir.
Session dir is deleted entirely on TEARDOWN. A stale-session sweep on the next
Start Session cleans anything a crash left behind (same pattern as the binary
manager's `cleanupTmp`).

---

## 5. Transcode pipeline (VOD, transcode-ahead)

**Input:** the selected source version's Emby stream URL, token in query string
(the same direct-play URL the version picker resolves). Note: your Emby servers
are remote, so the host pays bandwidth both directions — download source from
Emby + upload HLS to guests.

**Output:** H.264 HLS, VOD playlist type, segments retained (NOT
`delete_segments` — that's for live; we want the whole timeline available for
instant seeks).

**Transcode-ahead behavior:**
- ffmpeg runs faster than realtime when hardware-accelerated (NVENC ≈ 3-5× at
  1080p), so the transcode outruns playback. Within minutes the full movie is a
  complete VOD playlist and every seek is instant.
- Playback starts (Start the Show enabled) once a minimum head-start exists
  (≥ 60s transcoded).

**CPU-only hosts: disabled by default.** libx264 (veryfast/ultrafast) runs
roughly 1-2× realtime at 1080p — the transcode may barely outrun playback or
fall behind, pushing guests toward the live edge and stalling. A CPU host's
watch party is structurally unreliable. So:

- If the encoder probe returns `preferred === 'libx264'` (no hardware encoder),
  Watch Party is **disabled by default**. The pre-flight modal shows a blocked
  state — "Watch Party requires a hardware encoder (NVIDIA, Intel, or AMD)" —
  with a short explanation and a pointer to the Danger Zone override, instead of
  the quality/guest/legal form.
- A fourth Danger Zone toggle, **"Enable Watch Party on CPU-only systems,"**
  unlocks it with a clear warning: software encoding cannot reliably keep pace
  with playback, sessions will likely stall and desync, and the experience will
  be poor for everyone. For testing or low-resolution/short-clip use only.
- The 10s probe still runs first, so a CPU host waits ~10s before seeing the
  blocked state (instant after the probe caches).

**Margin tracking** (still relevant for hardware hosts under load, and for
unlocked CPU hosts):
- Track `margin = transcodedSeconds − playbackPosition`.
- Hardware encoder: margin grows monotonically — transcode finishes early.
- Unlocked CPU host: margin may stall or shrink. If it trends toward zero during
  LIVE, log a warning and surface a guest-side "buffering" affordance.

**Seek beyond the transcoded edge (rare, early only):** if the host seeks past
`transcodedSeconds` in the first couple minutes, either (a) show a brief
"buffering" until transcode reaches it, or (b) restart ffmpeg from the seek
point (abandoning sequential). Edge case — decide during implementation.

---

## 6. Sync protocol (WebSocket)

Control channel only — no media flows over the WebSocket. Media is HLS over
HTTP(S).

**Host → Guests:**
```
{ type: "session_info", title, duration, hlsLatencyHint }   // on connect
{ type: "play",   position }
{ type: "pause",  position }
{ type: "seek",   position }
{ type: "heartbeat", position, serverTime }                 // ~every 3-5s
{ type: "session_end" }
```

**Guests → Host (optional, diagnostics):**
```
{ type: "join",  clientId }
{ type: "ack",   clientId, drift }
```

**On guest connect (any time, including late-join during LIVE):** server
immediately sends `session_info` + the current state (playing/paused +
position). The guest seeks to that position and matches play/pause — that's the
late-join "drop at host's current position" behavior.

---

## 7. Sync correction (guest side)

Master clock = host's `video.currentTime`, delivered via heartbeat. Guest
computes expected host position accounting for heartbeat transit, compares to
local `video.currentTime`, and applies the **deadzone ladder**:

```
drift = expectedHostPos − localPos

|drift| < 0.5s        → do nothing            (acceptable, no judder)
0.5s ≤ |drift| < 3s   → nudge playbackRate     (1.05× / 0.95× to ease back)
|drift| ≥ 3s          → hard seek to host pos   (instant on VOD playlist)
```

Rate-nudging for small drifts avoids the constant micro-seek judder that makes
naive implementations unwatchable. Hard seeks are cheap here because the
playlist is VOD — the target segment already exists.

Discrete events (play/pause/seek) are applied immediately on receipt; the
heartbeat is the continuous background correction.

---

## 8. UI states

### Host (in the Nocturne window — no mpv, no window swap)

- **Pre-flight modal** (exists) → Start Session
- **Waiting room:** shareable URL + copy button, live guest count, transcode
  progress ("Preparing… Ns ready" → "Ready"), "Start the Show" button
  (disabled until buffer ≥ 60s), End button
- **Live:** embedded hls.js `<video>`, playback controls (host-only), guest
  count, End Session button. Host's controls drive the WebSocket broadcasts.

### Guest (browser, zero install)

- **Open URL → Waiting room:** "Waiting for the host to start the show",
  optionally who else is connected
- **Live:** hls.js `<video>`, playback controlled by host (local controls
  either hidden or show "Host controls playback"), local volume control only
- **Ended:** "The host has ended this session." No auto-redirect.

---

## 9. Module / build sequence

Each is its own investigate → plan → review → write gate.

1. **Item/version context plumbing** — thread the *already-existing* v2 dedup
   group + version list from DetailPage → WatchPartyButton → pre-flight →
   session manager, via props. DetailPage already has this data (it renders the
   version picker). `WatchPartyButton` currently takes no props and the
   pre-flight modal only takes `onClose` — that's the whole gap. No new dedup
   work; leans entirely on v2. Small, unblocks everything. *First.*
2. **VOD HLS transcoder** (main) — ffmpeg whole-file transcode-ahead, encoder
   args from the probe result, margin tracking, source-version selection.
3. **Local HTTP server** (main) — serve playlist + segments (localhost for host,
   exposed via tunnel for guests) + the guest HTML page.
4. **Embedded host player** (renderer) — hls.js `<video>` + waiting room + live
   controls, in the Nocturne window.
5. **WebSocket sync server + protocol** (main) — the message schema above.
6. **Tunnel manager** (main) — cloudflared spawn, URL parse, lifecycle.
7. **Guest HTML page** (static) — hls.js + sync client + waiting room + ended
   states.
8. **Session manager** (main) — owns the state machine, orchestrates 2-7.
9. **Danger Zone toggles + pre-flight gating** — three new settings mirroring
   the guest-limit unlock: (a) 4K source input, (b) 4K output ceiling,
   (c) enable on CPU-only. Plus the pre-flight 4K quality option and the
   CPU-only blocked state.
10. **Wire Start Session / Start the Show / End** — connect the buttons to the
    session manager.

Suggested first target: **piece 1** (context plumbing) — small, unblocks
everything, low risk. Then piece 2 (transcoder), which is the biggest unknown.

---

## 10. Failure modes

**Host-side:**
- *ffmpeg crash* → mark session FAILED, broadcast `session_end`, clean up. Don't
  auto-restart.
- *cloudflared crash* → same. Tunnel gone, guests unreachable.
- *disk full* → segments stop; monitor session dir size, warn, TEARDOWN cleans
  up.
- *machine sleep* → treat as End. Resuming a half-dead session is messier than
  restarting.

**Guest-side:**
- *browser close* → WebSocket closes; host logs it, session continues.
- *network blip* → hls.js buffer covers short drops; WebSocket reconnects with
  backoff; drift-correct on reconnect.
- *tunnel down* → segment fetches fail; after retries, guest shows "Host ended
  the session."
- *old browser* → document Chrome 80+/Firefox 78+/Edge 80+/Safari 14+.

---

## 11. Open questions / to investigate during build

- **cloudflared fanout:** confirmed per-connection proxy, not CDN fanout — so
  N guests = N × upstream from the host. Bandwidth math already accounts for
  this; verify with a 2-connection test before relying on it.
- **ffmpeg HLS VOD flags** for the bundled gyan.dev essentials build: exact
  `-hls_playlist_type`, segment naming, growing-playlist behavior. Verify on the
  Windows box.
- **ffmpeg reading Emby stream URL** with token in query string — confirm it
  authenticates and direct-plays without server-side transcode.
- **Session control visibility:** resolved — host plays in the renderer, so
  controls live in the Nocturne window. No mpv overlay needed.
- **Tunnel-before-content:** a guest opening the URL during WAITING gets the
  waiting room (served by the guest page), not an empty playlist — the guest
  page handles "not started yet."

---

*Push back on any of this. Once it's locked, we start with piece 1.*
