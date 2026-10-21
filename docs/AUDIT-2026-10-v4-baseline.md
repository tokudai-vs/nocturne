# Nocturne v4 baseline audit — October 2026

**Scope.** Read-only baseline of the native v4 codebase (`src-native/`, C# / .NET 8 / WPF) against
the v3.5 cross-server correctness audit (`AUDIT-2026-07-cross-server-correctness.md`), the frozen
dedup plan (v4.4) and the Trakt/TMDB technical spec v1.0. Nothing was fixed. This document records
what exists; the next pass decides what to do about it.

**Method.**
- **Code:** read at `cc33e05` on `main` (working tree differs only by untracked review docs). Every
  claim below was followed to the call that issues the write or request, and to its callers.
- **Live database:** `%APPDATA%\Nocturne\nocturne.db` (2.8 GB, WAL), queried **read-only** with
  Windows `sqlite3.exe` 3.50.6 `-readonly` so the reader took proper WAL locks. The v4 app
  (`%LOCALAPPDATA%\Programs\Nocturne\Nocturne.exe`, binaries dated 2026-10-21 09:22) was running and
  mid-sync during the audit, so counts drift between queries. Integrity numbers were taken inside
  one read transaction. An `immutable=1` read from WSL returned `database disk image is malformed`
  (torn view without the WAL) and was discarded.
- **Live log:** `%APPDATA%\Nocturne\nocturne.log` was used for runtime corroboration.
- **Mechanical scans:** (1) every `catch` block in non-test code, classified silent / logged /
  rethrow (147 / 120 / 7); (2) every SQL string literal in C# prepared with `EXPLAIN` against the
  live schema.
- **Tests:** `dotnet test` run once (Windows SDK). Side effects: build output under
  `Nocturne.Tests/bin|obj`, and log lines appended to the user's real `nocturne.log` (see 9.2).
- **Not done:** no UI exercise and no requests to either Emby server. Items whose outcome depends
  on server or mpv runtime behaviour are marked *runtime-confirm*.
- **Missing reference:** the frozen dedup plan v4.4 is **not in the repo**. Comparisons against it
  use the four review rounds (`nocturne-dedup-plan-review*.md`, which quote its DDL and rules) and
  Trakt/TMDB spec §3–§4, which restates the dedup schema it depends on.
- **Paths:** all code paths are under `src-native/`. File names are unique, so they are cited by
  file name. Secrets seen during the audit (server tokens, `api_key` values, Trakt client ID) are
  not reproduced here.

Servers: **Valkyrie** = `c1a9120c` (active server), **Raptor** = `63daeabfb7b743a786295ae08c54794f`.
`LibraryMode = combined`.

---

## 1. Summary

v4 has nearly all of the **schema** both specs call for. Every table and column on the dedup §2 /
Trakt §4 checklist exists in the live database, the FTS5 external-content table and its three
triggers are present and in sync (413,103 = 413,103), and the swap-in rebuild re-creates the
triggers. The **behaviour** behind that schema is much thinner, and several enforcement paths
contradict the scaffolding around them:

- **Stops lose the position.** Every user-initiated stop (Esc, Stop button, window close,
  previous-episode) zeroes the playback position *before* finalizing. Nothing is written to the
  local cache, and the owning server receives `PositionTicks = 0`.
- **EOF reaches nothing.** With `keep-open=yes`, natural end-of-file raises an event with no
  subscribers, so there is no auto-advance and no finalize.
- **Played-state cascades on non-completion paths.** The paths that *do* mark played are manual
  Next, any mpv `end-file` (including errors), Remove from Continue Watching, and Trakt pull. Each
  cascades "played" to every dedup sibling on every server.
- **Dedup groups over-merge in live data.** One group holds 43 distinct Jeopardy episodes; 87 movie
  groups contain two or more different TMDB ids.
- **Sync goes stale and never deletes.** It never re-reads a completed library whose item count is
  unchanged, and there is no delete path.
- **Trakt can't connect from the UI.** The bundled client ID is `""` and the custom-ID field isn't
  wired to the auth service.

**The single biggest risk** is playback finalize writing wrong resume and watched state **to the
Emby servers themselves**, then fanning it out across servers through `sibling_write_queue` and
over-merged groups. That damage lands outside Nocturne's cache, where no rebuild or resync can
repair it, and it is invisible with one server.

---

## 2. Findings by group

Status is one of **Present / Partial / Missing / Divergent / Unknown**.

### Group 1 — Cross-server correctness

| Item | Status | Evidence | Note |
|---|---|---|---|
| 1.1 Owning-server resolution | **Divergent** | `MainViewModel.cs:1254-1258` `Servers.FirstOrDefault(s => s.Id == item.ServerId) ?? Servers.FirstOrDefault(s => s.Id == ActiveServerId)`; `EmbyClient.cs:812-813` `var bUrl = !string.IsNullOrEmpty(serverUrl) ? … : _baseUrl;` | **26 of 40** resolution sites read ambient state: 15 as a silent fallback when `item.ServerId` is null or unknown, 11 directly. 5 more are server-agnostic or use the wrong id. Only 7 resolve strictly from the owning server. Table 1.1 below. `EmbyItem.ServerId` is `[JsonIgnore]` (`EmbyItem.cs:73-74`), so every item fetched live or rehydrated from `json_data` has no owner until a caller stamps one. |
| 1.2 Position write-back on stop | **Divergent** | `MainViewModel.cs:1408` `_lastPlaybackSeconds = 0;` then `:1413` `await FinalizeCurrentPlaybackAsync(isEof);`, which computes `:1263` `(long)(_lastPlaybackSeconds * 10_000_000.0)` | Every user-initiated stop writes **nothing** locally (the `else if (_lastPlaybackSeconds > 5)` branch at `:1321` is unreachable) and reports `PositionTicks = 0` to the server (`:1282`). `ReportPlaybackProgressAsync` (`EmbyClient.cs:769`) has **no callers**, so the server never sees an intermediate position either. Per-path table below. Runtime corroboration: *Sayonara (7469029)* was started 7 times on 2026-10-20, always `at tick 0` (`nocturne.log` lines 715–788). |
| 1.3 Mark-played cascade | **Divergent** | Threshold at `MainViewModel.cs:1286` `if (isEof \|\| progress >= 90.0)`, but `:1216` `PlayNextEpisodeAsync` → `FinalizeCurrentPlaybackAsync(isEof: true)`; `HomeViewModel.cs:384-395` | The 90% threshold exists but is bypassed by four paths, each cascading to siblings (table below). Same-server duplicate versions are marked played locally (`MarkLocalGroupPlayed`, `DatabaseService.cs:2615-2633`) but excluded from the Emby write (`sib.ServerId != item.ServerId`, `MainViewModel.cs:1306`), so local and server state diverge. |
| 1.4 Sync watermark on failure | **Partial** | Mid-sync failure: `SyncEngine.cs:405-409` breaks and sets `failedServers`; checkpoint only advances in the writer on commit (`DatabaseService.cs:776-798`); `:543-559` returns `partial` before `LastFullSync` is written (`:678-682`). Discovery failure: `EmbyClient.cs:431-435` `catch … return new List<EmbyItem>();` → `SyncEngine.cs:158` `Success: true` → `:170` dropped by `res.Views.Count > 0` | There is **no time-based watermark**. `LastFullSync` is written but never read as a fetch filter, so the v3.5 outage-window loss can't recur. Two other failure shapes exist instead: (a) an **unreachable server is silently excluded** and the sync still ends `complete`; (b) the skip rule `checkpoint.IsCompleted && checkpoint.TotalItems == serverTotal` (`SyncEngine.cs:196-199`) means a completed library is **never re-fetched while its item count is unchanged**, so server-side watched state, metadata edits and replaced files never reach the cache. |
| 1.5 Image URL construction | **Partial** | `ContinueWatchingCard.xaml.cs:97-109` (server null → `BuildImageUrl(…, null, null)` → instance `_baseUrl/_token`); `HomeViewModel.cs:136-139` `?? activeServer` and `it.ServerId ??= srv?.Id`; `MediaCard.xaml.cs:85-97` | The group-level image election (`image_server_id` set on 393,235 of 413,103 groups) is **never copied onto `EmbyItem`** by any shelf, home or library query. Only the watchlist uses it (`DatabaseService.cs:3369`, `WatchlistViewModel.cs:137-141`). `MediaCard` passes the item's own tag with `ImageItemId` (`:88-97`). There is **no** v4 sibling image fallback (no `ImageFailed` handler), so foreign 404s show the fallback tile instead of being masked. |
| 1.6 Auto-advance | **Divergent** | `MpvController.cs:194` `--keep-open=yes`; `:553-559` `eof-reached` → `NextEpisodeRequested?.Invoke()` (no subscribers anywhere); `MainViewModel.cs:274-302` listens only to `PlaybackEnded` | **Natural EOF never auto-advances or finalizes.** The player stops on the last frame; Esc then goes through 1.2 (position 0, progress 0, not marked played). Auto-advance runs only when `PlaybackEnded` fires, from mpv `end-file` of *any* reason (`:583-590`, `reason` unchecked) or process exit (`:251-257`). In that case the outgoing item is finalized as EOF: marked played on the owning server (with active fallback), locally group-wide, and on siblings. The next episode, when it comes from the fallback fetch (`MainViewModel.cs:924-930`), is **not stamped** with `ServerId`, so its playback and finalize resolve to the active server. |

**Table 1.1 — every base-URL / token resolution site.** "Fallback" means owning server when
`item.ServerId` matches a configured server, otherwise the active server. "Active" means the DI
singleton `EmbyClient`, which `MainViewModel.cs:829` configures as the active server.

| # | Site | Builds / calls | Resolves from |
|---|---|---|---|
| 1 | `EmbyClient.cs:812-813` `BuildImageUrl` | image URL + `api_key` | args, else **instance** `_baseUrl/_token` |
| 2 | `EmbyClient.cs:827-828` `BuildChapterImageUrl` | chapter image | args, else **instance** |
| 3 | `EmbyClient.cs:842-844` `GetStreamUrl` | stream URL + `api_key` | args, else **instance** |
| 4 | `EmbyClient.cs:190-198` `BuildUrl` + default headers `:166-187` | every API call | instance (correct iff instance is) |
| 5 | `SyncEngine.cs:153-154`, `:181-182`, `:336-337` | views / probe / batches | **owning** (new client per server) |
| 6 | `SyncEngine.cs:105-114` | sync with no servers configured | **active** singleton |
| 7 | `MainViewModel.cs:924-930` | next/prev episode fetch | **fallback**; results unstamped |
| 8 | `MainViewModel.cs:1032-1039` | stream URL per failover candidate | **fallback** |
| 9 | `MainViewModel.cs:1133`, `:1148` | chapters + `ReportPlaybackStart(item.Id…)` | candidate's server but **original `item.Id`** (wrong id after failover) |
| 10 | `MainViewModel.cs:1254-1290` | stop report, `MarkPlayedAsync` | **fallback** |
| 11 | `MainViewModel.cs:1361-1377` | sibling queue drain | **owning** (queue row) |
| 12 | `MainViewModel.cs:1422-1428` | chapter thumbnails | **fallback** |
| 13 | `MainViewModel.cs:1435-1447` | preview fallback image | **fallback** |
| 14 | `HomeViewModel.cs:71-75`, `:88` | hero trailer item fetch | **active** unless url+token passed |
| 15 | `HomeViewModel.cs:129-172` | cache-first rows' images; stamps `ServerId ??=` | **fallback** (stamps the active id) |
| 16 | `HomeViewModel.cs:190-213` | combined fan-out rows | **owning** |
| 17 | `HomeViewModel.cs:286-313`, `:349` | separate-mode rows | **active** (by design, separate mode) |
| 18 | `HomeViewModel.cs:376-382` | `RemoveFromResumeAsync` | **fallback** |
| 19 | `HomeViewModel.cs:479-490` | `PlayItemAsync` (non-embedded) | **fallback** |
| 20 | `LibraryViewModel.cs:93-97`, `:176-180`, `:285-304`, `:341-352` | combined mappings / views / items | **owning** |
| 21 | `LibraryViewModel.cs:206` | separate-mode views | **active** (by design) |
| 22 | `LibraryViewModel.cs:378-386` | separate-mode items + images | **active** (by design) |
| 23 | `LibraryViewModel.cs:404-411` | `PlayItemAsync` (non-embedded) | **fallback** |
| 24 | `SearchViewModel.cs:76-88` | combined search | **owning** |
| 25 | `SearchViewModel.cs:105-113` | separate-mode search | **active** (by design) |
| 26 | `DetailViewModel.cs:161-175` `ResolveClient` (used at `:242`, `:655-659`, `:705`, `:730`, `:752`, `:936-938`) | detail load, play, mark played, favourite, watch-party stream | **fallback**; also mutates shared `_currentServer` |
| 27 | `DetailViewModel.cs:245-250` | `GetCachedItem(itemId)` **id-only** fallback, then stamps `_currentServer.Id` | id-only (can bind the other server's row) |
| 28 | `DetailViewModel.cs:322`, `:358`, `:375`, `:390`, `:534`, `:563` | poster, backdrop, cast, episode images | **fallback** (via `_currentServer`) |
| 29 | `DetailViewModel.cs:550` | `UpsertItems(eps, _currentServer?.Id ?? "server", seasonId)` | literal `"server"` if unresolved (0 such rows live) |
| 30 | `MediaCard.xaml.cs:218` | context-menu play stream URL | **active** (no server args) |
| 31 | `MediaCard.xaml.cs:236` | mark played / unplayed | **active** |
| 32 | `MediaCard.xaml.cs:255` | favourite | **active** |
| 33 | `MediaCard.xaml.cs:85-97` | poster | `item.ImageServerId ?? item.ServerId` (**owning**; null → no Emby URL) |
| 34 | `ContinueWatchingCard.xaml.cs:97-109` | art | `item.ServerId`, null → **instance** fallback |
| 35 | `ContinueWatchingCard.xaml.cs:252` | mark played / unplayed | **active** |
| 36 | `ContinueWatchingCard.xaml.cs:270` | favourite | **active** |
| 37 | `WatchlistViewModel.cs:137-141` | poster from `image_server_id` | **owning** |
| 38 | `TrailerService.cs:44-45` | local trailer URL | caller's url/token, else client's |
| 39 | `DatabaseService.cs:915`, `:934`, `:1134`, `:2211`, `:2283` | CW-cache delete, cached-item lookup, group lookup, resume clear, progress write | **server-agnostic** `WHERE id = $id`; 4 ids collide across servers live (e.g. `1012702` is an Episode on Raptor and *When Marnie Was There* on Valkyrie) |
| 40 | `RecordWatchEvent` callers: `MainViewModel.cs:1157`, `:1347`; `HomeViewModel.cs:492`; `LibraryViewModel.cs:417`; `DetailViewModel.cs:673`; `MediaCard.xaml.cs:223` | `watch_events.server_id` | stores a **URL** (`srv?.Url ?? client.BaseUrl`), not a server id |

**Table 1.2 — playback-stop paths.**

| Path | Entry | Local write-back | Server write-back |
|---|---|---|---|
| Stop button | `PlayerOverlayWindow.xaml.cs:322` → `StopPlaybackAsync()` | none (position zeroed at `MainViewModel.cs:1408`) | `Stopped` with `PositionTicks = 0` (`:1282`) |
| Esc | `MainWindow.xaml.cs:433-437` → `StopPlaybackAsync()` | none | `PositionTicks = 0` |
| Natural EOF | `eof-reached` → `NextEpisodeRequested` (no subscriber) | none | none; then Esc, as above |
| mpv `end-file` / process exit | `MpvController.cs:583-590`, `:251-257` → `PlaybackEnded` → `StopPlaybackAsync(isEof: true)` or `PlayNextEpisodeAsync` | marked played; position cleared | `MarkPlayedAsync` + sibling cascade |
| Window close | `MainWindow.xaml.cs:376-387` `_ = _viewModel.StopPlaybackAsync();` (fire-and-forget) | none | `PositionTicks = 0`, racing process teardown |
| App exit | `App.xaml.cs:187-194` disposes mpv + DB, no finalize | none | none |
| Previous episode | `MainViewModel.cs:1221-1231` (zeroes at `:1228`, `isEof: false`) | none | `PositionTicks = 0` |
| Next episode | `MainViewModel.cs:1207-1218` (`isEof: true`) | marked played | marked played + cascade |
| Non-embedded play | `MediaCard.xaml.cs:206-225`; `PlayItemAsync` in Home/Library/Detail VMs | no stop handler exists | start report only |

When the `> 5 s` branch *is* reached, it calls `UpdatePlaybackProgress(item.Id, …)`
(`MainViewModel.cs:1341` → `DatabaseService.cs:2247`), which rewrites `json_data` for **every server's
row with that id** (`:2283` `WHERE id = $id`). Its `serverId` parameter is unused.

**Table 1.3 — paths that write played state.**

| Writer | Threshold | Cascade |
|---|---|---|
| `FinalizeCurrentPlaybackAsync` (`MainViewModel.cs:1286-1320`) | `isEof \|\| progress >= 90.0` | `MarkLocalGroupPlayed` + `EnqueueSiblingPlaybackWrite(…, markPlayed: true)` for other-server siblings |
| `PlayNextEpisodeAsync` (`:1216`) via `>` key (`MainWindow.xaml.cs:451-454`), overlay Next (`PlayerOverlayWindow.xaml.cs:352`, `:938`) | **none**, `isEof: true` hard-coded | as above |
| `PlaybackEnded` handler (`MainViewModel.cs:274-302`) | **none**; any `end-file` reason or process exit after the 1.5 s start window | as above |
| `RemoveFromContinueWatchingAsync` (`HomeViewModel.cs:384-395`) | **none** | group played locally + `markPlayed: true` to every other-server sibling |
| Trakt pull `ApplyWatchedStateToGroup` (`TraktSyncService.cs:293-312`) | Trakt history | every version in the group, local + queue |
| Detail toggles (`DetailViewModel.cs:705-709`, `:730-734`) | manual | owning server only; local cache not updated |
| Card toggles (`MediaCard.xaml.cs:236`, `ContinueWatchingCard.xaml.cs:252`) | manual | **active** server; local cache not updated |

### Group 2 — Known bugs from the dedup post-mortem

Here status describes the **defect**: Present = the defect is still in the code; Missing = not
found (fixed); Partial = fixed in one path, still live in another.

| Item | Status | Evidence | Note |
|---|---|---|---|
| 2.1 Episodes skipped by type filter | **Partial** | Fixed in the incremental index: `ConcurrentDedupIndex.cs:252` `if (item.Type != "Movie" && item.Type != "Series" && item.Type != "Episode") continue;`. Still live: `DedupEngine.cs:69-70` `var movies = …Type == "Movie"…; var series = …Type == "Series"…` | `DedupEngine.Deduplicate` is still called for combined-mode live rows: `HomeViewModel.cs:242`, `LibraryViewModel.cs:320`, `SearchViewModel.cs:100`. Episodes pass through ungrouped. The same method keys `groupedItemIds` by `item.Id` only (`DedupEngine.cs:99`), so a cross-server id collision **drops** an item from the result. |
| 2.2 Hard-coded rebuild row limit | **Missing** | `SettingsViewModel.cs:1273` `_dedup.RebuildIndex(_db);` → `DedupEngine.cs:270-278` pages `GetCachedItemsChunk(offset, 5000)` until empty | No cap. The rebuild is not given server priority (`RebuildIndex(_db)`, so `serverPriority` is null), so primary and image election differ from sync. |
| 2.3 `year` vs `production_year` | **Present** | `DatabaseService.cs:1166` `… WHERE LOWER(name) = $name AND year = $year AND id != $id;` | `cached_items` has no `year` column. It fired in production 4 times on 2026-10-20 (`nocturne.log` 585/609/638/661: `LoadItem failed … 'no such column: year'`). `SelfCheckPreparedStatements` validates a **different string** (`:2535` uses `production_year`), so the self-check certifies text that never runs. A prepare of every SQL literal against the live schema found no other nonexistent column. Fragments joined with `sql +=` were checked individually. |
| 2.4 Exception swallowing around DB | **Present** | 19 silent DB catches, e.g. `DatabaseService.cs:950` `catch { return null; }`, `:3702` (`GetUserDataOrigin` → null), `:3803` (`GetDedupGroup` → null) | Silent: `:196`, `:950`, `:993`, `:1580`, `:1584`, `:1777`, `:1830`, `:1897`, `:2416`, `:2454`, `:3210`, `:3415`, `:3581`, `:3628`, `:3649`, `:3684`, `:3702`, `:3724`, `:3745`, `:3803`. Logged-and-continue: `:417`, `:449` (failed `ALTER` → missing column, app continues), `:2240`, `:2313`, `:2486`, `:2514`, `:3116` (Trakt event lost), `:3153`, `:3173`, `:3192`, `:3247`, `:3267`, `:3288`, `:3381`, `:3457`, `:3494`, `:3540`, `:3607`. **Most dangerous:** `ConcurrentDedupIndex.cs:120-122` clears the index and sets `_nextGroupId = 1` *before* the `try`; the catch at `:155-158` only warns. If loading fails, sync mints group ids from 1 and `SaveDedupBatch` upserts `ON CONFLICT(group_id) DO UPDATE` (`DatabaseService.cs:1993`), **overwriting unrelated existing groups**. |
| 2.5 ToggleSwitch sliver | **Missing** | `SettingsPage.xaml:657-658` `<DockPanel …>` / `<ui:ToggleSwitch DockPanel.Dock="Right" …` | All 6 ToggleSwitches (`:658`, `:716`, `:785`, `:1103`, `:1109`, `:1115`) sit directly in a `DockPanel` with `DockPanel.Dock="Right"`. |

### Group 3 — Schema inventory

**3.1 Live schema.** The full dump is in Appendix A. It matches the DDL in
`DatabaseService.cs:55-160` (base tables), `:206-316` (dedup / Trakt tables), `:364-383` (indexes) and
`:390-414` (FTS5 + triggers) verbatim. Every startup also drops `items`, `dedup_episode_groups`,
`sync_state` and `image_cache` (`:50-53`). These are v3.5 table names. **v3.5 and v4 share the same
file path** (`%APPDATA%\Nocturne\nocturne.db`), so launching v4 destroys v3.5's tables.

**3.2 Comparison.** Reference: Trakt/TMDB spec §3.1 and §4; dedup plan §2 as quoted in reviews r1–r4.

| Table / column | Exists? | Diverges how? |
|---|---|---|
| `dedup_groups` | yes | `group_id INTEGER PRIMARY KEY`, no AUTOINCREMENT (matches r1 S5). `year` and `date_created` exist but are **NULL on all 413,103 rows**: sync never receives `ProductionYear` / `PremiereDate` (see 5.3). |
| ↳ `is_provisional` | yes | `NOT NULL DEFAULT 0`; 7,428 provisional (all Episode) |
| ↳ `version_count` | yes | `NOT NULL DEFAULT 1`; 0 rows drift from `COUNT(cached_items)` |
| ↳ `max_height`, `has_4k` | yes | `max_height` nullable; `has_4k NOT NULL DEFAULT 0`. Attach uses `Math.Max(x ?? 0, …)` (`ConcurrentDedupIndex.cs:644-646`), so the R3 #3 NULL-propagation bug is absent. |
| ↳ `tmdb_key` | yes | populated on 105,761 groups |
| ↳ `image_item_id`, `image_server_id` (+ `backdrop_*`) | yes | populated on 393,235 groups; not surfaced to UI except Watchlist (1.5) |
| ↳ `ix_groups_needs_art` | yes | matches §3.1 |
| `dedup_keys` | yes | `key TEXT PRIMARY KEY, group_id, is_authoritative`; 1,173,644 rows |
| ↳ `is_authoritative` | yes | 540,052 authoritative |
| ↳ `WITHOUT ROWID` | yes | — |
| `cached_items` | yes | `PRIMARY KEY (id, server_id)`; no FK on `group_id` (matches r2 C2). Extra legacy `dedup_group_id TEXT`, dual-written and still read (4.3). `parent_id` holds the **library view id** for synced rows, not the season/series (`DatabaseService.cs:714`; 0 of 349,240 episodes have `parent_id = season_id`). No `playback_position_ticks` column; position lives in `user_data` and `json_data`. |
| ↳ `height`, `bitrate`, `video_codec`, `hdr_type`, `edition`, `runtime_seconds`, `group_id` | yes | all present; `height` populated on 457,354 Movie/Episode rows |
| `user_data` | yes | identical to §4.1, incl. `origin`, `updated_locally`, `WITHOUT ROWID`, `ix_user_data_continue` |
| `watchlist_entries` | yes | identical to §4.2. Emby favourites are inserted during sync (`DatabaseService.cs:759-772`) but never deleted when un-favourited. |
| `tmdb_metadata` | yes | identical to §4.3; 0 rows live |
| `sibling_write_queue` | yes | `UNIQUE(server_id, item_id)` with upsert (`:2760-2766`); 2 rows pending live |
| `trakt_queue` | yes | identical to §4.4 (no unique key) + `ix_trakt_queue_order`. `group_id` is **not remapped** by the rebuild swap. |
| `integration_state` | yes | **Divergent:** `PRIMARY KEY (name, key) WITHOUT ROWID` vs spec `name TEXT PRIMARY KEY` (see §4 Spec deltas) |
| `migration_state` | yes | `key / value`; **never read or written** by any code (grep: only the `CREATE`) |
| FTS5 `dedup_groups_fts` | yes | external content on `dedup_groups`; **never queried**: no `MATCH` anywhere in the codebase |
| ↳ triggers `dg_fts_ai/ad/au` | yes | present live; re-created inside the swap (`DatabaseService.cs:3034-3051`); `'rebuild'` after swap (`:3069`). Live FTS rows = groups = 413,103. |
| Not in spec: `sync_checkpoints`, `watch_events`, `trakt_watchlist`, `trakt_watched_history`, `continue_watching_cache`, `media_stream_preferences` | yes | v4-specific; `watch_events` 0 rows (DB recreated 14:20 IST today, see Open questions) |

| Item | Status | Evidence | Note |
|---|---|---|---|
| 3.3 PRAGMAs at open | **Partial** | `DatabaseService.cs:46-48` `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;`; connection string `:29-31` `Cache = SqliteCacheMode.Shared, DefaultTimeout = 5` | `journal_mode` is **WAL**, verified in the file header (bytes 18–19 = `2,2`). `synchronous=NORMAL` and `busy_timeout=5000` are per-connection, so the code shows intent only; actual values are not observable from outside the process. `foreign_keys` is **never set**, so SQLite's default OFF applies (no FK constraints are declared anyway). Shared-cache mode is enabled. |
| 3.4 Writers / connections | **Present** (single connection) | One `SqliteConnection` (`DatabaseService.cs:41`); every method that touches `_connection` takes `lock (_dbLock)` or is a private helper only called under it (checked mechanically) | All writes are serialized, but there are many logical writers: the sync writer task, UI handlers, Trakt/TMDB, the rebuild. `SwapInRebuild` holds `_dbLock` for the **whole rebuild** (`:3003-3008`; JSON deserialization of ~470k rows), blocking every UI read. `ClearAllCache` deletes the hard-coded `%APPDATA%` path (`:1765-1770`) regardless of the constructor's `dbPath`. |

### Group 4 — Dedup engine as it exists

| Item | Status | Evidence | Note |
|---|---|---|---|
| 4.1 Key forms | **Partial** | `ConcurrentDedupIndex.cs:166-230` | Authoritative: `ep:{tvdb\|tmdb\|imdb}:{seriesId}:sXXeYY` (`:178-183`), `{tmdb\|imdb\|tvdb}:ep:{id}` (`:186-191`), `{tmdb\|imdb\|tvdb}:{movie\|series}:{id}` (`:211-216`). Fuzzy: `ep:series:{SeriesId}:sXXeYY` (`:194-195`), `ep:name:{norm}:sXXeYY` (`:197-204`), `name:{type}:{norm}:{year}` (`:219-226`). **Live:** 0 `ep:tvdb/tmdb/imdb` keys, because `SeriesProviderIds` is never populated (0 of 20,000 sampled episodes; `EmbyItem.cs:80-91`). The B2 stable episode keys are dead code. 0 `name:` keys, because `ProductionYear` is null on 128,555 of 128,557 movies. `ep:series` uses the **server-local** Emby series id. A null `IndexNumber` becomes `e00` (`:174`; 1,561 episodes). Provider ids aren't validated (live Tmdb id `../tv/222031` on 35 episodes and 17 series). |
| 4.2 Merge vs attach; order dependence | **Divergent** | Merge exists: Case 1 `:344-361` (≥2 authoritative groups → `Min()` winner), Case 2 `:364-382` (absorb provisional), Case 3 `:385-404`. Attach: `:467-473` `if (authMatches.Count > 0) { … AttachItemToGroupRecord(target, …) }` | **No conflicting-key check.** Authoritative merges are transitive across any shared key (group 1287 = *The Banker* + *Bunker* via `tvdb:movie:131759`). Path `:467-473` attaches an item to an authoritative group reached **only through a fuzzy key, with no cap**, despite the "remake guard" comment. Live: group 279812 holds 43 distinct Jeopardy episodes, each with its own `tvdb:ep` id, joined by `ep:series:…:s2021e00`. **Order-dependent:** winner = lowest id; primary election needs strict `score > PrimaryScore` (`:666-669`); the fuzzy cap (`:411-413`) depends on arrival order; `AttachItemToGroupRecord` overwrites ownership of every candidate key, including fuzzy keys owned by other groups (`:671-676`). Live: 87 movie groups with ≥2 distinct `tmdb_id`, 309 episode groups spanning >1 (season, episode), 3,732 groups holding >1 `tvdb:ep` key. |
| 4.3 Batch rebuild | **Partial** | `DedupEngine.cs:215-287` → `DatabaseService.cs:2999-3090` | Swap-in, not in-place: builds `*_rebuild` tables, swaps in one transaction under `legacy_alter_table`, remaps `cached_items.group_id` (3a/3b `:3054-3064`), restores triggers, rebuilds FTS, re-ingests NULLs. Deterministic for a fixed table (`ORDER BY rowid`). Gaps: does **not** remap the legacy `dedup_group_id` column, which is still read by `GetCachedItems` (`:810-811`), `GetGroupVersions` (`:1086`, `group_id = $gid OR dedup_group_id = $group_id`), `GetCachedChildren`, `GetCombinedLibraryItems`. Group ids restart at 1, so after a rebuild `dedup_group_id` points at unrelated groups (the r2 C1 class, half-fixed). Doesn't remap `trakt_queue.group_id`. Mutual exclusion with sync is UI-only (`SettingsPage.xaml:751` `IsEnabled="{Binding IsSyncing, Converter={StaticResource InverseBool}}"`). Live trigger text matches `EnsureSchema`'s indentation, not the swap's, so the swap has probably never run on this DB (weak inference). |
| 4.4 `version_count` | **Present** | `ConcurrentDedupIndex.cs:643` `target.VersionCount = target.ItemIds.Count;`; `ClearServerData` recomputes via `COUNT(*)` (`DatabaseService.cs:1858-1859`) | Recomputed from a set, not incremented (r3 #5 absent). Live drift 0. Trusted for one decision, the fuzzy cap (`:413`). No deletion path depends on it. |
| 4.5 Delete / unsync paths | **Partial** | Server removed: `SettingsViewModel.cs:778` → `DatabaseService.cs:1837-1872` | **Item removed on server: Missing** (no mark-and-sweep; `last_synced` / `ix_items_server_sync` are never used for deletion). **Library unmapped: Missing.** **Server removed: Partial.** It deletes items, checkpoints, queue rows, `user_data`, empty groups and their keys, and recomputes `version_count`. It does **not** re-elect the primary or image: groups whose primary lived on the removed server keep `primary_item_id/primary_server` pointing at deleted rows, so they **vanish from library shelves**, which filter on the primary (`:1017`). It also keeps `watchlist_entries` (`source = server_id`), `trakt_queue`, and keys contributed only by removed items. |
| 4.6 Current numbers | **Present** (numbers) | Snapshot inside one read transaction, ~15:00 IST, sync in progress (Valkyrie library 7756 at 41,500 / 676,688) | See table below. |

| Metric | v3 baseline | v4 live 2026-10-21 |
|---|---|---|
| Cached items | 1,480,383 | 563,439 (Raptor 415,231; Valkyrie ~148k, mid-sync). Movie 128,557 · Episode 328,618 · Series 9,738 · Season 22,392 · Folder 46,748 · Audio 25,587 |
| Groups | 32,366 | 413,103 total (every Movie/Series/Episode gets one, incl. singletons); **45,514 with ≥2 versions** (Movie 23,989 · Episode 20,732 · Series 793); 34,714 span both servers |
| Merged | 73,895 | 100,952 items in multi-version groups (55,438 beyond the first per group) |

The v3 metric definitions aren't recorded, so the rows may not be like-for-like (Open questions).
94,898 items have no group: Audio/Folder/Season/BoxSet/Playlist (non-dedup types) plus 110 Movies
and 12 Series in flight.

### Group 5 — Sync pipeline

| Item | Status | Evidence | Note |
|---|---|---|---|
| 5.1 Design; bounded; one writer | **Partial** | `SyncEngine.cs:221-226` `Channel.CreateBounded<SyncBatch>(new BoundedChannelOptions(8) { FullMode = Wait, SingleWriter = false, SingleReader = true })`; writer task `:256-314`; ≤4 servers concurrently, sequential per server (`:324-332`) | Decoupled pipeline, bounded. Adaptive batch 250 → 25,000 (`AdaptiveBatchController.cs:9-11`). One *sync* writer task, but DB writes from other threads share the connection (3.4). `RunFullSyncAsync` has **no re-entrancy guard** and 10 call sites (startup `MainViewModel.cs:850`, onboarding ×2, settings ×7, incl. after Trakt connect `SettingsViewModel.cs:1576`), so two syncs can interleave and share `_dedupIndex`. |
| 5.2 Checkpoint atomicity | **Present** (atomicity) / **Divergent** (refresh semantics) | `DatabaseService.cs:775-798` "write the checkpoint in the exact same transaction"; writer sets `watermark = batch.StartIndex + batch.Items.Count` (`SyncEngine.cs:265`) | Advances on **commit**, as a **contiguous prefix** per view (one producer per server, FIFO channel, sequential views). Caveats: (a) `UpsertItems` commits in 2,000-row transactions with the checkpoint only in the last (`:580-590`; batches go to 25,000), which is harmless on resume. (b) Dedup state is saved in a **separate, earlier** transaction (`ConcurrentDedupIndex.cs:478-481` before `SyncEngine.cs:273`). (c) Offset paging over a mutable, name-sorted set: resume after server-side inserts can skip items. (d) The count-equality skip rule (1.4). |
| 5.3 `Fields=` vs N+1 | **Present** | `SyncEngine.cs:369-373` `GetItemsAsync(…, fields: EmbyClient.FullSyncItemFields)`; `EmbyClient.cs:75` | No per-item fetch: **1 request per 1,000 items** at the default batch (4 at the 250 floor). But `FullSyncItemFields` omits `ProductionYear` and `PremiereDate`. Live: `"ProductionYear":null` and `"PremiereDate":null` in 5,000 / 5,000 sampled movies, and `production_year` NULL on 128,555 movies and all 10,592 series. Cause probable; see Open questions. |
| 5.4 Images in sync loop | **Present** (lazy) | No image calls in `SyncEngine.cs`; `ImageDownloadMs = 0` hard-coded (`:535`) | Images load lazily via `BitmapImage` / XAML bindings. v4 has **no** disk image cache; `ClearAllCache` only deletes the v3.5 `image-cache` dir (`DatabaseService.cs:1774-1778`). |
| 5.5 Pause during playback | **Missing** | No reference to player state in `SyncEngine.cs`; no pause API; no power mode in v4 | — |

### Group 6 — Memory and rendering

| Item | Status | Evidence | Note |
|---|---|---|---|
| 6.1 `.csproj` GC / runtime flags | **Present** (values) | `Nocturne.App.csproj:14` `<ServerGarbageCollector>false</ServerGarbageCollector>`, `:15` `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` | `TieredPGO` not set (so the .NET 8 default, enabled). `InvariantGlobalization` not set (so false). Installed `Nocturne.runtimeconfig.json`: `System.GC.Concurrent: true` only. **Measured:** the running process had a 1,200 MB working set / 1,078 MB private (mid-sync), against CLAUDE.md's "~45 MB". Structural holders: `ConcurrentDedupIndex` loads every group, key and item tuple into the `SyncEngine` singleton for the app lifetime (`:116-160`; 342,523 groups / 955,911 keys logged at 14:56), plus batches of up to 25,000 items. Attribution not measured. |
| 6.2 `BitmapImage` hygiene | **Partial** | `MediaCard.xaml.cs:110-116`, `ContinueWatchingCard.xaml.cs:115-121`, `PlayerOverlayWindow.xaml.cs:812-818`, `:860-865` | All 4 code sites set `DecodePixelWidth` (220/320/320/320) and `CacheOption = OnLoad`. **Freeze:** the two cards call `if (bmp.CanFreeze) bmp.Freeze();`, which for an http URI that is still downloading is false, so it's skipped in the common case (*runtime-confirm*). The two player sites never freeze, and cache into an unbounded `Dictionary<string, BitmapImage>` (`:602`). **Six XAML bindings** go through the default string→ImageSource converter with no `DecodePixelWidth`: `HomePage.xaml:31` (hero 1920), `DetailPage.xaml:122` (backdrop), `:310` (poster), `:597` (per-episode thumbnail), `:731` (cast), `WatchlistPage.xaml:104` (posters). |
| 6.3 Virtualization | **Divergent** | `MediaRow.xaml:35-49` `<ScrollViewer …><ItemsControl … VirtualizingPanel.VirtualizationMode="Recycling" ScrollViewer.CanContentScroll="True">` with `VirtualizingStackPanel` | **Home rows:** virtualization flags are set, but the `ItemsControl` sits *inside* an external `ScrollViewer` and has no template `ScrollViewer`, so the panel isn't the scroll owner, gets unbounded measure, and realizes everything (*runtime-confirm* with a live visual tree). Rows hold ≤20–24 items. **Library** (`LibraryPage.xaml:43-55`), **Search** (`SearchPage.xaml:15-28`), **Watchlist** (`WatchlistPage.xaml:62-66`): `ItemsControl` + `WrapPanel` in a `ScrollViewer`, **no virtualization**. **Episodes** (`DetailPage.xaml:577`): default `StackPanel` inside the page `ScrollViewer` (`:107`), not virtualized; a season can hold hundreds of episodes, each with a full-decode image (`:597`). Library pages are **capped at 100–200 items** with no paging (`LibraryViewModel.cs:274`, `:295`, `:347`, `:367`, `:378`). |
| 6.4 Large JSON deserialization | **Missing** (streaming) | `EmbyClient.cs:468` `await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>()` | Every list call fully materializes. `DeserializeAsyncEnumerable` isn't used anywhere. A 25,000-item batch of movies at the live average of 5.5 KB/item is ~140 MB of response text. Every cache read also deserializes `json_data` per row. |

### Group 7 — Player

| Item | Status | Evidence | Note |
|---|---|---|---|
| 7.1 Embedding | **Present** | `MpvHwndHost.cs:40-50` `CreateWindowEx(0, "static", …, WS_CHILD \| WS_VISIBLE \| WS_CLIPCHILDREN, …)`; `MpvController.cs:189` `$"--wid={(long)hwnd}"` | `--wid` into a child HWND hosted by `MainWindow.xaml:336`. `MpvController` owns the process and **spawns a new `mpv.exe` per playback** (`:166` `StopAsync`, then `:243` `Process.Start`). That includes episode navigation, which is not v3.5's idle-process model. |
| 7.2 HUD compositing | **Present** (described) | `PlayerOverlayWindow.xaml:5-11` `WindowStyle="None" AllowsTransparency="True" Background="Transparent" ShowInTaskbar="False"`; `PlayerOverlayWindow.xaml.cs:138-170` `SyncWithParent` | A separate owned top-level **layered** WPF window covers MainWindow's whole client rect. Video is not resized and there are no reserved bands. Fullscreen resizes MainWindow to the monitor rect (`MainWindow.xaml.cs:126-188`) and the overlay follows via `LocationChanged/SizeChanged` (`:373-374`). |
| 7.3 mpv options | **Present** (listed) | CLI `MpvController.cs:187-230`; `--config-dir` → `portable_config/mpv.conf` (installed copy identical to `build/mpv/portable_config/mpv.conf`) | **CLI:** `--wid`, `--input-ipc-server`, `--idle=yes`, `--osc=no`, `--really-quiet`, `--keep-open=yes`, `--input-default-bindings=yes`, `--input-vo-keyboard=yes`, `--input-cursor=yes`, `--force-window=yes`, `--hwdec=auto`, `--load-scripts=no`, `--osd-level=0`, `--osd-bar=no`, `--border=no`, `--sub-auto=all`, `--sub-visibility=yes`, `--sub-border-size`, `[--start]`, `[--sub-font/-size/-color/-back-color]`. **mpv.conf:** `vo=gpu-next`, `gpu-api=d3d11`, `hwdec=auto-safe` (overridden by CLI `auto`), `tone-mapping=auto`, `target-colorspace-hint=yes`, `keep-open=yes`, `cache=yes`, `demuxer-max-bytes=50M`. **`video-sync` is not set.** `--load-scripts=no` disables `nocturne_select.lua` / `open-subtitles.lua` in embedded mode. |
| 7.4 Playback event surface | **Divergent** | `MpvController.cs:24-34` events; subscribers in `MainViewModel.cs:196-302` | **Start:** `MainViewModel.ExecuteEmbeddedPlaybackAsync` after `PlayEmbeddedAsync` returns (`:1148-1156`). `PlaybackStarted` has **no subscribers**. **Pause/resume:** `PauseChanged` (`:220-245`). **Position:** `PositionChanged` (`:196-209`; observe + 250 ms poll). **Stop/EOF:** `PlaybackEnded` (`:274-302`, from `end-file` / process exit / `StopAsync`). `NextEpisodeRequested` (EOF) and `ErrorOccurred` have **no subscribers**. **Seek:** no event; seeks are commands (`SeekTo`, `:395`). `PlayEmbeddedAsync` swallows spawn/IPC errors (`:283-287`) and returns quietly when `mpv.exe` is missing (`:168-174`). The failover loop (`MainViewModel.cs:1030-1066`) only advances on a thrown exception, so it **can't see those failures**. |

### Group 8 — Trakt as it exists

There are **two Trakt stacks.** The new one (`TraktAuthService` / `TraktScrobbleService` /
`TraktQueueDrainWorker` / `TraktSyncService`) is spec-shaped. The legacy one (`TraktService`) is
still wired into playback, sync, library and cards. They don't share connection state.

| Item | Status | Evidence | Note |
|---|---|---|---|
| 8.1 Client ID / secret / token storage | **Partial** | New: `TraktAuthService.cs:18` `private const string BundledClientId = "";`; tokens DPAPI-hex in SQLite `integration_state` (`:428-441`). Legacy: `Settings.cs:86-90` `TraktAccessToken`, `TraktRefreshToken`, `TraktClientIdOverride`, `TraktClientSecretOverride` in plaintext `nocturne-settings.json` | Live `settings.json` holds a `TraktClientIdOverride` (43 chars), an empty secret, and null tokens; `integration_state` has no Trakt rows. `CredentialProtection.cs:14-29` / `:36-50`: if DPAPI throws, it **silently falls back to a fixed-key XOR** (`catch { }`), and `Unprotect` returns XOR-garbage instead of failing. No token logging found. `user_code` is logged (`TraktAuthService.cs:116`, allowed by spec). Legacy `TraktService.cs:160`, `:234` log raw Trakt response bodies. **Emby access tokens** are plaintext in `settings.json` (`Settings.cs:10`). |
| 8.2 OAuth flow / redirect | **Divergent** | `TraktAuthService.cs:82-117` device code + PKCE S256; poll mapping `:139-193` (400 Pending, 429 SlowDown, 404, 409, 410, 418); UI `SettingsViewModel.cs:1545-1576` | Device flow, so no redirect URI. **Unreachable in practice:** `SetCustomClientId` has **no callers** (grep), and the Settings client-ID box writes only the legacy field (`SettingsViewModel.cs:1489` `_settings.Update(s => s.TraktClientIdOverride = value)`). `GetActiveClientId()` therefore returns `""`. *Runtime-confirm* the device-code request failure. Legacy flow (`TraktService.cs:171-212`): device flow **with `client_secret`**, writing plaintext tokens to settings (`:190`). |
| 8.3 Scrobble call sites | **Partial** | Queue path: `MainViewModel.cs:1150-1156` (start), `:227-240` (pause/resume), `:1274-1278` (stop) → `TraktScrobbleService.cs:27-60` → `TraktQueueDrainWorker.cs:33-150` | Queue path skips provisional groups (`:38`) and applies D7 using the **primary** version's origin, not the played version's (`:45`). It drops start/pause rows older than 5 min (`Drain :61-66`) and treats 409 as success (`:129-134`). Stop progress comes from `_lastPlaybackSeconds`, which is **0 on every Esc** (1.2). Episode payloads take ids from group keys; over-merged groups yield an arbitrary episode id (last key wins, `TraktScrobbleService.cs:112-127`). Legacy direct path (`TraktService.cs:388-411`) is also called at `MainViewModel.cs:1149`, `:1270`, `MediaCard.xaml.cs:222`, `HomeViewModel.cs:491`, `LibraryViewModel.cs:415`, `DetailViewModel.cs:671`. It's dormant because `TraktService.IsConnected` reads `settings.TraktAccessToken` (`:138`), and it would **double-scrobble** if a legacy token existed. The same flag gates the sync-time watchlist pull (`SyncEngine.cs:638`) and the sidebar shelf (`LibraryViewModel`), so both stay off even when the new auth is connected. |
| 8.4 Token refresh | **Partial** | `TraktAuthService.cs:234-324`; called only from `TraktQueueDrainWorker.cs:44` (before drain) and `:140` (on 401) | Proactive within 24 h (`:250`), but **not at app start** (spec §6.3). Any exception, including a transient network error, falls through to `ClearTokens(); SaveState(); SetConnectionState(NeedsReconnection)` (`:314-323`), so being offline near expiry wipes the tokens. `SaveState` **throws on UTC+ machines** after any token clear: `ClearTokens` sets `ExpiresUtc = DateTime.MinValue` (`:359`), and `:438` `((DateTimeOffset)_state.ExpiresUtc)` applies the local offset. The visible state is never set. Reproduced by the failing test `ClientIdUsedForToken_InvalidationGuard` (`ArgumentOutOfRangeException` at `TraktAuthService.cs:438`). Fallback lifetime hard-coded to 90 days (`:204`, `:303`). |

### Group 9 — Cross-cutting

| Item | Status | Evidence | Note |
|---|---|---|---|
| 9.1 Culture-sensitive parsing | **Partial** | `TraktSyncService.cs:172`, `:216`, `:255`, `:281` `DateTimeOffset.TryParse(m.LastWatchedAt, out var dt)` (no culture / styles) | All Emby payloads go through System.Text.Json (invariant); `FlexibleStringConverter` uses `CultureInfo.InvariantCulture` (`EmbyItem.cs:449`); mpv args (`MpvController.cs:206`, `:212`, `:265`), `WatchPartyService.cs:343-346` and `Converters.cs:133` are invariant. `int/long.TryParse` calls are on internal ids and regex digits (no grouping allowed by default `NumberStyles.Integer`). The 4 Trakt date parses use the current culture's calendar (*runtime-confirm* under e.g. `th-TH`). Related **timezone** defect: `TraktAuthService.cs:438` (8.4) fails on every UTC+ machine, including this one. |
| 9.2 Logging | **Divergent** | `NocturneLogger.cs:17` `Path.Combine(dir, "nocturne.log")`; `:36-46` `File.AppendAllText(...)` + `Console.WriteLine` | `%APPDATA%\Nocturne\nocturne.log`, all levels, **no rotation, no redaction** (spec §6.2 requires redaction rules). Separate `crash.log` (`App.xaml.cs:57`). **Leaks:** full stream URLs with the Emby `api_key` (`MpvController.cs:281` `$"Embedded playback started: {title} ({url}) …"`, `:319`); **19 occurrences** in the live log. The unit tests write into the same production log, because `NocturneLogger` is static with a fixed path. |
| 9.3 Swallowing outside the DB | **Present** (defect class) | `EmbyClient.cs:786` `catch { }`; `MainViewModel.cs:1272`, `:1284`, `:1292`, `:1349` silent in finalize; `:943`, `:1140`; `MainViewModel.cs:1373` result of `MarkPlayedAsync` ignored, then `:1379` `RemoveSiblingWrite` | Every `EmbyClient` method catches, logs, and returns an empty list / false / null, so callers can't tell failure from empty (`GetViewsAsync` → server silently dropped from sync, 1.4). **The sibling queue drops writes on failure:** the bool is ignored and the row deleted, so the "persistent" queue never retries. It's also only drained after a playback, never at startup (2 rows pending live). `SettingsService.cs:24-40` load failure returns defaults (logged to `Console` only, invisible in a WinExe), and the next `Update` overwrites the user's servers and tokens; `Save` is a non-atomic `File.WriteAllText` (`:46-47`). `CredentialProtection` falls back to XOR (8.1). `MpvController` has 21 silent catches (StopAsync/Dispose/IPC). `App.xaml.cs:24-41` marks every dispatcher exception handled after a MessageBox, and `:43-47` observes unobserved task exceptions into `crash.log` only, where every `_ = …Async()` failure ends up. |
| 9.4 Tests | **Partial** | `Nocturne.Tests/` (15 files) | **90 tests, 89 pass, 1 fail** (`TraktAuthTests.ClientIdUsedForToken_InvalidationGuard`, 8.4). Coverage: `AdaptiveBatchController` (8), `ColorContrastUtils` (7), `ConcurrentDedupIndex` (7), `DatabaseService` (8: CW cache, checkpoint atomicity, …), `DedupEngine.Deduplicate` (5), `EmbyClient` payload/config (9), `EmbyConnect` DTOs (5), `IntroDb` (2), `ThemeColorService` (3), TMDB rate limit (2), Trakt auth/PKCE/poll mapping (4), scrobble payload/provisional skip (4), Trakt pull idempotence (1), watch-party init (3), merged watchlist (2), plus theory cases. **Not covered:** playback finalize / stop paths, `SyncEngine`, `SwapInRebuild`, `ClearServerData`, any multi-server routing, `GetSiblingVersions`. The name+year dedup test passes only because fixtures set `ProductionYear`, which production sync never receives. |

---

## 3. Top 10 risks

Ordered by what corrupts or loses data silently first; cosmetic last.

1. **Stops overwrite resume state on the server.** Every Esc, Stop, close and previous-episode
   reports `PositionTicks = 0` to the owning server and writes nothing locally (`MainViewModel.cs:1408`
   → `:1413` → `:1263`, `:1282`). No progress is ever reported mid-playback. Resume is non-functional
   (log: 7 plays of one movie, all from tick 0). Whether Emby *resets* an existing resume point on
   `Stopped` with 0 is *runtime-confirm*.
2. **"Played" written on non-completion paths, fanned out to every server.** Manual Next (`isEof:
   true`), any mpv `end-file` including stream errors, and process exit all route to the EOF path;
   Remove from Continue Watching marks the group played. Each enqueues `markPlayed` for every
   other-server sibling (Table 1.3). This is the v3.5 RC6 class, reintroduced.
3. **Dedup over-merging amplifies #2 and corrupts identity.** Fuzzy-key attach to authoritative
   groups has no cap and no conflict check, authoritative merges are transitive, and malformed
   provider ids are trusted (43-episode group; 87 movie groups with ≥2 TMDB ids; 3,732 groups with
   >1 `tvdb:ep` key). Watched-state writes, Trakt pulls and scrobble payloads all act on whole groups.
4. **A dedup-index load failure overwrites existing groups.** `InitializeFromDb` resets to empty and
   `_nextGroupId = 1` before its `try`, swallows the failure, and the sync then upserts colliding
   group ids over unrelated rows (`ConcurrentDedupIndex.cs:120-158`, `DatabaseService.cs:1993`).
5. **Server state never re-syncs and nothing is ever deleted.** Completed libraries with unchanged
   counts are skipped forever (`SyncEngine.cs:196-199`); an unreachable server is silently dropped and
   the sync reports `complete`; no delete path exists for removed items or unmapped libraries; server
   removal strands groups whose primary was on that server.
6. **The sibling write queue drops writes on failure** (`MainViewModel.cs:1373-1379`) and only drains
   after a playback, so Trakt-pulled and cascaded writes to an offline server are lost without a
   trace.
7. **Cross-server identity leaks.** 26 of 40 URL/token sites read ambient state; `ServerId` is
   `[JsonIgnore]`. Card context menus always target the active server. Id-only SQL writes touch the
   other server's row on the 4 colliding ids. The failover start report uses the wrong item id.
   Auto-advance from the fallback fetch plays and finalizes against the active server.
8. **Settings file loss.** A transient load failure returns defaults and the next save overwrites
   all servers and tokens (`SettingsService.cs:24-54`); saves are non-atomic.
9. **Credential exposure.** Emby `api_key` in plaintext in `nocturne.log` (19 URLs), Emby tokens in
   plaintext `settings.json`, and a silent DPAPI→XOR downgrade in `CredentialProtection`.
10. **Integration and UX gaps.** Trakt can't connect (empty bundled ID, custom ID not wired; UTC+
    `SaveState` crash; refresh wipes tokens on network errors; latent double scrobble).
    `ProductionYear`/`PremiereDate` are never synced (no year anywhere, no movie fuzzy keys). The
    `year` SQL error breaks detail loads (fired 4×). Library pages capped at 100–200 items; local
    search scans 50 rows; FTS unused. No list virtualization; 1.2 GB working set vs ~45 MB claimed.

---

## 4. Spec deltas

Places where the code does something defensible that the spec doesn't describe, or where the spec
looks wrong. Flagged, not judged.

- **`integration_state` key.** Code: `PRIMARY KEY (name, key)`. Spec §4.5: `name TEXT PRIMARY KEY`
  with `key`/`value` columns, which allows one row per integration while §8/§10 store several
  markers per integration. Likely a **spec bug**; the code's composite key is what the spec's usage
  needs.
- **Fuzzy group cap = `4 × max(2, serverCount)`** (`ConcurrentDedupIndex.cs:412`), which is 8 for two
  servers. r2 M6 called 50 too loose, and r3 "Minor" says two editions × two servers legitimately
  reaches 8. The spec number isn't available to compare; note the cap applies only to the
  provisional path (4.2).
- **Server-local fuzzy episode key** `ep:series:{SeriesId}:sXXeYY`. Not one of the forms in r1 B2
  (`ep:grp:{series_group_id}…`). It collapses within-server duplicates cheaply but can collide across
  servers, because Emby ids are per-server integers.
- **`continue_watching_cache` and `media_stream_preferences`** are v4 additions with no spec text: a
  last-rendered CW list for instant paint, and per-series audio/subtitle language memory.
- **Emby favourites feed `watchlist_entries` during sync** (`DatabaseService.cs:759-772`). That fits
  D10's "display-only union", but the spec has no removal rule for an un-favourited item.
- **Adaptive batch sizing (250–25,000) and a bounded channel of 8.** Sensible for mixed-latency
  servers; the spec (as quoted by reviewers) doesn't size batches.
- **Partial-position propagation to sibling servers** (`MainViewModel.cs:1329-1339`, via
  `ReportPlaybackStopped`). r1 B5 flagged this exact behaviour as reintroducing the audited bug class.
  Whether v4.4 kept, dropped or constrained it isn't visible without the frozen plan.
- **Playback candidate order** is quality → bitrate → server priority across *all* siblings
  (`MainViewModel.cs:994-1001`), regardless of which version the user clicked unless
  `FallbackCopies` is set. r1 P1 asked for a capability filter before the quality sort; none exists.
- **One mpv process per playback** instead of v3.5's long-lived idle process. Simplifies `--wid`
  re-parenting; contradicts CLAUDE.md's "no process restart" description, which is v3.5 text.

---

## 5. Open questions

| Question | What would settle it |
|---|---|
| What exactly did the frozen dedup plan v4.4 §2 / merge rules say? This audit reconstructs it from reviews r1–r4. | Commit the v4.4 plan to `docs/` and re-run the §3.2 / §4 comparison. |
| Does Emby reset or overwrite a stored resume point when it receives `/Sessions/Playing/Stopped` with `PositionTicks = 0` (1.2, risk #1)? | On a test item with a known resume point, play from it and press Esc; read `/Users/{id}/Items/{item}` `UserData` before and after. |
| Does mpv with `--idle=yes --keep-open=yes` emit `end-file` (reason `error`) on mid-stream network failure, and does that arrive after the 1.5 s start window (the mark-played-on-error path)? | Play a stream, block the server mid-playback, and capture the IPC events. |
| Natural EOF behaviour in the embedded player (expected: freezes on the last frame, no auto-advance). | Smoke-test item 7; watch for an `Auto-advancing` log line. |
| v3 baseline definitions: is "32,366 groups / 73,895 merged" multi-version groups and items within them, or something else? | The v3 query or report that produced those numbers. |
| Is `ProductionYear` / `PremiereDate` absent because `FullSyncItemFields` omits them, or a server-side issue? | One `/Users/{id}/Items?Ids=…` request with and without `Fields=ProductionYear,PremiereDate`. |
| Actual per-connection PRAGMA values (`synchronous`, `busy_timeout`, `foreign_keys`) in the running process. | Log `PRAGMA …` results once after `Initialize()`. |
| Attribution of the 1.2 GB working set (dedup index vs batches vs WPF/images). | `dotnet-counters` / `dotnet-gcdump` during and after a sync. |
| Is `MediaRow` virtualization effective? | Count realized containers in a live visual tree (Snoop / VS Live Visual Tree) on a 20-item row. |
| Why was the DB recreated at 14:20 IST today (hash index `0 existing groups`, `nocturne.log:1280`), and were `watch_events` lost (0 rows)? `ClearAllCache` preserves them only on its file-delete path. | User recollection of a reset, or log lines just before 1280. |
| Where did the 2 pending `sibling_write_queue` rows (Valkyrie `7333698`, `7326203`, `mark_played = 1`, 14:59:53 IST) come from? Trakt isn't connected. | Log around 14:59 IST; add a source tag to the queue row. |
| Is the installed binary built from `cc33e05`? The log's stack traces cite line numbers from earlier builds. | Embed the commit hash in the assembly version and log it at startup. |
| Does Emby serve the correct image when `Tag` belongs to a different item (MediaCard passes the item's tag with `ImageItemId`)? | One request with a mismatched tag. |
| How does Trakt respond to a device-code request with `client_id: ""` (8.2), and what does the Settings page show? | Click Connect in Settings on a clean profile. |

---

## Appendix A — live schema dump

`SELECT type, name, sql FROM sqlite_master ORDER BY type, name;` against the live database,
read-only, 2026-10-21. Whitespace normalized; FTS5 shadow tables listed by name only.

```sql
-- index idx_cached_items_dedup
CREATE INDEX idx_cached_items_dedup ON cached_items(dedup_group_id);
-- index idx_cached_items_imdb
CREATE INDEX idx_cached_items_imdb ON cached_items(imdb_id);
-- index idx_cached_items_parent
CREATE INDEX idx_cached_items_parent ON cached_items(parent_id);
-- index idx_cached_items_series
CREATE INDEX idx_cached_items_series ON cached_items(series_id);
-- index idx_cached_items_tmdb
CREATE INDEX idx_cached_items_tmdb ON cached_items(tmdb_id);
-- index idx_cached_items_type
CREATE INDEX idx_cached_items_type ON cached_items(type);
-- index idx_watch_events_source
CREATE INDEX idx_watch_events_source ON watch_events(source);
-- index idx_watch_events_time
CREATE INDEX idx_watch_events_time ON watch_events(watched_at);
-- index ix_dedup_groups_primary
CREATE INDEX ix_dedup_groups_primary ON dedup_groups(primary_item_id, primary_server);
-- index ix_dedup_groups_series
CREATE INDEX ix_dedup_groups_series ON dedup_groups(series_group_id, season_number, episode_number);
-- index ix_dedup_groups_shelf
CREATE INDEX ix_dedup_groups_shelf ON dedup_groups(item_type, date_created DESC);
-- index ix_dedup_groups_sort
CREATE INDEX ix_dedup_groups_sort ON dedup_groups(item_type, sort_name);
-- index ix_dedup_keys_group
CREATE INDEX ix_dedup_keys_group ON dedup_keys(group_id);
-- index ix_groups_needs_art
CREATE INDEX ix_groups_needs_art ON dedup_groups(group_id) WHERE image_tag IS NULL AND tmdb_key IS NOT NULL;
-- index ix_items_group
CREATE INDEX ix_items_group ON cached_items(group_id);
-- index ix_items_server_sync
CREATE INDEX ix_items_server_sync ON cached_items(server_id, last_synced);
-- index ix_trakt_queue_order
CREATE INDEX ix_trakt_queue_order ON trakt_queue(queued_utc ASC);
-- index ix_user_data_continue
CREATE INDEX ix_user_data_continue
ON user_data(last_played_utc DESC)
WHERE position_ticks > 0 AND played = 0;
-- index ix_watchlist_entries_added
CREATE INDEX ix_watchlist_entries_added ON watchlist_entries(added_utc DESC);
-- index sqlite_autoindex_cached_items_1
-- (auto)
-- index sqlite_autoindex_continue_watching_cache_1
-- (auto)
-- index sqlite_autoindex_media_stream_preferences_1
-- (auto)
-- index sqlite_autoindex_migration_state_1
-- (auto)
-- index sqlite_autoindex_sibling_write_queue_1
-- (auto)
-- index sqlite_autoindex_sync_checkpoints_1
-- (auto)
-- index sqlite_autoindex_trakt_watched_history_1
-- (auto)
-- index sqlite_autoindex_trakt_watchlist_1
-- (auto)
-- index sqlite_autoindex_watch_events_1
-- (auto)
-- table cached_items
CREATE TABLE cached_items (
id TEXT NOT NULL,
server_id TEXT NOT NULL,
parent_id TEXT,
group_id INTEGER,
name TEXT NOT NULL,
type TEXT NOT NULL,
run_time_ticks INTEGER,
production_year INTEGER,
community_rating REAL,
index_number INTEGER,
parent_index_number INTEGER,
series_id TEXT,
series_name TEXT,
season_id TEXT,
season_name TEXT,
genres TEXT,
tmdb_id TEXT,
imdb_id TEXT,
dedup_group_id TEXT,
height INTEGER,
width INTEGER,
video_codec TEXT,
audio_codec TEXT,
bitrate INTEGER,
size_bytes INTEGER,
runtime_seconds INTEGER,
hdr_type TEXT,
edition TEXT,
json_data TEXT NOT NULL,
last_synced INTEGER NOT NULL,
PRIMARY KEY (id, server_id)
);
-- table continue_watching_cache
CREATE TABLE continue_watching_cache (
id TEXT NOT NULL,
server_id TEXT NOT NULL,
sort_order INTEGER NOT NULL,
json_data TEXT NOT NULL,
updated_at INTEGER NOT NULL,
PRIMARY KEY (id, server_id)
);
-- table dedup_groups
CREATE TABLE dedup_groups (
group_id INTEGER PRIMARY KEY,
item_type TEXT NOT NULL,
primary_item_id TEXT NOT NULL,
primary_server TEXT NOT NULL,
is_provisional INTEGER NOT NULL DEFAULT 0,
version_count INTEGER NOT NULL DEFAULT 1,
name TEXT NOT NULL,
sort_name TEXT,
year INTEGER,
overview TEXT,
image_tag TEXT,
backdrop_tag TEXT,
community_rating REAL,
date_created INTEGER,
series_group_id INTEGER,
season_number INTEGER,
episode_number INTEGER,
max_height INTEGER,
has_4k INTEGER NOT NULL DEFAULT 0,
max_bitrate INTEGER,
audio_formats TEXT,
image_item_id TEXT,
image_server_id TEXT,
backdrop_item_id TEXT,
backdrop_server_id TEXT,
tmdb_key TEXT
);
-- table dedup_groups_fts
CREATE VIRTUAL TABLE dedup_groups_fts USING fts5(
name,
sort_name,
content='dedup_groups',
content_rowid='group_id'
);
-- table dedup_keys
CREATE TABLE dedup_keys (
key TEXT PRIMARY KEY,
group_id INTEGER NOT NULL,
is_authoritative INTEGER NOT NULL DEFAULT 1
) WITHOUT ROWID;
-- table integration_state
CREATE TABLE integration_state (
name TEXT NOT NULL,
key TEXT NOT NULL,
value TEXT NOT NULL,
PRIMARY KEY (name, key)
) WITHOUT ROWID;
-- table media_stream_preferences
CREATE TABLE media_stream_preferences (
media_key TEXT PRIMARY KEY,
audio_track_id INTEGER,
audio_language TEXT,
subtitle_track_id INTEGER,
subtitle_language TEXT,
updated_at INTEGER NOT NULL
);
-- table migration_state
CREATE TABLE migration_state (
key TEXT PRIMARY KEY,
value TEXT NOT NULL
);
-- table sibling_write_queue
CREATE TABLE sibling_write_queue (
queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
server_id TEXT NOT NULL,
item_id TEXT NOT NULL,
position_ticks INTEGER,
mark_played INTEGER NOT NULL DEFAULT 0,
queued_utc INTEGER NOT NULL,
attempts INTEGER NOT NULL DEFAULT 0,
UNIQUE(server_id, item_id)
);
-- table sqlite_sequence
CREATE TABLE sqlite_sequence(name,seq);
-- table sync_checkpoints
CREATE TABLE sync_checkpoints (
server_id TEXT NOT NULL,
library_id TEXT NOT NULL,
start_index INTEGER NOT NULL,
total_items INTEGER NOT NULL,
is_completed INTEGER NOT NULL DEFAULT 0,
last_synced INTEGER NOT NULL,
PRIMARY KEY (server_id, library_id)
);
-- table tmdb_metadata
CREATE TABLE tmdb_metadata (
provider_key TEXT PRIMARY KEY,
poster_path TEXT,
backdrop_path TEXT,
still_path TEXT,
overview TEXT,
vote_average REAL,
fetched_utc INTEGER NOT NULL,
not_found INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;
-- table trakt_queue
CREATE TABLE trakt_queue (
queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
group_id INTEGER NOT NULL,
action TEXT NOT NULL,
progress REAL NOT NULL DEFAULT 0,
payload TEXT,
queued_utc INTEGER NOT NULL,
attempts INTEGER NOT NULL DEFAULT 0
);
-- table trakt_watched_history
CREATE TABLE trakt_watched_history (
key TEXT PRIMARY KEY,
trakt_type TEXT NOT NULL,
tmdb_id TEXT,
imdb_id TEXT,
show_tmdb_id TEXT,
season_number INTEGER,
episode_number INTEGER,
watched_at INTEGER NOT NULL,
synced_at INTEGER NOT NULL
);
-- table trakt_watchlist
CREATE TABLE trakt_watchlist (
id TEXT PRIMARY KEY,
trakt_id INTEGER,
tmdb_id TEXT,
imdb_id TEXT,
title TEXT NOT NULL,
year INTEGER,
type TEXT NOT NULL,
added_at INTEGER NOT NULL
);
-- table user_data
CREATE TABLE user_data (
server_id TEXT NOT NULL,
item_id TEXT NOT NULL,
position_ticks INTEGER NOT NULL DEFAULT 0,
runtime_ticks INTEGER,
played INTEGER NOT NULL DEFAULT 0,
last_played_utc INTEGER,
origin TEXT NOT NULL DEFAULT 'local',
updated_locally INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY (server_id, item_id)
) WITHOUT ROWID;
-- table watch_events
CREATE TABLE watch_events (
id TEXT PRIMARY KEY,
item_id TEXT NOT NULL,
server_id TEXT NOT NULL,
item_name TEXT NOT NULL,
series_name TEXT,
item_type TEXT NOT NULL,
duration_seconds INTEGER NOT NULL,
watched_at INTEGER NOT NULL,
source TEXT NOT NULL,
genres TEXT
);
-- table watchlist_entries
CREATE TABLE watchlist_entries (
source TEXT NOT NULL,
ref TEXT NOT NULL,
item_type TEXT NOT NULL,
added_utc INTEGER NOT NULL,
title TEXT,
year INTEGER,
tmdb_key TEXT,
pending_op TEXT,
PRIMARY KEY (source, ref)
) WITHOUT ROWID;
-- trigger dg_fts_ad
CREATE TRIGGER dg_fts_ad AFTER DELETE ON dedup_groups BEGIN
INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
VALUES ('delete', old.group_id, old.name, old.sort_name);
END;
-- trigger dg_fts_ai
CREATE TRIGGER dg_fts_ai AFTER INSERT ON dedup_groups BEGIN
INSERT INTO dedup_groups_fts(rowid, name, sort_name)
VALUES (new.group_id, new.name, new.sort_name);
END;
-- trigger dg_fts_au
CREATE TRIGGER dg_fts_au AFTER UPDATE ON dedup_groups BEGIN
INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
VALUES ('delete', old.group_id, old.name, old.sort_name);
INSERT INTO dedup_groups_fts(rowid, name, sort_name)
VALUES (new.group_id, new.name, new.sort_name);
END;
-- FTS5 shadow tables: dedup_groups_fts_config, dedup_groups_fts_data, dedup_groups_fts_docsize, dedup_groups_fts_idx
```
