# Changelog

## Unreleased

Cross-server correctness release. Full root-cause analysis in
`docs/AUDIT-2026-07-cross-server-correctness.md`. **After installing, run one
manual full sync** — it repairs `library_id` values corrupted by the old
resume-refresh and rebuilds dedup on clean data.

### Added

- **Per-server sync-failure indicator** — a failed or unreachable server is no
  longer silent: an amber warning chip (with the failing server names) persists
  in the corner until that server's next successful sync, plus a one-time toast
  per session. Per-server health is persisted and survives restarts.
- **Guest-cap enforcement, for real** — the Watch Party sync server now refuses
  over-cap WebSocket connections at handshake time with an explicit
  "This watch party is full" state on the guest page (no reconnect loop). The
  10-guest ceiling is enforced in the main process regardless of what the
  renderer sends, unless the Danger Zone unlock is on.
- **Mark played/unplayed cascades across the dedup group** — the user sees one
  deduped item, so an explicit mark now applies to every copy on every server
  (clicked copy strict, siblings best-effort, one Trakt push).

### Fixed

- **Continue Watching tells the truth.** Playback stops (ESC, natural EOF,
  episode auto-advance, Watch Party) now write the final position into the
  local cache with Emby's resume thresholds; the row is ordered by when you
  actually played, not when the cache last synced; finished-elsewhere ghosts
  are reconciled away against the server's resume list each sync (with a 24h
  guard protecting fresh local-only progress); and clicking a second-server
  item actually plays it.
- **Cross-server resolution across the app.** Images, detail-page enrichment,
  season/episode lists, similar items, favorites, and mark-played all resolve
  against the server that owns the item instead of the active one — foreign
  posters no longer 404 into fallbacks, and foreign series pages get their
  episode lists back.
- **Episode navigation reports to the right server.** Cross-server binge
  progress (auto-advance and manual prev/next) lands on the owning server and
  mirrors into the local cache; the mark-played cascade now fires only when an
  item was actually finished (≥90%), instead of on every stop.
- **Sync integrity in combined mode.** The resume-refresh no longer reassigns
  items to the wrong library; an incremental sync no longer advances its
  watermark past a failed server (which permanently skipped that server's
  changes); ending a Watch Party from the waiting room no longer reports a
  phantom playback stop to Emby/Trakt; and quitting mid-party can no longer
  orphan ffmpeg/cloudflared.

## v3.5.0 — 2026-06-10

### Added

**Watch Party** — host a synchronized watch party from your library; friends join with a link, no install:

- One-click hosting from any movie detail page. Pick an item, click Watch Party, share the invite URL; guests watch in any modern browser (Chrome 80+ / Firefox 78+ / Edge 80+ / Safari 14+).
- **First-run setup** downloads pinned ffmpeg + cloudflared binaries with checksum verification (one time, ~80 MB). Nothing Watch-Party-related runs at app boot — setup, probe, and sessions are all behind explicit clicks.
- **Hardware encoder probe** (NVENC → Quick Sync → AMF → libx264) with cached result drives encoder-specific ffmpeg arguments.
- **Two-phase start.** *Start Session* spawns the transcoder and the Cloudflare Quick Tunnel, then drops you in a waiting room (shareable URL + copy button, live guest count, transcode-buffer progress). *Start the Show* unlocks once a 60-second head start is transcoded and begins playback for everyone at once.
- **VOD transcode-ahead pipeline.** The whole file is transcoded ahead of playback to H.264 HLS (4s segments, framerate-independent keyframe alignment via `-force_key_frames`), so the transcode outruns playback and seeking is instant on the finished range. Bitrate ladder: 2.5 / 5 / 20 Mbps for 720p / 1080p / 4K. Input probing is trimmed (`-probesize 1M -analyzeduration 1M -fflags +nobuffer`) to cut remote-source cold-start by tens of seconds.
- **Cinema-mode host player.** The host watches via an embedded hls.js player inside Nocturne (solo playback stays on mpv, untouched): zero-chrome fullscreen stage, auto-hiding overlay controls with cursor hide, keyboard shortcuts (Space/K play-pause, ←/→ seek 10s, M mute, F fullscreen), guest counter, and a movie-time seek bar that distinguishes played / transcoded / not-yet-transcoded ranges.
- **Guest cinema page** — self-contained HTML + bundled hls.js (no CDN) served through the tunnel: waiting room, auto-reconnecting WebSocket, volume-only controls ("Host controls playback"), ended state.
- **Tight sync.** Host play/pause/seek broadcast over WebSocket; ~1s heartbeats feed a guest-side deadzone-ladder drift corrector (<0.5s: leave it; 0.5–3s: playback-rate nudge; ≥3s: hard seek). Guests preload the stream during the waiting room — the transcode head start doubles as their buffer head start, so the show starts in about a second instead of paying HLS startup through the tunnel. Late joiners drop in at the host's current position.
- **Resume support.** Start the party from your saved Emby resume position (fast `-ss` input-seek over HTTP range). A watchdog detects servers that refuse range requests and explains the slow start in the waiting room.
- **Watch-history reporting (optional).** Progress reports to the picked version's Emby server every 10s plus Trakt scrobbling (start/pause/stop with edge de-duplication), matching solo-playback semantics — including the cross-server mark-played cascade in combined mode.
- **Session hygiene.** Server-side guest-cap enforcement; per-session file log at `{userData}/watch-party/session.log` with tokens redacted; stale-session sweep; teardown kills ffmpeg + cloudflared and deletes the session dir on End, on errors, and on app quit (including quitting mid-teardown). Navigating away from the host page mid-session asks to end-for-everyone first — no stranded sessions.
- **CPU-only systems are blocked by default** in pre-flight with an explanation: software encoding cannot reliably outrun playback. A Danger Zone override exists for testing.

**Danger Zone (Settings)** — four explicit Watch Party unlocks, each behind a TL;DR-first warning modal with checkbox acknowledgment:

- **Remove the 10-guest limit** — copyright, tunnel fair-use, and bandwidth implications spelled out.
- **Prefer the 4K version as transcode input** — better downscale master at the cost of a much heavier transcode and higher source bandwidth; output unchanged.
- **Allow 4K (2160p) output** — adds a 4K ceiling to pre-flight quality (~20 Mbps upload per guest). Output is always min(source resolution, ceiling); Nocturne never upscales.
- **Enable Watch Party on CPU-only systems** — testing override; sessions are expected to stall.

### Changed

- Renderer CSP gains exactly `media-src 'self' blob:` and `worker-src 'self' blob:` for hls.js MSE playback — no other widening.
- AppShell supports a chrome-less cinema mode (no TopBar, no padding) used by the Watch Party host page during LIVE.
- 720p Watch Party streams now encode at 2.5 Mbps instead of inheriting the 1080p 5 Mbps target.

### Technical

- New main-process modules: `watchparty-binary-manager`, `watchparty-encoder-probe`, `watchparty-transcoder`, `watchparty-http-server`, `watchparty-sync-server`, `watchparty-tunnel`, `watchparty-session` (state machine: IDLE → INITIALIZING → WAITING → LIVE → teardown), `watchparty-guest-page`, `watchparty-logger`.
- `hls.js` and `ws` are bundled dependencies — no CDN fetches. Stream URLs are built in the main process from version identifiers; Emby tokens never reach the renderer or guests.
- Single localhost HTTP server (OS-assigned port) serves HLS, the guest page, and the WebSocket upgrade on one origin through one tunnel. Strict path allowlist for session-dir files.
- HLS playback uses `startPosition` pinning — hls.js otherwise treats the growing EVENT playlist as live and parks new players at the live edge instead of the show's timeline.
- Watch Party session log is the survivable diagnostics channel for packaged builds (GUI-subsystem Windows Electron detaches stdout).

---

## v3.0.0 — 2026-05-13

### Added

**Trakt.tv integration**
- OAuth device flow to connect a Trakt account; bundled `client_id` with optional override under Settings → Trakt → Advanced
- Auto-scrobble playback events (`start` / `pause` / `stop`) — Trakt marks watched at 80%+
- Bidirectional watched-state sync: post-connect preview with per-item checkboxes for the initial backfill, then a 6-hour background refresh
- Trakt watchlist exposed as a virtual library in the sidebar; unmatched titles render greyed-out with a "Find on TMDB / Remove" info modal
- Trakt ratings on detail pages alongside IMDB / Rotten Tomatoes
- Add / remove from watchlist directly on detail pages via a Bookmark toggle
- Drift indicator ("T✓") on items watched on Trakt but not on the current server, with one-click sync
- Encrypted credential storage via Electron `safeStorage` (DPAPI on Windows)
- Failed-event queue with exponential backoff so scrobbles + history pushes survive offline periods
- Manual sync trigger and configurable scrobble settings under Settings → Trakt

**Skip intro / credits / recap (TheIntroDB)**
- Amber-filled clickable skip button — mouse click or Enter key to jump past the segment
- Per-segment re-arm on backward seek (seeking back before a segment's start re-triggers the prompt)
- Lua-driven 60Hz fill animation matching the ModernZ amber theme
- "Next Episode" mode for credits — credits prompt advances to the next episode instead of skipping ahead in the current one

**Next / Previous episode navigation**
- `>` and `<` keybinds inside mpv jump to adjacent episodes
- Cross-server-aware advance — picks the right server when the next episode lives elsewhere
- Natural EOF auto-advance via mpv's `eof-reached` property (mpv pauses at EOF under `keep-open=yes`; we observe the property flip)

**Watch History Analytics page** (`/analytics`)
- Accessible from the user menu (top-right avatar dropdown)
- Source selector: **Local** (Emby cache), **Trakt** (mirrored history), **Combined** (union with Trakt timestamp preference). Hidden when Trakt is not connected.
- Time range selector: Last 30 days / 90 days / 1 year / All time
- Stat cards: total watched, total watch time, currently watching, average per week
- Lifetime stats card (Trakt mode) — movies / episodes / lifetime watch time / distinct shows from `/users/me/stats`, cached 1h
- Activity heatmap — GitHub-style grid with range-adaptive cell sizing (fat cells for 30d, GitHub-fine for year view), day-of-week + month labels, 5 quantile-based intensity buckets
- Watch frequency bar chart — daily bars for 30d/90d, weekly aggregation for 1y/all-time, Y-axis in hours with nice-step gridlines, sparse rotated X labels
- Top series (by episode count in range), top movies (poster grid), genre breakdown (top 8 + Other bucket)
- On-demand Trakt history backfill with configurable cap (Last 2 years / Full history) and live progress

**Image fallback chain for dedup-grouped items**
- When a primary image fails to load, MediaCard automatically cycles through sibling versions' URLs from the same dedup group
- Session-level cache of known-bad URLs to skip them on subsequent renders
- Same chain for HeroBackdrop on detail pages

**Trailer playback on detail pages**
- "Watch Trailer" button opens the trailer URL externally (YouTube) via `shell.openExternal`
- Sourced from Emby's `RemoteTrailers` field

**Subtitle auto-download**
- Settings toggle for automatic OpenSubtitles search on playback start
- Preferred-language setting (ISO 639-2/B) plumbed through the existing `open-subtitles.lua` integration

**Pre-release bundling guards**
- `scripts/check-relative-requires.js` — fails `prebuild` if any `src/**` file does `require('./…')`
- `scripts/check-bundling.js` — fails `postbuild` if any `src/main/*.ts` with non-type exports is absent from the bundled `out/main/index.js`
- Pre-release smoke test checklist in `AGENTS.md` and `CLAUDE.md`

### Changed
- Mark played / mark unwatched in Nocturne now also pushes to Trakt when connected
- Settings page reorganized with a Trakt section that has an Advanced subsection (Trakt history backfill range, clear failed event queue, manual sync)
- mpv OSC mouse trigger now responds to any mouse movement (`deadzonesize=0`) — fixes the controls feeling unresponsive
- mpv window controls removed (`window_controls=no`) so the embedded title bar doesn't double up with the OS chrome
- `npm run lint` now runs ESLint v9 flat config + the static require-scan; build-script lint covered for the first time

### Fixed
- Trakt `scrobble/pause` with progress < 1.0% now clamps to 1% before send (Trakt rejected these with 422)
- Trakt `scrobble/pause` with progress >= 80% auto-converts to `scrobble/stop` (Trakt API requires `stop` above that threshold)
- Image fallback for dedup-group primaries whose source server returns a broken image
- electron-vite dynamic-require bundling bug: `image-fallbacks` and `analytics` modules used to be referenced only via `require('./…')`, so they were silently dropped from `out/main/index.js` and every wrapped IPC handler threw `Cannot find module` at runtime; static imports now in place + permanent guard
- Skip intro re-arm — prompt fires again after seeking back before the segment's start (was previously a one-shot)
- Trakt history queue prune on startup — drops poison events from pre-fix versions (progress=0 stops/pauses, and pauses with progress >= 80%) so they don't retry forever
- `initDatabase` on existing DBs no longer fails with "no such column: last_played_date" — the ALTER TABLE migration now runs before any index that references the column
- `npm run lint` used to exit 0 silently when ESLint had no config — fixed by shipping an `eslint.config.js` and adding the require-scan as a real failure path

### Technical
- New SQLite tables: `trakt_scrobble_queue`, `trakt_watched_history`, `trakt_watchlist`, `trakt_ratings`
- New column: `items.last_played_date` (TEXT, partial index `WHERE last_played_date IS NOT NULL`) — populated on every play action so the analytics activity-by-day queries can range-filter without scanning the whole table
- All Trakt joins use indexed columns (no perf impact at 1.5M items)
- Static-import enforcement for `src/main/` relative modules via `scripts/check-relative-requires.js`
- Postbuild bundle-presence check via `scripts/check-bundling.js`
- `scripts/download-mpv.js` — removed dead recursive `copyDir` helper that blocked the freshly-enabled lint pass

---

## v2.0.0 — 2026-04-19

### Added
- **SQLite-backed cache** for instant browsing — every page reads from disk first, enriches from API in background. Tested with 1.5M+ items.
- **Multi-server support** — add, switch, and remove multiple Emby servers; per-server library mappings and cached data.
- **Combined library mode** — merge libraries across multiple servers into unified shelves with cross-server deduplication.
- **Library deduplication** — TMDB ID → IMDB ID → Name+Year matching, with episode-level dedup across multiple series versions.
- **Virtual libraries** — group multiple Emby libraries (e.g., `Movies` + `Movies 4K` + `Movies Anime`) into a single shelf, with drag-and-drop mapping and an auto-grouping wizard on first launch.
- **Version picker** on detail pages showing quality, codec, and file size per copy; preferred-quality setting (highest/lowest) for automatic selection.
- **Settings page** with subtitle appearance config, power modes (performance/balanced/efficiency), cache stats, and server management.
- **OpenSubtitles integration** — press `b` during playback to search and download subtitles in 50+ languages.
- **Auto-update** via electron-updater + GitHub Releases — download progress bar, restart-to-update notification, manual "Check for Updates" in Settings.
- **Right-click context menus** on every media card (mark played, favorite, go to series, etc.).
- **Toast notification** system for user feedback.
- **Reset App** option in Settings → Danger Zone (clears cache, settings, and servers).
- **Anchored audio / subtitle / playlist menus** in mpv — pinned panels instead of transient popups.
- **Disk-backed image cache** with LRU eviction and configurable size limit.
- **Hero banner** with rotating backdrop images on the home page.
- **Saved server cards** on the login page for quick reconnection.
- **Session expiry detection** (HTTP 401) with automatic redirect to login.
- **Error boundary** in the renderer to survive unexpected crashes.

### Changed
- mpv now runs in **idle mode** for sub-second playback startup (no process restart between titles).
- **Continue Watching** is now deduplicated across versions.
- **Search results** are deduplicated and show **version count badges**.
- Sync engine rewritten with **batched fetches** and **checkpointed resume** for interrupted syncs.
- All HTTP funnels through the main process via `embyClient` singleton; renderer never makes direct network requests.

### Fixed
- Sync stability at 1.5M+ items (memory pressure, write contention, request pacing).
- Multiple combined-mode dedup edge cases (cross-server TMDB collisions, episode count mismatches).
- Memory and performance regressions during long-running syncs.

---

## v1.0.0 — 2026-04-10

### Features
- Plex-like dark cinematic browsing UI with hero banners, poster grids, and detail pages
- Full Emby server integration (login, libraries, search, metadata, watch status)
- Video playback via bundled mpv with full codec support (HEVC, DTS, TrueHD, HDR10, Dolby Vision)
- ModernZ on-screen controller with amber-themed seek bar and interactive controls
- Hot-loaded mpv (idle mode) for instant playback — zero startup delay
- Smooth fade-to-black transitions between browsing and playback
- Continue Watching and Next Up rows on home screen
- Series detail with season tabs and episode lists
- Infinite scroll library browsing with sort/filter
- Real-time search with dropdown preview
- Playback progress reporting to Emby server
- Resume playback from last position
- Custom frameless window with integrated title bar
- Splash screen on startup
- Session persistence (auto-login on restart)
- Keyboard shortcuts throughout
- Windows installer (NSIS)

### Tech Stack
- Electron 33 + React 19 + TypeScript
- Vite (via electron-vite) for build
- Zustand for state management
- mpv (bundled) for video playback with JSON IPC
- ModernZ OSC for player interface
- CSS Modules for styling
- Lucide React for icons
