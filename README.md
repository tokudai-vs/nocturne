<div align="center">

# :movie_camera: Nocturne

**A modern, cinematic desktop client for Emby media servers**

*A personal project born out of frustration — none of the existing Emby clients looked or felt good enough. Nocturne is my attempt at building the Emby experience I actually wanted: a Plex-like browsing UI with full codec playback support.*

*This is not a production application. It's a personal tool that I'm sharing in case others find it useful or want to build on it. Expect rough edges.*

[Download](#installation) · [Features](#features) · [Screenshots](#screenshots) · [Building](#building-from-source) · [Contributing](#contributing)

---

</div>

## Features

### :art: Cinematic Browsing UI
- **Plex-inspired dark theme** with amber accents and smooth animations
- **Hero banners** with auto-rotating featured content on the home screen
- **Poster grids** with hover effects (scale, glow, info overlay)
- **Detail pages** for movies, series, and episodes with backdrop art, cast, and media info
- **Sidebar overlay** navigation with gradient fade effect
- **Real-time search** with instant dropdown preview

### :tv: Powerful Playback
- **Full codec support** — HEVC/H.265, H.264, DTS, TrueHD, Atmos, AAC, HDR10, Dolby Vision
- **Direct play** — no server-side transcoding needed
- **Instant playback** — mpv runs in idle mode, zero startup delay
- **ModernZ controls** — modern, interactive on-screen controller with amber theme
- **Resume playback** — picks up exactly where you left off
- **Smooth transitions** — fade-to-black between browsing and playback

### :link: Emby Integration
- Browse all libraries (movies, TV shows, collections, etc.)
- Continue Watching and Next Up on home screen
- Search across all content types
- Playback progress synced to Emby server in real-time
- Watch status tracking (played, favorite, etc.)
- Session persistence — auto-login on restart

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
| `M` | Toggle mute |
| `F` | Toggle fullscreen |
| `ESC` / `Q` | Stop playback |

## Screenshots

> *Screenshots coming soon — see the [releases page](../../releases) for the latest build.*

<!-- 
Uncomment and add screenshots:
### Home Screen
![Home](docs/screenshots/home.png)

### Movie Detail
![Detail](docs/screenshots/detail.png)

### Library Browse
![Library](docs/screenshots/library.png)

### Player (ModernZ)
![Player](docs/screenshots/player.png)
-->

## Installation

### Windows (Recommended)

1. Download the latest `Nocturne-Setup-x.x.x.exe` from the [Releases page](../../releases)
2. Run the installer
3. Launch Nocturne from the Start Menu or Desktop shortcut
4. Enter your Emby server address and sign in

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

# One-command setup (installs deps, downloads mpv + ModernZ, generates icons)
npm run setup

# Or do it manually:
npm install
npm run download-mpv
npm run download-modernz
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

The installer will be at `dist/Nocturne Setup x.x.x.exe`.

## Architecture

```
┌─────────────────────────────────────────┐
│           Electron Main Process         │
│  ┌─────────────┐  ┌─────────────────┐  │
│  │ EmbyClient  │  │  MpvManager     │  │
│  │ (REST API)  │  │  (JSON IPC)     │  │
│  └──────┬──────┘  └────────┬────────┘  │
│         │                  │            │
│    IPC Bridge (contextBridge)           │
│         │                  │            │
├─────────┴──────────────────┴────────────┤
│           Renderer Process              │
│  ┌──────────────────────────────────┐   │
│  │  React 19 + TypeScript           │   │
│  │  ┌──────┐ ┌───────┐ ┌────────┐  │   │
│  │  │Zustand│ │Router │ │CSS Mod │  │   │
│  │  └──────┘ └───────┘ └────────┘  │   │
│  └──────────────────────────────────┘   │
└─────────────────────────────────────────┘
         ↕ JSON IPC (named pipe)
┌─────────────────────────────────────────┐
│  mpv (bundled, idle mode)               │
│  + ModernZ OSC                          │
│  + portable_config                      │
└─────────────────────────────────────────┘
```

### Key Design Decisions

- **mpv as external process** — Electron's Chromium compositor cannot embed mpv video. mpv runs as a separate fullscreen window with the main window hidden during playback. Smooth fade transitions mask the window swap.
- **Idle mode** — mpv starts once on app launch and stays running. Playback is triggered via `loadfile` IPC command — instant, no process startup delay.
- **ModernZ OSC** — Community-built modern on-screen controller for mpv. Fully interactive with clickable buttons, draggable seek bar, and subtitle/audio menus.
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
| Video | mpv (bundled) |
| Player UI | ModernZ OSC |
| Installer | electron-builder (NSIS) |

## Roadmap (v2)

- [ ] Library deduplication (merge 4K + HD copies of same movie)
- [ ] Virtual library mapping (combine multiple Emby libraries into one view)
- [ ] Multi-server support
- [ ] Settings page with playback preferences
- [ ] Auto-update via electron-updater
- [ ] Subtitle download integration
- [ ] Watch party / sync playback

## Contributing

Contributions are welcome! Please open an issue first to discuss what you'd like to change.

## License

[MIT](LICENSE)

## Acknowledgments

- [mpv](https://mpv.io) — the best video player engine
- [ModernZ](https://github.com/Samillion/ModernZ) — beautiful mpv OSC
- [Emby](https://emby.media) — media server
- [Electron](https://www.electronjs.org) — desktop app framework
- [Plex](https://plex.tv) — UI inspiration

---

<div align="center">
<sub>Co-authored with <a href="https://claude.ai">Claude</a> (Anthropic)</sub>
</div>
