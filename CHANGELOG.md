# Changelog

## v1.0.0 (2026-04-10)

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
