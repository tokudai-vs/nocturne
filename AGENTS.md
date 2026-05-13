# AGENTS.md

Operating guide for AI coding agents working in this repo. Mirrors the same content as `CLAUDE.md` so that any agent platform (Claude Code, generic OpenAI-style tools, etc.) finds it under the conventional filename.

The full project tour lives in `CLAUDE.md`. This file adds two sections that capture the failure modes we've already paid for and don't want to pay for again.

---

## Pre-release smoke test

**Required** before claiming any feature "shipped" that touches main-process code or IPC. Must be run on Windows after `npm run package:win` + install over the current build:

1. **Fresh boot** — home page loads with all rows (Continue Watching, Latest Movies, Latest Episodes, Hero banner, Trakt Watchlist if connected). NO row should be silently missing.
2. **Open any library** (Movies, Shows, virtual library) — items appear with posters loading.
3. **Open `/analytics`** — page renders in Local mode + Last 30 days. Switch the source selector to Trakt and Combined; each renders an explicit state (stat cards, empty-state, or error card — never a silently blank body). Switch the range to 90 days / 1 year / All time; heatmap + bar chart re-render proportionally.
4. **Open any movie detail page** — hero backdrop + poster + Watch Trailer button + Bookmark (Trakt watchlist toggle) all render. Trakt rating chip appears when connected.
5. **Open any episode detail page** — hero + S/E label + adjacent episode navigation appears.
6. **Play 30s of any episode** — fade-to-black → mpv fullscreen → scrobble appears in main log → `ESC` returns to detail page.
7. **Episode navigation in player** — press `>` mid-playback; advance to next episode without process restart. Press `<`; previous episode loads. EOF: let an episode end; auto-advance to the next.
8. **Skip prompts** — play an episode with intro / credits / recap data in TheIntroDB; verify the amber skip button appears at the segment, click it (or Enter), playback jumps past. Seek back before the segment; prompt re-arms.
9. **Image fallback** — browse a dedup-grouped item whose primary server has a broken image; the card swaps to a sibling's poster instead of showing the letter placeholder.
10. **Inspect the main-process log** for the following confirmations:
    - `[image-fallback] built …` appears at least once after browsing a dedup-group item
    - `[analytics:get-stats] …` appears when `/analytics` is opened
    - `[trakt-scrobbler]` lines on `start` / `stop` (and on `pause` if you paused mid-playback)
    - **No** `Cannot find module` errors
    - **No** `UnhandledPromiseRejection` at startup

Any failure of items 1–10 blocks the release. Document the failure and fix before re-running.

---

## Bundling traps — avoid dynamic `require()`

Electron-vite bundles only what it can statically analyse. A `require('./module')` call inside a function body or conditional is **NOT** traced; the referenced file gets excluded from `out/main/index.js` even though the source still compiles cleanly. At runtime the IPC handler throws `Cannot find module './foo'` and the renderer renders empty.

Two regressions in v3 (`image-fallback` + `analytics`) both stemmed from this. The renderer saw `{ success: false, error: "Cannot find module './foo'" }` from every wrapped IPC handler and rendered empty arrays. Detail pages worked only because they fell through to an alternate IPC path; library/home pages didn't have a fallback.

### Rules

- All imports of internal modules in `src/main/` MUST be at the top of the file as static `import { ... } from './foo';`
- Never use `const { x } = require('./foo')` for a relative path
- Importing built-ins (e.g. `require('node:fs')`, `require('electron')`) is fine — only **relative-path** requires are dangerous

### Guards in place

Two scripts enforce this, wired into the npm workflow:

- **`scripts/check-relative-requires.js`** — regex scan of `src/main`, `src/preload`, `src/renderer`, `src/shared`. Fails the build if it finds `require('./…')`. Wired into `prebuild` and the `lint` script. To grant an intentional exception, append a line comment ending in `// eslint-allow-relative-require: <reason>`.
- **`scripts/check-bundling.js`** — post-build scan. For every `src/main/*.ts` with non-type exports, grep `out/main/index.js` for at least one exported symbol. If a whole module's symbols are missing, the build fails — the module wasn't bundled. Wired into `postbuild` so it runs automatically after `electron-vite build`.

### Debugging "Cannot find module" at runtime

Grep the source for the missing module name. The fix is always: add a static `import` to a file that's already reachable from the main entry. The two guard scripts catch this before the installer ships; if you bypass them, see the smoke test above.

---

## Cross-reference

Everything else — repo layout, IPC patterns, mpv integration, SQLite schema, build commands, code conventions — is in `CLAUDE.md`. Read it first.
