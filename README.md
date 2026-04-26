<div align="center">

# :movie_camera: Nocturne

**A modern, cinematic desktop client for Emby — with multi-server support, instant browsing from a local cache, and library deduplication.**

*A personal project born out of frustration — none of the existing Emby clients looked or felt good enough. Nocturne grew from a basic player into a full-featured Emby client backed by a local SQLite cache that makes browsing a million-item library feel instant, with multi-server support and cross-server deduplication on top.*

*This is not a production application. It's a personal tool that I'm sharing in case others find it useful or want to build on it. Expect rough edges.*

[Download](#installation) · [Features](#features) · [Screenshots](#screenshots) · [Building](#building-from-source) · [Contributing](#contributing)

---

</div>

## Features

### :zap: Cache & Performance
- **SQLite-backed local cache** (better-sqlite3, WAL mode) — tested with libraries of 1.5M+ items
- **Instant browse from cache** — every page loads from disk, then enriches in the background
- **Background sync engine** with full and incremental modes, checkpointed for interrupted resume
- **Disk-backed image cache** with LRU eviction and configurable size limit
- **Power modes** — `performance`, `balanced`, and `efficiency` profiles tune sync concurrency and image prefetch

### :satellite: Multi-Server
- Add multiple Emby servers and switch between them from the user menu
- **Separate** or **combined library mode** — view each server independently or merge libraries across servers into one shelf
- **Cross-server deduplication** — same movie on two servers shows up once, with a version picker
- Online/offline status indicator per server, saved server cards on the login page for quick reconnect

### :file_folder: Smart Library Organization
- **Virtual libraries** — group `Movies`, `Movies 4K`, and `Movies Anime` into a single "Movies" shelf
- **Auto-grouping wizard** on first launch suggests virtual library mappings
- **Drag-and-drop library mapping** in Settings, with configurable icons per virtual library
- Per-server mappings persist across syncs

### :card_index_dividers: Library Deduplication
- Matches duplicates by **TMDB ID → IMDB ID → Name+Year** fallback chain
- **Version picker** on detail pages showing quality, codec, and file size per copy
- **Episode-level dedup** across multiple series versions (e.g., 1080p + 4K remux of the same show)
- Deduped Continue Watching, search results (with version count badges), and home rows
- Preferred-quality setting (highest / lowest) for automatic version selection

### :tv: Playback
- **Bundled mpv** — no separate install required
- Full codec support: HEVC/H.265, H.264, DTS, TrueHD, Atmos, AAC, **HDR10**, **Dolby Vision**
- **Direct play** — no server-side transcoding
- **Instant playback** — mpv runs in idle mode, `loadfile` is sub-second
- **ModernZ OSC theme** with amber accent — interactive seek bar, draggable controls
- **Anchored audio / subtitle / playlist menus** — pinned panels, not transient popups
- Resume from exact position, smooth fade-to-black transitions between browser and player

### :speech_balloon: Subtitles
- Configurable appearance: font, size, color, border, background, position
- **OpenSubtitles integration** — press `b` during playback to search and download
- 50+ language code mappings for clean track labels

### :art: UI
- Plex-like dark theme with amber accents
- **Hero banner** with rotating backdrop on the home screen
- **Continue Watching** and **Next Up** rows, deduplicated across versions
- **Right-click context menus** on every card (mark played, favorite, go to series, etc.)
- Real-time search with version count badges and instant dropdown preview
- Smooth fade-to-black transitions for playback start/end

### :gear: Other
- **Auto-update** via electron-updater + GitHub Releases (download progress + restart prompt)
- **Toast notification** system for user feedback
- **Error boundary** to keep the renderer alive after unexpected crashes
- **Session expiry handling** — 401 detection redirects to login automatically
- **Reset App** option in Settings → Danger Zone (clears cache, settings, and servers)

### :keyboard: Keyboard Shortcuts

**Browsing:**
| Key | Action |
|-----|--------|
| `Ctrl+K` | Focus search |
| `Ctrl+B` | Toggle sidebar |
| `Escape` | Close sidebar / menus |

**Playback (mpv):**
| Key | Action |
|-----|--------|
| `Space` | Play / Pause |
| `←` / `→` | Seek -10s / +10s |
| `Shift+←` / `Shift+→` | Seek -30s / +30s |
| `↑` / `↓` | Volume up / down |
| `S` | Cycle subtitles |
| `A` | Cycle audio tracks |
| `B` | Search OpenSubtitles for current file |
| `M` | Toggle mute |
| `F` | Toggle fullscreen |
| `ESC` / `Q` | Stop playback |

## Screenshots

> *Screenshots from v1 are outdated and have been removed pending v2 captures — see the [releases page](../../releases) for the latest build.*

<!--
Add v2 screenshots here:
### Home Screen (hero banner + Continue Watching)
![Home](docs/screenshots/home.png)

### Detail Page (version picker)
![Detail](docs/screenshots/detail.png)

### Combined Library Browse
![Library](docs/screenshots/library.png)

### Settings (subtitle config + power modes)
![Settings](docs/screenshots/settings.png)

### Player (ModernZ + anchored menus)
![Player](docs/screenshots/player.png)
-->

## Installation

### Windows (Recommended)

1. Download the latest `Nocturne-Setup-2.0.0.exe` from the [Releases page](../../releases)
2. Run the installer
3. Launch Nocturne from the Start Menu or Desktop shortcut
4. Enter your Emby server address and sign in
5. (Optional) Add additional servers from the user menu, or enable combined mode in Settings

> **Note:** mpv is bundled with the installer — no separate installation needed.

## Building from Source

### Prerequisites
- Node.js 20+
- npm
- Windows 10/11 (for building the Windows target)
- mpv installed (`winget install shinchiro.mpv`) — needed for development

### Setup

```bash
# Clone the repo
git clone https://github.com/YOUR_USERNAME/nocturne.git
cd nocturne

# One-command setup (installs deps, downloads mpv + ModernZ,
# mirrors our custom mpv portable_config to resources/, generates icons)
npm run setup

# Or do it manually:
npm install
npm run download-mpv
npm run download-modernz
npm run mirror-mpv-config
npm run generate-icons

# Start in development mode
npm start
```

### Building the Installer

```powershell
# From Windows PowerShell:
npm run generate-icons
npm run package:win
```

The installer will be at `dist/Nocturne Setup 2.0.0.exe`.

### Notes for Development

- **First sync time scales with library size** — a 1M-item library takes roughly 4–5 hours for the initial full sync. Incremental syncs after that are minutes.
- **Combined mode requires 2+ servers** to be configured before it can be enabled in Settings.
- **Manual rebuild dedup** may be needed after major library reorganizations on the server (renaming libraries, large bulk imports). It's available in Settings → Cache.

## Architecture

```
┌─────────────────────────────────────────────────┐
│             Electron Main Process               │
│  ┌──────────────┐  ┌────────────┐  ┌─────────┐ │
│  │ EmbyClient   │  │ MpvManager │  │ Updater │ │
│  │ (REST/IPC)   │  │ (JSON IPC) │  │         │ │
│  └──────┬───────┘  └─────┬──────┘  └────┬────┘ │
│         │                │              │       │
│  ┌──────┴───────┐  ┌─────┴──────┐  ┌────┴────┐ │
│  │ ServerMgr    │  │ SyncEngine │  │ Settings│ │
│  │ Multi-server │  │ Checkpoint │  │ Store   │ │
│  └──────┬───────┘  └─────┬──────┘  └─────────┘ │
│         │                │                      │
│  ┌──────┴────────────────┴──────────────────┐  │
│  │ SQLite (better-sqlite3, WAL)             │  │
│  │ items · dedup_groups · sync_state · imgs │  │
│  └──────────────────────────────────────────┘  │
│         │                                       │
│    IPC Bridge (contextBridge → window.api)      │
├─────────┴───────────────────────────────────────┤
│             Renderer Process                    │
│  React 19 + TypeScript + Zustand + CSS Modules  │
└─────────────────────────────────────────────────┘
         ↕ JSON IPC (named pipe)
┌─────────────────────────────────────────────────┐
│  mpv (bundled, idle mode)                       │
│  + ModernZ OSC                                  │
│  + open-subtitles.lua + nocturne_select.lua     │
│  + portable_config                              │
└─────────────────────────────────────────────────┘
```

### Key Design Decisions

- **Cache-first** — every page reads from SQLite first, then enriches from the API in the background. Browsing stays instant even on cold starts.
- **mpv as external process** — Electron's Chromium compositor cannot embed mpv video. mpv runs as a separate fullscreen window with the main window hidden during playback. Smooth fade transitions mask the window swap.
- **Idle mode** — mpv starts once on app launch and stays running. Playback is triggered via `loadfile` IPC command — instant, no process startup delay.
- **Dedup pipeline** — TMDB → IMDB → Name+Year. Runs after each sync, persisted to SQLite, and respected by every list query (Continue Watching, search, library, home rows).
- **Multi-server combined mode** — virtual libraries can span servers. Cross-server dedup makes the same movie on two servers appear once with a version picker that lets you pick which server to play from.
- **All HTTP through main process** — The renderer never makes direct HTTP requests. All Emby API calls go through IPC to the main process, avoiding CORS and keeping tokens secure.

## Tech Stack

| Component | Technology |
|-----------|------------|
| Shell | Electron 33 |
| UI | React 19 + TypeScript |
| Build | electron-vite + Vite 7 |
| State | Zustand |
| Styling | CSS Modules |
| Icons | Lucide React |
| Database | better-sqlite3 (WAL mode) |
| Settings | electron-store |
| Auto-update | electron-updater |
| Video | mpv (bundled) |
| Player UI | ModernZ OSC |
| Subtitle search | OpenSubtitles (via `open-subtitles.lua`) |
| Installer | electron-builder (NSIS) |

## Roadmap

### v3 (planned)
- [ ] Trakt.tv integration (scrobbling + collection sync)
- [ ] Trailer playback in detail pages
- [ ] Smart collections (rules-based grouping)
- [ ] Watch party — Cloudflare Tunnel + HLS for low-friction co-viewing

### v4 (planned)
- [ ] Native shell rewrite — .NET + WebView2 + libmpv (drop Electron + mpv subprocess)
- [ ] Mica + acrylic effects on Windows 11
- [ ] In-process video composition (no more window-swap hack)

## Contributing

Contributions are welcome! Please open an issue first to discuss what you'd like to change.

## License

[MIT](LICENSE)

## Acknowledgments

- [mpv](https://mpv.io) — the best video player engine
- [ModernZ](https://github.com/Samillion/ModernZ) — beautiful mpv OSC
- [OpenSubtitles](https://www.opensubtitles.com) — subtitle database powering in-player search
- [Emby](https://emby.media) — media server
- [better-sqlite3](https://github.com/WiseLibs/better-sqlite3) — synchronous SQLite that makes the cache layer possible
- [electron-updater](https://www.electron.build/auto-update) — auto-update plumbing
- [Electron](https://www.electronjs.org) — desktop app framework
- [Plex](https://plex.tv) — UI inspiration

---

<div align="center">
<sub>Co-authored with <a href="https://claude.ai">Claude</a> (Anthropic)</sub>
</div>
