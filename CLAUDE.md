# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Nocturne

A desktop Emby media server client built with Electron + React + TypeScript. Uses mpv as an external video player process. Windows-only target.

## Commands

- `npm start` — dev mode (electron-vite dev)
- `npm run build` — production build (electron-vite build)
- `npm run package:win` — build + create Windows NSIS installer
- `npm run lint` — ESLint across src/
- `npm run setup` — one-command dev setup (installs deps, downloads mpv + ModernZ, mirrors `build/mpv/portable_config` into `resources/`, generates icons)

Individual setup steps: `npm run download-mpv`, `npm run download-modernz`, `npm run mirror-mpv-config`, `npm run generate-icons`

`mirror-mpv-config` runs automatically as `prebuild` so production builds always ship the latest portable_config (including custom Lua scripts).

## Architecture

Three Electron processes with strict boundaries:

**Main process** (`src/main/`) — all network I/O, database, mpv control, settings. The renderer NEVER makes direct HTTP requests; everything goes through IPC.

**Preload** (`src/preload/index.ts`) — contextBridge exposing `window.api` with typed IPC wrappers. This is the single contract between main and renderer.

**Renderer** (`src/renderer/`) — React 19 SPA with hash router, Zustand stores, CSS Modules.

### IPC pattern

All IPC handlers are registered in `src/main/ipc-handlers.ts`. Every handler returns `{ success: true, data }` or `{ success: false, error }` via the `ok()`/`fail()` helpers. The preload bridge in `src/preload/index.ts` maps these to `window.api.*` calls. When adding new IPC channels, update all three files: handler, preload bridge, and renderer types.

### mpv integration

mpv runs as an external process in idle mode (started once at app launch via `MpvManager.startIdle()`). Communication uses JSON IPC over a Windows named pipe (`\\.\pipe\nocturne-mpv-{pid}`). Playback is triggered by sending `loadfile` commands — no process restart needed.

During playback: main window hides, mpv goes fullscreen. On playback end: main window shows first (with black overlay), then mpv window hides behind it. This creates smooth fade transitions.

Key file: `src/main/mpv-manager.ts`. mpv config lives in `build/mpv/portable_config/`. Custom Lua scripts: `nocturne_select.lua` (anchored audio/sub/playlist menus), `open-subtitles.lua` (in-player OpenSubtitles search bound to `b`).

### Local data layer

- **SQLite** (`src/main/database.ts`) — better-sqlite3 with WAL mode. Stores cached Emby items (per-server), dedup groups, sync state/checkpoints, image cache metadata. DB at `{userData}/nocturne.db`.
- **Sync engine** (`src/main/sync-engine.ts`) — EventEmitter state machine: `idle → discovering → fetching (batched) → enriching → deduping → done`, with checkpoint rows so an interrupted sync resumes mid-batch instead of restarting. Emits progress events the renderer subscribes to via `sync-store`.
- **Dedup engine** (`src/main/dedup-engine.ts`) — pipeline runs after every sync. Match order: **TMDB ID → IMDB ID → Name+Year**. Operates across libraries AND across servers when combined mode is on. Episode-level dedup matches by series + season/episode index. Persisted to `dedup_groups`; every list query joins through it.
- **Virtual libraries** (`src/main/virtual-library.ts`) — combine multiple Emby libraries (and, in combined mode, libraries from multiple servers) into unified views. First-launch wizard auto-suggests groupings (e.g., `Movies` + `Movies 4K`).
- **Image cache** (`src/main/image-cache.ts`) — downloads and caches images locally with LRU eviction and configurable size limit.
- **Server manager** (`src/main/server-manager.ts`) — multi-server registry. Tracks online/offline status per server. **Combined mode** (gated on 2+ servers) routes virtual library queries across all enabled servers; the dedup engine then collapses cross-server duplicates so the same movie on two servers shows up once with a multi-source version picker.
- **Settings** (`src/main/settings.ts`) — electron-store. Holds power mode (`performance` / `balanced` / `efficiency` — controls sync concurrency + image prefetch), preferred quality, subtitle appearance, image cache size limit.
- **Updater** (`src/main/updater.ts`) — electron-updater wired to GitHub Releases; emits download progress + restart prompt to renderer.

### Renderer state

Zustand stores in `src/renderer/stores/`:
- `auth-store` — login, session restore, server switching
- `library-store` — library data, resume, next up, heroes
- `player-store` — playback state, transition animations
- `sync-store` — sync progress tracking
- `settings-store` — user preferences
- `ui-store` — sidebar, search, navigation
- `toast-store` / `context-menu-store` / `app-store` — UI utilities

### Renderer pages and routing

Hash router defined in `App.tsx`. Pages: `HomePage`, `LibraryPage`, `DetailPage`, `SearchPage`, `SettingsPage`, `LoginPage`. Protected routes wrap authenticated pages via `ProtectedRoute`.

### Styling

CSS Modules (`.module.css` files co-located with components). Dark theme with amber accents. No CSS framework.

## Build configuration

- `electron-vite` config in `electron.vite.config.ts` — separate configs for main, preload, renderer
- Path aliases: `@renderer` -> `src/renderer`, `@shared` -> `src/shared`
- electron-builder config in `electron-builder.yml` — NSIS installer, mpv bundled as extraResource
- TypeScript: `tsconfig.json` (base), `tsconfig.node.json` (main/preload), `tsconfig.web.json` (renderer)

## Key conventions

- All Emby API communication funnels through `src/main/emby-client.ts` (singleton `embyClient`). The renderer API layer (`src/renderer/api/`) provides typed wrappers but all calls still go through IPC.
- Cross-server operations (mark played, favorites, playback report) check `serverId` and route through `serverManager` in IPC handlers — never assume a single active server.
- Cache-first reads: every list page should hit SQLite first (`databaseManager`/virtual-library queries) and only call Emby for enrichment or when cache is cold.
- Playback position uses Emby's tick format (1 second = 10,000,000 ticks).
- The `window.api` type is declared in `src/renderer/env.d.ts`.

# Using Gemini CLI for Large Codebase Analysis

When analyzing large codebases or multiple files that might exceed context limits, use the Gemini CLI with its massive
context window. Use `gemini -p` to leverage Google Gemini's large context capacity.

## File and Directory Inclusion Syntax

Use the `@` syntax to include files and directories in your Gemini prompts. The paths should be relative to WHERE you run the
  gemini command:

### Examples:

**Single file analysis:**
gemini -p "@src/main.py Explain this file's purpose and structure"

Multiple files:
gemini -p "@package.json @src/index.js Analyze the dependencies used in the code"

Entire directory:
gemini -p "@src/ Summarize the architecture of this codebase"

Multiple directories:
gemini -p "@src/ @tests/ Analyze test coverage for the source code"

Current directory and subdirectories:
gemini -p "@./ Give me an overview of this entire project"

# Or use --all_files flag:
gemini --all_files -p "Analyze the project structure and dependencies"

Implementation Verification Examples

Check if a feature is implemented:
gemini -p "@src/ @lib/ Has dark mode been implemented in this codebase? Show me the relevant files and functions"

Verify authentication implementation:
gemini -p "@src/ @middleware/ Is JWT authentication implemented? List all auth-related endpoints and middleware"

Check for specific patterns:
gemini -p "@src/ Are there any React hooks that handle WebSocket connections? List them with file paths"

Verify error handling:
gemini -p "@src/ @api/ Is proper error handling implemented for all API endpoints? Show examples of try-catch blocks"

Check for rate limiting:
gemini -p "@backend/ @middleware/ Is rate limiting implemented for the API? Show the implementation details"

Verify caching strategy:
gemini -p "@src/ @lib/ @services/ Is Redis caching implemented? List all cache-related functions and their usage"

Check for specific security measures:
gemini -p "@src/ @api/ Are SQL injection protections implemented? Show how user inputs are sanitized"

Verify test coverage for features:
gemini -p "@src/payment/ @tests/ Is the payment processing module fully tested? List all test cases"

When to Use Gemini CLI

Use gemini -p when:
- Analyzing entire codebases or large directories
- Comparing multiple large files
- Need to understand project-wide patterns or architecture
- Current context window is insufficient for the task
- Working with files totaling more than 100KB
- Verifying if specific features, patterns, or security measures are implemented
- Checking for the presence of certain coding patterns across the entire codebase

Important Notes

- Paths in @ syntax are relative to your current working directory when invoking gemini
- The CLI will include file contents directly in the context
- No need for --yolo flag for read-only analysis
- Gemini's context window can handle entire codebases that would overflow Claude's context
- When checking implementations, be specific about what you're looking for to get accurate results