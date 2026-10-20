<div align="center">

# :movie_camera: Nocturne v4.1

**A modern, cinematic native Windows desktop client for Emby — powered by .NET 8, WPF-UI, and mpv.**

*Zero Electron. Zero Chromium. Built from the ground up for Windows 11 with hardware-accelerated Mica, Acrylic, multi-server support, instant browsing from a local SQLite cache, and cross-server deduplication.*

[Features](#features) · [Screenshots](#screenshots) · [Building from Source](#building-from-source) · [Testing](#testing) · [License](#license)

---

</div>

## What's New in v4.1

Nocturne v4.1 introduces major player polish, window cohesion, and pipeline performance:
- **Media Player Close Button & Window Cohesion**: Dedicated close button (`Dismiss24`) in the player top overlay bar, paired with reliable Win32 foreground focus restoration so Nocturne always returns to the front when closing the player.
- **Netflix-Style Timeline Scrubbing Thumbnails**: 16:9 interactive chapter and preview thumbnail popup hovering above the timeline scrubber with drop shadow, amber border, chapter title, and timestamp.
- **Subtitle Border Customization**: New setting in Settings → Playback allowing users to choose subtitle border size (`None` by default, `Thin`, `Medium`, `Thick`), dynamically passed to mpv (`--sub-border-size`).
- **Timeline Progress Bar Reset**: Clean reset of timeline sliders and counters to 0:00 immediately upon advancing or switching episodes.
- **Preserved Window Rounded Corners**: Windows 11 DWM rounded corners (`DWMWCP_ROUND`) are now retained during windowed media playback.
- **Sync Engine Pipeline Overhaul**: High-throughput bounded pipeline with batched atomic SQLite WAL commits and contiguous low-water-mark checkpoints.
- **YouTube Trailer Playback & Watch Party**: Direct trailer playback integration from detail pages and native watch party synchronization.

---

## Features

### :zap: Cache & Performance
- **SQLite-backed local cache** (`Microsoft.Data.Sqlite`, WAL mode) — tested with libraries of 1.5M+ items.
- **Instant browse from cache** — every page loads from local disk in sub-milliseconds, then revalidates in the background.
- **Background sync engine** with batched fetching and checkpointed state to resume gracefully across app restarts.
- **Disk-backed image cache** with LRU eviction and configurable size limits.

### :satellite: Multi-Server Architecture & Authentication
- **Emby Connect & PIN Authorization** — Sign in instantly via `emby.media/pin` with zero local password requirements (ideal for accounts configured via Emby Connect without a local password).
- **Multi-Strategy Authentication** — Resilient login pipeline supporting JSON, form-encoded, and passwordless accounts with automatic RFC header negotiation.
- Add multiple Emby servers and switch seamlessly between them from the user menu.
- **Separate or combined library mode** — view each server independently or merge libraries across servers into a unified shelf.
- **6-pass deduplication pipeline** — matches duplicate titles by TMDB ID → IMDB ID → Name+Year across servers and folders.
- **Version picker on detail pages** — shows quality, resolution (4K UHD / 1080p), codec (HEVC / H.264 / AV1), bitrate, and server source per copy.

### :art: Windows 11 Design & Personalization
- **Windows 11 DWM Composition**: Native Mica, Acrylic, and Tabbed backdrops.
- **Live Theme Accent Color Selection**: Customize buttons, highlights, glowing rings, and progress indicators:
  - Signature Amber (`#E5A00D`)
  - Electric Blue (`#0078D4`)
  - Ultraviolet (`#8A2BE2`)
  - Emerald Green (`#107C41`)
  - Crimson Rose (`#E81123`)
  - Sunset Orange (`#FF8C00`)
  - Cyber Cyan (`#00B7C3`)
- **Cinematic Shell**: Obsidian dark theme or clean light mode with adaptive glowing splash screen, hero rotating backdrops, and smooth animations.
- **Plex & Netflix Detail Presentation**: Smooth vertical opacity gradient fade into theme background, high-contrast typography, official content ratings, resolution/codec badges, genre pills, circular cast & crew avatars with floating horizontal scroll buttons, and cohesive light/dark mode support.
- **TV Episodes with Played Tracking**: 16:9 episode thumbnails with watched checkmark badges, in-progress resume progress bars, and 1-click watched/unwatched toggles.
- **Smooth Horizontal Navigation**: Hidden scrollbars with hover-activated floating chevron scroll buttons (`<` and `>`) across all content rows and cast carousels.
- **Right-Click Context Menus**: Play, mark as watched/unwatched, and toggle favorites directly from cards.

### :tv: Video Playback Engine (Embedded mpv)
- **Embedded Win32 HWND Player**: Embedded directly within Nocturne's main window — zero separate processes or lost focus.
- **Modern Floating Player HUD**: Sleek translucent overlay that appears on mouse activity and auto-hides after 2.5s of inactivity.
  - Interactive timeline scrubber with live timestamp formatting (`hh:mm:ss`) and clean progress reset across tracks.
  - **Netflix-Style Timeline Scrubbing Thumbnails**: 16:9 chapter images, titles, and timestamps display smoothly on hover/seek.
  - Play, Pause, -10s rewind, +10s fast-forward, volume control, and Fullscreen toggle.
  - Floating top bar with title, subtitle, episode jump, back button, and dedicated close button (`Dismiss24`).
  - **Seamless Window Cohesion**: Immediate foreground focus restoration prevents the app from being dropped to the background on exit.
- **Direct play**: No server transcoding needed; native support for 4K UHD, HDR10, Dolby Vision (P5/P8 tone-mapped), HEVC, AV1, DTS-HD, and TrueHD Atmos.
- **Customizable Subtitle Borders**: Configure subtitle outline thickness (`None`, `Thin`, `Medium`, `Thick`) in Settings.
- **Windowed Rounded Corners**: Windows 11 DWM rounded corners are preserved during windowed playback.
- **In-player navigation**:
  - `>` / `N`: Jump to next episode without interrupting playback.
  - `<` / `P`: Jump to previous episode.
  - `EOF`: Automatically advance to next episode when playback finishes.
- **TheIntroDB skip segments**: Amber HUD prompt and `Enter` key shortcut to skip intros, recaps, and credits. Re-arms on backward seek.

### :clapper: Trakt.tv Integration
- **OAuth Device Flow**: 1-click activation via `trakt.tv/activate`.
- **Real-time scrobbler**: Automatically scrobbles `start`, `pause`, and `stop`. Progress >= 80% automatically marks title as watched.
- **Bidirectional watch state sync**: Keeps local Emby watch status in sync with Trakt.
- **Trakt Watchlist virtual library**: Appears as a dedicated shelf in the library view with 1-click bookmarking on detail pages.

### :bar_chart: Watch History Analytics (`/analytics`)
- **Three data sources**: Local (Emby SQLite cache), Trakt (mirrored history), Combined (union).
- **Time range selector**: Last 30 Days, Last 90 Days, Last Year, All Time.
- **Stat cards**: Total watch time, weekly average hours, movies watched, episodes watched, and currently-watching series.
- **Trakt Lifetime block**: Lifetime movies, episodes, hours, and distinct shows from Trakt `/users/me/stats`.
- **GitHub-style activity heatmap**: Range-adaptive grid with 5 amber quantile intensity levels.
- **Watch frequency bar chart**: Visualizes daily and weekly viewing hours.
- **Top series, top movies & genre shares**: Detailed breakdowns of viewing habits.

---

## Building from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Node.js (v18+) for packaging automation

### Build Commands
```bash
# Clone the repository
git clone https://github.com/tokudai-vs/Nocturne.git
cd Nocturne

# Build the native solution
dotnet build src-native/Nocturne.sln

# Run all unit tests
dotnet test src-native/Nocturne.Tests/Nocturne.Tests.csproj

# Compile the standalone setup installer
node scripts/build-installer.js
```

The generated installer will be output directly to `dist/Nocturne-v4.1.0-Setup.exe`.

---

## Testing

Nocturne includes unit tests for core modules:
```bash
dotnet test src-native/Nocturne.Tests/Nocturne.Tests.csproj
```
Tests cover:
- URL normalization & Emby ServiceStack PascalCase serialization.
- 6-pass deduplication matching pipeline & primary item resolution scoring.
- TheIntroDB skip segment detection, re-arm logic, and action labels.
- Theme accent color palette validation.

---

## License

MIT © Vishal ([@tokudai-vs](https://github.com/tokudai-vs))
