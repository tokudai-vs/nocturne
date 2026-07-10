# Nocturne cross-server correctness audit — July 2026

**Scope.** End-to-end code audit of the "active-server assumption" bug class: code paths
that resolve an item, stream URL, image, playback report, watched-state write, or
user-data mutation against `embyClient` (the active client) when the data actually lives
on another server. Trigger: three sibling bugs (solo cross-server play — fixed in
`94dbd25`; episode-nav reporting — fixed here; Continue Watching — root-caused and fixed
here). Setup that exposes all of them: **combined mode with two servers**
(Valkyrie `68c856f5` / Raptor `3424343e`).

**Method.** Full code audit performed in WSL (the app is Windows-only and was NOT run).
Everything below is verified **by reading code** unless explicitly marked
*runtime-confirm*. Section 5c lists the exact Windows steps needed to confirm the
runtime-only items. Type-check (`tsc --noEmit`, both configs) and the
`check-relative-requires` guard pass after the fixes. `npm run build` (and therefore
the `check-bundling` post-build guard) cannot run in WSL — node_modules holds the
Windows rollup native binary — so **run the build + guard on Windows first** (it's
step 0 of §5c anyway).

Baseline for this audit: `94dbd25` (cross-server solo-play fix) on `main`.
Note: the `v3.5.0` tag still points at `6c413bd`, behind both `5a66f2c` (guest error
reporting) and these fixes — it must be moved before release (not moved in this audit).

---

## 1. Domain 1 — Continue Watching (root cause)

### How the row is actually built

- Combined mode + `syncStatus === 'complete'` → **the SQLite cache is authoritative**:
  `library-store.ts:fetchResume` → `cache:get-resume-items` →
  `getResumeItemsDeduped()` (`src/main/database.ts`).
- Combined mode + sync incomplete → live fan-out `libraries:get-all-servers-resume`
  (per-server `/Items/Resume`, per-server errors collected).
- Separate mode → cache first, then active-server `/Items/Resume`.

So in steady state the row is a pure cache query — and the cache's
`playback_position_ticks` was **never written by any playback path**. That is the
central defect; everything else compounds it.

### Root causes (each verified in code)

| # | Defect | Where (pre-fix) | Produces symptom |
|---|--------|-----------------|------------------|
| RC1 | No playback path wrote `playback_position_ticks` to the cache. Every stop handler wrote only `last_played_date`; position only ever changed via sync or mark-played (which zeroes it). | `ipc-handlers.ts` end-file + eof-reached handlers; `advanceToEpisode` (wrote nothing at all); `watchparty-session.ts:stopHistoryReporting` | **Missing items** (stop a movie halfway → `ticks = 0` in cache → fails `WHERE playback_position_ticks > 0` until the next incremental sync). **Stale items** (finish a movie → cache keeps old `ticks > 0` → item lingers). |
| RC2 | Row ordered by `cached_at DESC` — that's *sync* recency, not *play* recency. Every sync rewrites `cached_at`, so after any sync the ordering is effectively "whatever synced last". Ghost rows could crowd genuine ones out of the 12-row limit. | `database.ts:getResumeItemsDeduped` | **Wrong/unexpected items**, wrong order. |
| RC3 | The sync resume-refresh **upserted items into the wrong library**: `upsertItems(resumeResult.Items, serverId, libraries[0]?.Id \|\| 'unknown', …)` reassigned every resumable item's `library_id` to the server's *first* library. Corrupts virtual-library membership (items leak into the wrong shelf or vanish from their own) and perturbs dedup's `COUNT(DISTINCT library_id)` duplicate scan. | `sync-engine.ts` — 4 call sites (full sync ×2, incremental ×2) | **Wrong/unexpected items**; second-server items vanishing from their shelves. |
| RC4 | Combined-mode incremental sync **advanced `lastFullSync` even when a server failed** (per-server try/catch swallows, timestamp written unconditionally). After an outage window, everything that changed on the failed server — new items *and* watch state — is filtered out forever by `MinDateLastSaved` until a manual full sync. | `sync-engine.ts:incrementalSyncAllServers` | **Second-server items missing** (both new items and resume state). |
| RC5 | Clicking a Continue Watching card (and the hero Play button) called `play(item)` with **no serverId** → playback-info resolved against the active server → silent failure for every second-server item. | `HomePage.tsx:253–259` | Second-server items **look broken even when listed**. |
| RC6 | The unconditional **mark-played cascade on every stop**: in combined mode, any playback stop (even a 2-minute abandon) marked all dedup-sibling copies on other servers *played*. This corrupted server-side resume state on the other server and set `played` flags that fed back through sync. | `ipc-handlers.ts` end-file + eof-reached handlers; `watchparty-session.ts:stopHistoryReporting` | **Wrong/unexpected items** + items disappearing from resume on the second server. |
| RC7 | Auto-advance (binge path) skipped everything: the old episode's stop was reported to the **active** server (404 for foreign episodes), and no cache update or cascade ran at all. | `ipc-handlers.ts:advanceToEpisode` | Binged episodes keep stale resume state; cross-server binge progress lost entirely. |
| RC8 | Ghost reconciliation gap: the resume-refresh can only *add/update* items currently in `/Items/Resume` (Limit 12). It can never *clear* an item that left the server's resume list (finished on another client, cleared server-side). Incremental library sync can't catch it either — `MinDateLastSaved` does not reliably change on user-data-only changes (*runtime-confirm*: verify against your Emby version). | `sync-engine.ts` resume-refresh phases | **Stale items** persist until a full sync happens to re-fetch that row. |

Dedup substitution (the "is a dedup-primary substituted for the in-progress copy?"
question): **no** — `getResumeItemsDeduped` deliberately picks the group member with MAX
`playback_position_ticks`, not the primary. That logic is correct; it only misfired
because the ticks themselves were stale (RC1/RC8). With RC1 fixed and played rows
excluded from the MAX (see below), it now selects the genuinely in-progress copy.

### Fixes applied (commit accompanying this document)

- **`recordPlaybackStopInCache()`** (`ipc-handlers.ts`): every solo stop path (ESC/quit,
  natural EOF, episode advance) now mirrors Emby's server-side resume thresholds into
  the cache — `< 5%` → position cleared; `5–90%` → position + played-percentage stored;
  `≥ 90%` → `played = 1`, position cleared. The next sync still reconciles to server
  truth; this just removes the window where the cache lies.
- **`cascadeMarkPlayedIfWatched()`** (`ipc-handlers.ts`): the cross-server mark-played
  cascade now fires **only when the stop actually finished the item (≥ 90%)**, and also
  updates the sibling rows in the local cache. Same gate applied to the Watch Party
  cascade (`watchparty-session.ts`).
- **`advanceToEpisode`**: old episode's stop is reported to the server that owns it
  (`foreignServerFor(oldSession.serverId)`); the new episode's start is reported to
  `targetServer` (whose stream URL it already used); cache update + gated cascade added.
  This closes the known-unfixed episode-nav bug.
- **`getResumeItemsDeduped`**: `AND played = 0` on both the outer filter and the group-MAX
  subquery (a played sibling with stale ticks can no longer shadow the in-progress
  copy), and ordering changed to `COALESCE(last_played_date, cached_at) DESC`.
- **`upsertItemsPreservingLibrary()`** (`database.ts`): resume-refresh now preserves an
  existing row's `library_id`/`library_name`; the fallback library is only used for rows
  not yet in the cache. All 4 sync-engine call sites switched.
- **Incremental watermark**: `incrementalSyncAllServers` no longer advances
  `lastFullSync` when any server failed, so the next run re-covers the gap.
- **HomePage**: Continue Watching clicks and the hero Play button pass `item.serverId`
  through `play()`.
- **Watch Party**: `reportProgressOnce`/`stopHistoryReporting` now write the position
  (not just `last_played_date`) into the cache, so a party shows up in Continue
  Watching like solo playback does.

### Still open for Domain 1 (design decision — see §5b)

RC8 (ghost reconciliation for *externally* finished items) is only partially covered:
finishing something in Nocturne now clears it locally, but finishing it on another
client (phone/TV) leaves a ghost until a full sync. Options in §5b.

---

## 2. Cross-server assumption inventory (whole app)

Every `embyClient` (active-client) usage was reviewed. Status after this audit:

### Fixed here

| Path | Defect | Fix |
|------|--------|-----|
| **Images — the big one.** `buildImageUrl`/`buildCachedItemImageUrl` (`renderer/utils/image-url.ts`) built *every* image URL from the active server's URL + token (`useAuthStore`). Every non-active-server item's primary image 404'd **by construction**; the dedup fallback chain (built server-correctly in `image-fallbacks.ts`) was silently compensating for grouped items; ungrouped foreign items fell to the letter placeholder. This answers "are the 500/404 fallbacks a server-resolution issue?" — **yes, largely.** | Wrong-server URL for all foreign items | `image-url.ts` now resolves per `item.serverId` via a serverId→{url,token} map (refreshed from `servers:get-all` on login/restore/switch). `MediaCard`, `HeroBackdrop`, `HomePage` hero, `LibraryPage` list thumbs, `DetailPage` poster/episode thumbs pass the owning serverId. |
| DetailPage enrichment (`library.getItem`), seasons, episodes, similar | All active-server; a **foreign series detail page silently rendered no seasons/episodes** (dedup primaries are chosen by metadata quality, not server, so this hits ordinary browsing in combined mode) | New `getItemForServer` / `getSeasonsForServer` / `getEpisodesForServer` / `getSimilarForServer` in `emby-client.ts`; serverId threaded through the 4 IPC handlers + preload + types; DetailPage resolves the owning server from the cache row and the active version pill |
| DetailPage mark played / unplayed / drift-sync | `item:mark-played` called without `serverId` → foreign items 404'd silently (UI showed optimistic state) | serverId passed from the cached row |
| DetailPage favorite toggle | Used the **legacy** `emby:user:favorite` handler — active-server-only *and* no local cache update | Switched to cross-server-aware `item:toggle-favorite` with serverId |
| Episode-nav start/stop reporting + auto-advance bookkeeping | See RC7 | See §1 |
| Continue Watching / hero click-through | See RC5 | See §1 |

### Verified correct (no change)

- `item:mark-played` / `item:mark-unplayed` / `item:toggle-favorite` handlers — route via
  `serverManager.getServer(serverId)` and update the cache. ContextMenu passes
  `item.serverId` on all actions.
- `item:remove-from-continue` — clears across the whole dedup group, per-owning-server.
- `player:play` + `PlaybackSession` — cross-server stream + start/progress/stop
  (the `94dbd25` fix), session serverId derived from the cache row.
- Sync engine server iteration — uses push/popContext around per-server client mutation;
  full sync marks `partial` and keeps checkpoints when a server fails.
- Trakt: scrobbler resolves ids from the local cache (server-agnostic);
  `trakt-sync.applyWatchedState` pushes to each item's home server;
  `pushHistoryAdd/Remove` are cache-based. Watchlist matcher is cache-based.
- Watch Party source resolution + history reporting target — resolved against the
  picked version's server from the start (see §3).
- `libraries:get-all-servers-*` handlers — per-server standalone calls with error
  bundles.

### Known remaining active-server paths (acceptable or design decisions — §5b)

- `cache:get-item` fallback → `embyClient.getItem` (only hit when the row isn't cached;
  foreign uncached ids will fail — rare, self-heals after sync).
- TopBar search dropdown uses `emby:search:query` (active server only). SearchPage
  covers foreign titles via the deduped cache search; the dropdown does not.
- `emby:library:get-nextup` — active server only; combined mode hides the Next Up row,
  so this is consistent (a combined Next Up is a feature request, not a bug).
- Legacy handlers `emby:user:mark-played/mark-unplayed/favorite` and
  `emby:media:report-*` are now referenced only by the dead renderer API layer
  (`src/renderer/api/*`, `use-emby.ts` — no component imports them). Candidates for
  deletion; left in place to keep this change reviewable.
- `sync-engine.precacheHomepageImages` — active-server only by design (best-effort
  warm-up; combined full sync runs per-server anyway inside pushContext).

---

## 3. Domain 2 — Watch Party

### Verified correct by code audit

- **Cross-server source resolution**: `selectWatchPartySource` → owning server config →
  `getStreamUrlForServer`; tokens never leave main; the history target captures the
  *picked version's* server, not the active one.
- **Session lifecycle**: state machine transitions logged; `endSession` is re-entrant
  (single `endPromise` shared by End-click / error / app-quit races); teardown kills
  tunnel → transcoder → sync server → HTTP server → session dir, each step isolated;
  stale-session sweep on next start; `before-quit` awaits teardown before `app.exit()`.
- **Transcoder stop**: `q` on stdin (finalizes the playlist), SIGKILL after 5 s.
  **Tunnel stop**: kill, SIGKILL after 5 s. ffmpeg/cloudflared crash → session ends
  with error. No orphan path found in code *except* the quit-ordering bug below (fixed).
- **HTTP server**: strict `stream.m3u8|segment_%05d.ts` allowlist, guest page + bundled
  hls.js only, WS upgrade restricted to `/ws`, listens on 127.0.0.1.
- **Danger Zone gates in main**: CPU-only encoder — enforced in the session manager;
  4K output ceiling — enforced in the IPC handler (degrades to 1080); 4K source
  preference — read from settings in main.

### Broken / fixed here

| Finding | Detail | Status |
|---------|--------|--------|
| **Guest-cap unlock NOT re-enforced in main** | `watchparty:start-session` accepted any `maxGuests` (including `'unlimited'`) from the renderer without checking `watchPartyMaxGuestsUnlocked` — contradicting the "main never trusts the renderer on a safety check" rule the CPU gate follows. | **Fixed**: clamped to ≤ 10 unless the unlock is set (logged when clamped). |
| **Cap not actually enforced against connections** | `onCountChanged` only *logs* overflow; the sync server never refuses a socket. The code comment defers this ("piece-N polish"), but the CHANGELOG claims "Server-side guest-cap enforcement". | **Open — §5b** (either implement refusal or correct the CHANGELOG). |
| **Ending from the waiting room reported a stop** | `stopHistoryReporting` was gated on `historyTarget` (set during INITIALIZING), not on the show having started. Ending a WAITING session sent `reportPlaybackStopped` + Trakt `scrobble:stop` at the resume offset — a Resume start at ≥ 80% would credit a full watch on Trakt without a second played. The code comment claimed the opposite. | **Fixed**: `historyStarted` flag set by `startHistoryReporting`, checked in `doEndSession`. |
| **Quit-during-LIVE could orphan ffmpeg + cloudflared** | `before-quit` closed the DB *before* awaiting teardown; `stopHistoryReporting`'s un-guarded `updateItemUserData` would then throw "Database not initialized" and abort `doEndSession` before `teardown()` ran. | **Fixed**: DB write guarded; `closeDatabase()` moved after teardown settles in `index.ts`. |
| Unconditional sibling cascade on End | Same class as RC6 — every End click marked cross-server siblings played. | **Fixed**: gated on ≥ 90% watched. |
| Local cache divergence | WP progress/stop wrote only `last_played_date` (ties into Domain 1). | **Fixed**: position mirrored (progress + stop). |

### The 1080p→1080p black-guest issue

Not touched, per instructions. Code-level observations that support the
tunnel-bandwidth-starvation hypothesis: the only argv difference between the working
720p and failing 1080p output is the bitrate ladder (`2.5M` vs `5M` — the stream-copy
hypothesis is indeed impossible in this code; `buildArgs` always re-encodes), and
cloudflared is a per-connection proxy, so host upload = bitrate × guests. The new
guest-side `client_error` reporting (`5a66f2c`) will land `hls fatal` / `stalled` lines
in `session.log` on the next live run — that's the confirmation channel
(*runtime-confirm*, §5c item 6).

---

## 4. Domain 3 — Core system regression sweep

Code-audit verdicts on the daily paths (post-fix):

- **Solo playback, active server** — intact; the `94dbd25` fast path is unchanged
  (`serverId` absent/matching-active goes through the exact pre-existing code).
- **Solo playback, cross-server preferred version / version picker** — fixed in
  `94dbd25`; audit found no remaining gap in that path.
- **Episode auto-advance / manual prev-next** — streams were already cross-server-aware;
  reporting/bookkeeping fixed here (§1 RC7). The mpv suppression logic
  (`advanceInProgress` + file-loaded gate + 5 s failsafe) is sound by code inspection;
  the actual window swap behavior is *runtime-confirm*.
- **Search** — SearchPage: cache-first (covers both servers) + active-server API
  enrichment with dedup-group filtering: correct. TopBar dropdown: active-server only
  (§5b). Note `searchItemsDeduped`'s LIKE is name-only — parity with Emby search is
  approximate by design.
- **Library browse** — vlib queries are cache-based and server-agnostic; deduped
  pagination sound. The RC3 library_id corruption was the one real defect (fixed); rows
  corrupted by past syncs will self-heal on the next full sync (each item's true
  library re-upserts it) — recommend one manual full sync after this ships.
- **Sync** — full sync per-server checkpointing/resume verified sound; `partial` status
  prevents cache-authoritative reads while a server is missing. Incremental had the
  RC4 watermark bug (fixed). Remaining gap: `server-error` / `partial` events are
  emitted by the engine but **never forwarded to the renderer** — a failing server is
  invisible in the UI (§5b).
- **Dedup** — engine correct and server-agnostic; primary selection is metadata-scored
  (which is *why* foreign-primary detail pages had to work — now they do). Episode
  backfill + singleton dissolution sound. Incremental sync skips dedup by design
  (7-day drift rebuild).
- **Image loading** — root cause was server resolution (§2); the fallback chain remains
  as defense for genuinely broken images.
- **Trakt** — scrobbler thresholds (pause<1% clamp, ≥80% stop conversion) and queue
  prune verified present; scrobble start/stop call sites intact through my edits
  (same call shape, same order). The WAITING-room fix *removes* a spurious scrobble.
- **Bundling rules** — all new imports are static; `check-relative-requires` passes;
  no new module files added to `src/main` (existing modules only), so
  `check-bundling`'s export scan is unaffected.

**Pre-existing debug noise** (left in place, flagged for cleanup): `[push-nav-context]`,
`[end-file]`, `[client-message]`, `[episode-nav]` `// [DEBUG]` blocks and the
`[item:mark-played] handler entry` log in `ipc-handlers.ts`; `console.log` diagnostics
in `virtual-library.ts` watchlist matching. Harmless but chatty.

---

## 5. Prioritized fix list

### (a) Unambiguous fixes made in this audit (all type-checked; runtime steps in 5c)

1. Continue Watching cache truth: stop-path position writes + Emby-threshold mirroring
   (solo ESC/EOF/advance + Watch Party).
2. Mark-played cascade gated on ≥ 90% watched (solo + WP) + sibling cache updates.
3. Episode-nav start/stop reported to the owning servers; advance path bookkeeping.
4. `getResumeItemsDeduped`: `played = 0` filter (outer + group MAX) and
   last-played ordering.
5. Resume-refresh library_id preservation (`upsertItemsPreservingLibrary`, 4 sites).
6. Incremental sync watermark held back when a server fails.
7. Per-owning-server image URLs across the renderer.
8. DetailPage: enrichment/seasons/episodes/similar/mark-played/favorite routed to the
   owning server; foreign series pages get their episode lists back.
9. HomePage: Continue Watching + hero play pass serverId.
10. WP: guest-cap clamp in main; WAITING-room end no longer reports a stop; quit-order
    DB close fix (orphaned ffmpeg/cloudflared path closed).

### (b) Design decisions needed (not guessed at)

1. **Ghost reconciliation for externally-finished items (RC8).** Options:
   (i) raise the resume fetch to `Limit: 100` and, per server, zero out
   `playback_position_ticks` for that server's cached rows with `ticks > 0` that are
   absent from the response — cheap, bounded, recommended; (ii) make Continue Watching
   always live-fetch in combined mode (loses cache-first snappiness and offline rows);
   (iii) accept staleness until full sync. **Recommendation: (i)** — it's one query per
   server per sync and converges everything.
2. **Surface per-server sync failures.** `server-error`/`partial` events should be
   forwarded to the renderer (toast + a badge on the sync indicator). Currently a dead
   second server is completely silent. Small change but touches preload/types/UI.
3. **Watch Party guest-cap refusal at connect time.** Implement the deferred refusal
   (close the (N+1)th socket with an explicit "session full" message) or amend the
   CHANGELOG claim. Recommendation: implement — the clamp in (a)10 fixes policy but an
   over-cap crowd still degrades everyone's stream.
4. **Should manual "Mark played" cascade across the dedup group?** Today the detail-page
   button and context menu mark only the clicked copy (the code comment claiming it
   cascades is wrong); playback-completion and remove-from-continue do cascade. Decide
   one way and align (recommendation: cascade on explicit mark-played too, matching
   remove-from-continue).
5. **TopBar search dropdown** could use the deduped cache search (covers both servers)
   instead of the active-server API. Recommendation: switch — SearchPage already proves
   the pattern.
6. **Dead legacy API layer** (`src/renderer/api/*`, `use-emby.ts`, legacy
   `emby:user:*` / `emby:media:report-*` handlers): delete or keep? Recommendation:
   delete in a housekeeping commit — every dead path is a future wrong-server call.
7. **Debug logging sweep** (§4 list) before the next release.

### (c) Windows runtime confirmations required (in order)

Environment: `npm run package:win`, install over current build, combined mode with both
servers enabled, Trakt connected. Then:

1. **Continue Watching truth (RC1/RC2/RC5).** Play a movie from Server A for ~2 min past
   5%, ESC → Home: it must appear in Continue Watching *immediately* (no sync), ordered
   first. Repeat with an item that only exists on Server B: card art loads, click
   resumes playback from B (watch main log for `[media:playback-info] … serverId=` and
   no 404s). Finish a movie (or seek to > 90% and let it end): it must drop off the row
   immediately and show the played badge in its library.
2. **Cross-server images.** Switch active server to Valkyrie; browse a Raptor-only
   library shelf: posters must load *without* `[image-fallback] card … advancing to
   fallback` spam in the console (some fallback lines for genuinely broken images are
   fine; wholesale per-card fallbacks mean the map didn't load).
3. **Foreign series detail page.** Open a series whose dedup primary lives on the
   non-active server (check `[trakt-watchlist] match … server=` logs or pick a
   B-only show): seasons + episode list must populate; play an episode; toggle
   favorite and mark-played and verify the change on that server's own web UI.
4. **Episode nav (RC7).** Binge two episodes of a B-server show while active on A
   (auto-advance at EOF and manual `>`): both episodes must show as watched on
   *Server B's* web UI, and the second episode must appear under Continue Watching if
   you stop it midway.
5. **Incremental sync gap (RC4).** Stop Server B, launch Nocturne (incremental sync
   runs, log shows `keeping lastFullSync`), quit, start B, relaunch: items added on B
   during the outage must appear after the sync.
6. **Watch Party.** (a) Start a session, end it from the *waiting room*: main log must
   NOT contain `reportPlaybackStopped`, Trakt must not receive a scrobble.
   (b) Run a 1080p-source→1080p-output party with a remote guest and read
   `{userData}/watch-party/session.log` for the new `guest … hls fatal` / `stalled`
   lines — this is the bandwidth-starvation confirmation.
   (c) Start a party with `maxGuests` set above 10 while the Danger Zone unlock is
   OFF: log must show the clamp line.
   (d) Quit the app mid-LIVE: ffmpeg + cloudflared must exit (Task Manager) and the
   session dir must be gone.
7. **Full smoke test** (AGENTS.md items 1–10) — required regardless, since main-process
   and IPC surfaces changed.

**Post-ship note:** run one manual **full sync** after installing — it repairs
`library_id` values corrupted by the old resume-refresh (RC3) and rebuilds dedup on
clean data.
