# Changelog

## v3.5.0 — Unreleased

### Added

### Changed

### Technical

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
