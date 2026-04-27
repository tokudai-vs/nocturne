# Changelog

## v3.0.0 — 2026-04-19

### Added — Trakt.tv Integration
- Connect Trakt account via OAuth device flow
- Auto-scrobble playback (start/pause/stop) with 80%+ watched threshold
- Bidirectional watched-state sync (initial pull with preview, ongoing 6h refresh)
- Trakt watchlist as virtual library in sidebar
- Trakt ratings displayed on detail pages alongside IMDB/RT
- Watchlist toggle (Bookmark) on detail pages
- Drift indicator for items watched on Trakt but not locally
- Encrypted token storage via safeStorage (DPAPI on Windows)
- Configurable scrobble settings + manual sync trigger
- Failed-event queue with exponential backoff retry

### Changed
- Mark played/unwatched in Nocturne now also syncs to Trakt (when connected)
- Settings page reorganized with Trakt section

### Technical
- 3 new SQLite tables: `trakt_watched_history`, `trakt_watchlist`, `trakt_ratings`
- All Trakt joins use indexed columns (no perf impact at 1.5M items)
- Bundled `client_id` with optional override for advanced users

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
