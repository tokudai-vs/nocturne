# Nocturne v4 — Remediation pass, October 2026

Work order: `docs/claude-code-brief-2026-10-21.md` (Stages A–I) against `docs/AUDIT-2026-10-v4-baseline.md`,
building toward `docs/multi-quality-dedup-plan-v4.4.md`. Tracking issue #18. One commit per stage:

| Stage | Commit | Tests after the stage |
|---|---|---|
| docs (audit, brief, plan, reviews) | `8b328e1` | 89/90 (pre-existing Trakt failure) |
| A — stop writing wrong state to the servers | `0b019ab` | 116/117 |
| B — instant startup | `b102e4f` | 117/118 |
| C — fix the dedup inputs, then re-measure | `2c9a371` | 145/146 |
| D — the four silent failures | `330bc58` | 156/157 |
| E — correctness hygiene | `4aa66e0` | 167/168 |
| F — Watch Party | `5d46cad` | 175/176 |
| G — adaptive batch sizing | `68c9367` | 182/183 |
| H — onboarding | `f21eaa7` | 184/185 |
| I — Trakt wiring | `ae0d4ac` | 193/193 |

The one failure carried through A–H is `TraktAuthTests.ClientIdUsedForToken_InvalidationGuard`, the real bug the brief
names; it is fixed in Stage I (I3), not Stage E as the brief's rule 2 says. No stage ended with a new failure.

**Waiting on you:** the production Trakt client ID (I1). `TraktAuthService.BundledClientId` is empty with a
`// TODO(client-id)` marker; until it is filled in, Trakt connects only with a custom client ID (Settings → Trakt →
Client ID), which works end to end. The legacy `TraktService` carried a hard-coded client ID
(`TraktService.cs:105` before Stage I); it was removed with the legacy auth and is not reproduced here. If that was
the production ID, it is the value for `BundledClientId`.

Your profile already has a custom Client ID: the one saved in Settings, which the Stage I startup step moved into
the Trakt auth store on 2026-10-22 01:11. Trakt is not connected on this profile (no tokens). Connecting from
Settings → Trakt uses that ID.

**Also flagged for you:** a Watch Party with a real remote guest over a public trycloudflare link (not opened, per
instruction). Everything else was verified in local-only mode.

---

## 1. Outcome of every brief item

Outcomes: **Fixed** / **Partially fixed** / **Not started** / **Found not to apply**.

### Stage A — stop writing wrong state to the servers (`0b019ab`)

| # | Outcome | Notes |
|---|---|---|
| A1 | Fixed | Playback state moved into `PlaybackSession` (`Nocturne.Core/Services/PlaybackSession.cs`); the position is read before any reset. Esc/Stop/window close report the real position. |
| A2 | Fixed | Previous-episode path finalizes with `PlaybackStopReason.Previous` before the UI reset. |
| A3 | Fixed | Next passes the real progress (`PlaybackStopReason.Next`); only ≥ 90 % marks played. |
| A4 | Fixed | `MpvController.PlaybackEnded(MpvEndReason)` parses `end-file` `reason`; only `eof` (and `eof-reached` with `keep-open=yes`, previously unwired) completes and auto-advances. |
| A5 | Fixed | Remove-from-CW sends `DELETE /PlayingItems/{id}?PositionTicks=0` and clears local resume; never marks played. |
| A6 | Fixed | `FeatureFlags.SiblingCascade` off; brought back in C7, gated (see C7). |
| A7 | Fixed | Sibling queue rows are removed only when the server accepted the write; failures count attempts (cap 10, kept, not deleted). |
| A8 | Fixed | Progress reported every 10 s and on pause/resume. |
| Tests | Fixed | 27 tests over every stop path (Stop, Esc, window close, previous, next, EOF, mpv error/quit) asserting local position and reported ticks. Window close now awaits the stop report (5 s cap). |

### Stage B — instant startup (`b102e4f`)

| # | Outcome | Notes |
|---|---|---|
| B1 | Fixed | `[Startup]` lines for each step. Warm, Debug, isolated profile: `MainWindow.Show` 4,996 → 320 ms; first render 6,286 → 1,537 ms. |
| B2 | Fixed | `Resources/splash.png` as `SplashScreen`. |
| B3 | Fixed | Cache reads moved off the UI thread, single-flight `HomeViewModel.LoadAsync`, skeleton shelves. |
| B4 | Fixed | Index loads on its own read-only connection (no `_dbLock`) at sync start, not on first paint. Still reloaded on every sync and still ~1.7 M keys in memory; not in scope. |
| B5 | Partially fixed | `InitializeAsync` failure shows an in-window error, not an endless splash. Failures after the window is shown are not covered by this path. |

### Stage C — dedup inputs (`2c9a371`)

| # | Outcome | Notes |
|---|---|---|
| C1 | Fixed | `sXXeYY` keys only with both `IndexNumber` and `ParentIndexNumber` (the brief named only `IndexNumber`; 169 episodes had a null season). Keyless items get a stable identity key. |
| C2 | Fixed | Series provider ids from the cached Series row, no round-trip. `SeriesId` was in `json_data` for 567,418/567,418 episodes. |
| C3 | Fixed | Live check first (Valkyrie, one movie): without `ProductionYear,PremiereDate` both came back null; with them, 1986 / 1986-12-12. The field list was the cause; both added. |
| C4 | Fixed | Merge refused on conflicting ids of the same provider type; shared key demoted. Evidence is every id a group's members carry (owned provider keys incl. demoted ones, plus `{key}@{groupId}` evidence rows). A group never takes another group's key or re-promotes a demoted one. Malformed provider ids are not keys. |
| C5 | Fixed | Cap and conflict guard on the fuzzy → authoritative path. |
| C6 | Fixed | Rebuild of a copy of the old live DB: conflicting same-provider groups 7,584 → 0. The fresh-sync baseline is in §2. |
| C7 | Fixed | Cascade engaged only when `migration_state.dedup_engine = "2"` (set by a rebuild with this engine or by indexing from empty). The fresh DB of §2 was indexed from empty, so the cascade is on there. |

### Stage D — the four silent failures (`330bc58`)

| # | Outcome | Notes |
|---|---|---|
| D1 | Fixed | `InitializeFromDb` loads into locals, swaps on success, throws on failure (the sync reports it). |
| D2 | Fixed | Settings load failure logged and surfaced at startup (start on defaults, with the bad file kept, or exit); never saved over; temp-file-and-rename writes. |
| D3 | Fixed | DPAPI or nothing; foreign data throws instead of returning XOR garbage. |
| D4 | Fixed | Decision: v4 has its own `nocturne-v4.db` and `nocturne-v4-settings.json`; v3.5 keeps `nocturne.db` / `nocturne-settings.json`. v4 no longer drops v3.5's tables. Migration ran on a profile copy; on the live profile the DB was deleted on your instruction before it could run. |
| `SwapInRebuild` lock | Partially fixed | Build phase no longer holds `_dbLock`; `DedupEngine.WriterGate` serialises sync and rebuild. A first rebuild still blocks UI reads ~3.8 min (state write 48 s + swap 179 s). **Deferred** (needed by the plan's periodic reconcile). |
| `ClearAllCache` path | Fixed | Uses the constructor's path; no longer deletes v3.5's `image-cache`. |

### Stage E — correctness hygiene (`4aa66e0`)

| # | Outcome | Notes |
|---|---|---|
| E1 | Fixed | `year` → `production_year` in the sibling name+year lookup. |
| E2 | Fixed | Every literal statement (91) is registered at type init and the self-check prepares exactly those. 11 of the old list's 23 strings were not what the code ran. First run found exactly one failure: E1. |
| E3 | Fixed | Logger redacts `api_key`, tokens, `device_code`, `code_verifier`, `client_secret`, Emby token headers; rotates at 5 MB. |
| E4 | Fixed | `NocturneLogger.LogPath` injectable; tests run against `%TEMP%\nocturne-tests` (logging and every `AppPaths` default). |
| E5 | Fixed | The 16 remaining silent DB read catches log the statement and rethrow in Debug. |

### Stage F — Watch Party (`5d46cad`)

Investigation findings: §3. Approved changes:

| Item | Outcome | Notes |
|---|---|---|
| 1. Resume-point offset | Fixed | Positions converted to stream time at source (`WatchPartyService.ToStreamTime`); `startOffsetSec` removed from `session_info`. |
| 2. Guest cap | Fixed | `WatchPartyService.MaxGuests = 10`; the 11th socket gets `session_full` and close 1008; the guest page shows "This watch party is full" and stops reconnecting. |
| 3. Teardown | Fixed | ffmpeg killed first, guests messaged (bounded), then cloudflared; `Dispose` kills synchronously. mpv, ffmpeg and cloudflared run in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`Utils/ChildProcessJob.cs`). |
| 4. Tunnel failure | Fixed | No tunnel URL within 30 s fails the start with a message in the modal; no `127.0.0.1` share link. |
| Bundle hls.js | Fixed | hls.js 1.6.16 (v3.5's pinned dependency, Apache-2.0) embedded and served at `/hls.min.js`; no jsDelivr. |
| Pin + verify binaries | Fixed | ffmpeg 8.1.1 essentials and cloudflared 2026.5.0, pinned SHA-256 (GitHub release digests; cloudflared's also matches its release notes). Download hash checked before install; installed executables re-hashed before each session. No PATH / app-dir fallbacks. |
| Guests behind | Fixed | Guests report position every 2 s; the banner shows "Guests are N s behind". Host pause not added (as instructed). |
| Local-only mode | Fixed | `StartSessionAsync(publicLink: false)`: 127.0.0.1 only, no cloudflared. |
| Docs correction | Fixed | No `docs/nocturne-v4-handover.md` exists in the repo; the claim was in the brief (§F.6), corrected there with the enforcing line `src/main/watchparty-sync-server.ts:86` (commit `2bbfae5`). |
| Found during verification | Fixed | (a) Overlapping `SendAsync` on one guest socket (mpv events + heartbeat timer) made the server abort guests every 15–25 s. (b) `StopSessionAsync` threw "Collection was modified" with guests connected; in the shipped order that was before ffmpeg and cloudflared were killed, so **End** with guests left both running. |
| Real-guest test | **Not started — flagged for you** | Needs a public trycloudflare link and a remote browser; not opened, per instruction. |

Verification (local-only, real ffmpeg, headless Edge guest, party started from a 60 s resume point): host at movie
time 120.3 s = stream 59.8 s, guest 0.4 s behind, connected for the full 60 s, Stop in 26 ms, every guest browser
process gone after the harness exited (Job Object).

### Stage G — adaptive batch sizing (`68c9367`)

| # | Outcome | Notes |
|---|---|---|
| G1 | Fixed | Per-server model, no absolute threshold; a 6 s-per-request server reaches 25,000. |
| G2 | Fixed | Request time is fitted as `fixed + perItem × size` (least squares over the last 8 full pages). The next size is the largest predicted to finish within the time budget, approached in ×1.25 / ×0.75 steps. The budget is 15 s (half the 30 s timeout), stretched to `1.25 × fixed` up to 22.5 s when the fixed cost alone approaches it. Warm-up is 3 requests. The per-server baseline is a slow EWMA of the per-item slope, seeded from the first 5 fits, with its rise clamped to 1 % per request. Short final pages are not samples. Only a request over 22.5 s shrinks the batch outright. Baseline and current are logged with every batch. |

The brief's suggested shape (ms/item EWMA with grow/shrink bands) was built first and failed on the live sync. 100k
items into Valkyrie's TV library, each request pays ~10 s of fixed cost (Emby pages with OFFSET). Dividing by the item
count leaves that cost in, so shrinking the batch raised ms/item, which shrank the batch further, down to 250 items
(24 items/s). The fit separates the two terms.

The first version of the fit then failed further in. Its budget was a fixed 15 s, and every request over it shrank the
batch. From ~300k items in, Valkyrie took 13–20 s per request at any size: 250 items took 19 s, 2,081 took 17 s. The
batch fell to the minimum and stayed there for about two hours at 11–43 items/s. That run averaged 63 items/s over
571k items (its first 20 minutes, at ~10 s of fixed cost: ~206 items/s). Fixed in the same commit: the budget
stretches with the fixed cost, and the minimum always probes upward.

Live check against the version that ran overnight. Read-only requests on the same library at offsets 400k–520k,
interleaved request by request, 25 each:

| Controller | Items | Request time | Throughput | Batch sizes |
|---|---|---|---|---|
| As run overnight | 7,171 | 426 s | 17 items/s | 250–608 |
| Committed (`68c9367`) | 108,644 | 453 s | **240 items/s** | grew to 17,246, then settled near 9,000–13,000 |

Every committed-controller request took 14–22 s, with no failures. Tests: 15 (the original 8 rewritten for the new
semantics, plus the 6 s baseline, deep paging, noisy deep paging, fixed cost above the budget, the same with noise,
short page and transient slowdown). The two fixed-cost tests fail on the overnight version (250 and 258 items against
~9,000).

### Stage H — onboarding (`f21eaa7`)

Investigation findings: §4.

| Step | Outcome | Notes |
|---|---|---|
| Connection test with the real error | Fixed | `EmbyClient.CheckServerAsync` keeps the reason (DNS, TLS, refused, timeout, HTTP status); onboarding shows it. |
| Saved server unreachable ≠ session expired | Fixed | Reachability checked first; an offline server is reported, not sent to a sign-in. |
| First sync failure visible | Fixed | A server returning no libraries is counted as failed; the run ends *Partial* naming it (it used to report "completed successfully"). Partial/Error leave a warning pill with the message; Partial no longer leaves "Syncing…" up for good. |
| Library mapping / combined vs separate | Fixed (step was missing) | After the first completed sync with ≥ 2 servers, the user is asked combined vs separate, shown the groups the sync maintains. `FirstLaunchComplete` now means "library setup confirmed"; adding a server re-opens it. |

### Stage I — Trakt wiring (`ae0d4ac`)

| # | Outcome | Notes |
|---|---|---|
| I1 | **Waiting on you** | `BundledClientId = ""` with `// TODO(client-id)`. Custom client ID path tested end to end (device code → token → profile → reads). |
| I2 | Fixed | Settings → Client ID calls `SetCustomClientId`; the legacy `TraktClientIdOverride` is moved into the auth stack once at startup. On the live profile this ran at the 01:11 launch (logged); the field is now null. |
| I3 | Fixed | `SaveState` no longer casts `DateTime.MinValue` to `DateTimeOffset` (threw on UTC+). The pre-existing failing test passes. |
| I4 | Fixed | Refresh clears tokens only on 400/401; network errors, 5xx and 429 keep the session. |
| I5 | Fixed | Refresh at app start, before the watched-state pull. |
| I6 | Fixed | Legacy auth (device flow with client secret, plaintext tokens, hard-coded client ID) and legacy scrobbling removed; `TraktService` now only reads (stats, watchlist) through `ITraktAuthService`, so `IsConnected` follows the new auth (watchlist pull and sidebar shelf light up). Scrobbling is the queue path only: no double scrobble. |
| Cascade gate on pull | Fixed | `ApplyWatchedStateToGroup` marks the whole group only with the cascade engaged; otherwise the primary version. |
| Card context-menu Play | Fixed | Routed through `MainViewModel.StartPlaybackAsync` (it started mpv directly on the active server with no session; audit row 141). |

Verification of F–I as committed: each stage commit was built and tested on its own in a scratch worktree (the
counts in the table above). The final tree ran as a Release build on the live profile: it started, every
constructor resolved, nothing was logged at Error, and an incremental sync on the committed controller completed
successfully in 238 s (1,673,754 items indexed). Not
exercised in the app: the H library-setup prompt (it needs a first sync with two servers on a fresh profile; this
profile has already completed setup, `LibraryMode = combined`) and the connection-test error text (covered by
tests).

---

## 2. v4 baseline on clean data (fresh full sync)

The old database and log were deleted on 2026-10-21 (both settings files kept). The app created a fresh schema and
ran a full sync from 2026-10-21 23:56 to 2026-10-22 03:53 local (UTC+5:30), about 4 h. The app was restarted twice
mid-sync to change the batch controller; both times the sync resumed from its checkpoint. It ended "completed
successfully": 1,673,746 items across both servers. The figures below come from the resulting `nocturne-v4.db`,
read-only. The "old live DB" column is the incremental database the audit measured (711k rows, before Stage C);
its input differs, so it is context, not a like-for-like comparison.

| Metric | Fresh sync (v4 baseline) | Old live DB |
|---|---|---|
| Items (Episode / Movie / Series) | 1,673,746 (1,321,509 / 130,094 / 42,050) | 711k |
| Total groups | **1,129,251** | 574,086 |
| Multi-version groups (items in them) | **321,498** (685,900) | 121,118 |
| … spanning both servers | 291,261 | — |
| **Groups with conflicting same-provider ids** (explicit count) | **0** | 7,584 |
| Groups with ≥ 2 authoritative keys of one provider type | 0 | — |
| Movie groups with ≥ 2 distinct `tmdb_id` | 0 | 86 |
| **Groups holding more than one `tvdb:ep` key** | **0** | 7,254 |
| **Largest group** | **10 items** (1 group > 8: AEW Dynamite, 10 dated episodes Valkyrie tags all S20E22, one server, no ids to conflict on) | 65 |
| Episode groups spanning > 1 (season, episode) | 1,603: 1,597 share one TVDB episode id (same episode, numbered differently per server); **6 are wrong** (below) | 925 |
| Movie/Series/Episode items without a group | 0 | — |
| Movies with a production year (C3) | 129,831 / 130,094 | not measured (Valkyrie returned none before C3) |
| Demoted (suspect) provider keys / evidence rows | 9,179 / 9,704 | — |

Target met: zero conflicting-id groups on clean input.

**The 6 wrong groups** are a class the conflict guard cannot see. In each, a Valkyrie episode with no provider ids
at all (a file name like `The Yogi Bear Show (1961) - S02E10 - Yogi in the City WEBDL-1080p`) matched by season and
episode number into a group whose members carry the TVDB id of a different episode. Raptor numbers that episode
S02E10; Valkyrie numbers the same episode S02E04 and carries its id. Affected: The Yogi Bear Show S02E04, E07, E10
and E13; Super Friends S09E09; UNTOLD S06E05. With the cascade on, watching one of them marks a different episode
watched: on the other server for the Yogi Bear and Super Friends groups, on Valkyrie itself for UNTOLD. Not fixed
in this pass; listed in §6.

Index load at this size: 1,129,251 groups and 5,356,488 keys took 57 s at the start of the next sync. It runs on its
own read-only connection, so the UI is not blocked, but it repeats on every sync (B4).

Startup on this database (Release, warm, three launches): first render 1.86–1.90 s; view-model graph ~710 ms. The
first launch after a build was cold: 10.7 s, 8.3 s of it in the view-model graph.

---

## 3. Stage F — Watch Party investigation findings (before any change)

1. **UI entry point:** yes, reachable — Watch Party button on the detail page (`DetailPage.xaml:490`) →
   `DetailViewModel.OpenWatchPartyAsync` (downloads binaries on first use) → `StartWatchPartySessionAsync` →
   `WatchPartyService.StartSessionAsync`. Banner: Copy Link / Start show / End. The live log showed one real session
   (2026-10-19 22:27): tunnel up, host playing; guest joins and stop were not logged.
2. **Host and guest:** the host watches the original stream in mpv; `MainViewModel` broadcast heartbeat on every
   mpv position event plus a 1 s timer, play/pause from mpv pause events, seek only from skip prompts. Guests are a
   browser page (hls.js, then from jsDelivr) on a WebSocket, correcting drift by playback rate (< 3 s) or seek.
   Guest messages were discarded.
3. **No hls.js host player in v4.** The HLS never reaches `MpvController`; it exists only for guests. Because
   ffmpeg seeks with `-ss` before `-i`, the HLS timeline starts at the resume offset while the host broadcast
   absolute movie time: a party started from a resume point put guests that far ahead, past the transcoded part.
   v3.5's host watched the HLS itself, so its positions were already stream time.
4. **No Emby transcode/HLS session.** A local ffmpeg pulls Emby's direct static stream and writes HLS to a temp
   directory; Nocturne owns it and deletes it at stop. Same as v3.5.
5. **cloudflared:** involved and wired (quick tunnel). On a 30 s timeout the share link silently became
   `127.0.0.1`.
6. **Guest cap:** none in v4 — every socket admitted, no constant, no UI. v3.5 does enforce one, at
   `src/main/watchparty-sync-server.ts:86` (commit `2bbfae5`), contrary to the brief.

Also found: shutdown was a fire-and-forget stop that messaged guests before killing ffmpeg/cloudflared (orphan
risk); binaries were trusted on `File.Exists` and also taken from PATH; the guest link is the only access control
(as in v3.5).

## 4. Stage H — onboarding investigation findings (before any change)

| Step | Present / reachable | Where |
|---|---|---|
| First-run detection | Present: onboarding when no saved server has a token. `FirstLaunchComplete` was written and never read. | `MainViewModel.InitializeAsync` |
| Emby Connect PIN | Present, the primary action | `OnboardingViewModel.StartEmbyConnectPinFlowAsync`; page "Sign In with Emby Connect" |
| Manual server entry | Present as the "or connect directly" path | `ConnectToServerAsync` |
| Connection test, real error | Broken: generic "Could not connect…"; reason only in the log. Saved-server switch treated offline as "Session expired". | `EmbyClient.GetServerInfoAsync`, `SwitchToSavedServerAsync` |
| Library discovery and mapping | **Missing** (v3.5: `LibraryMappingWizard` after the first sync). v4 auto-groups Movies/Shows for combined mode without asking; per-server `LibraryMappings` is never read. | v3.5 `src/renderer/components/ui/LibraryMappingWizard.tsx`, `src/main/library-mapper.ts` |
| Combined vs separate | **Missing** from onboarding; defaults to separate, Settings only | `Settings.LibraryMode` |
| First sync with visible progress | Present (header pill + bar) | `MainWindow.xaml` |
| What the user sees on failure | **Missing**: Error hid the pill silently; Partial left "Syncing…" up indefinitely; an unreachable server was dropped and the run reported success | `MainViewModel` progress handler, `SyncEngine` discovery |

---

## 5. Worse than the audit recorded

1. **Remove-from-Continue-Watching marked the item played on its own server**, not only on siblings: the DELETE
   had no `PositionTicks`, which Emby-lineage servers treat as played to completion (inferred from server source).
2. **Every Esc wiped the server resume point** (`Stopped` with `PositionTicks = 0`), answering the audit's open
   question (inferred from server source).
3. **The live DB had grown worse** than the audit's figures: largest group 65 items (audit 43); 7,584 groups with
   conflicting same-provider ids.
4. **v4 wiped v3.5's settings:** both used `nocturne-settings.json`; v4's PascalCase save dropped v3.5's camelCase
   keys (servers, tokens).
5. **A dedup rebuild never finished** on the live-size DB: LIMIT/OFFSET paging re-sorted every preceding row's JSON
   per chunk (26 s per chunk at offset 600k; > 1 h 40 min, holding `_dbLock`). Fixed in C (keyset paging, 0.07 s).
6. **After any rebuild the cascade would have written to unrelated items:** the swap did not remap the legacy
   `dedup_group_id` column that sibling lookup still matches. Fixed in C.
7. **No sync reentrancy guard at all:** eight call sites start a sync; an overlapping sync replaced the dedup index
   under the first one's writer. Fixed in D.
8. **Clear Cache deleted v3.5's image cache** (`%APPDATA%\Nocturne\image-cache`; v4 never writes it). Fixed in D.
9. **The self-check certified mostly the wrong text:** 11 of its 23 strings were not what the code executed.
   Fixed in E.
10. **Tests wrote into the user's real profile:** 23 lines of this pass's own test runs in the live log, and a test
    created `nocturne-v4-settings.json` in the real `%APPDATA%\Nocturne` (via `SettingsService`'s default
    constructor) before E4 redirected test paths.
11. **Watch Party: End with guests connected left ffmpeg and cloudflared running** (teardown threw before killing
    them), and **guests were dropped every 15–25 s** by overlapping socket sends. Fixed in F.
12. **A fresh sync of Valkyrie's TV library (676,802 items) is bounded by Emby's offset paging.** Each request pays
    a fixed cost: ~10 s at 100k items in, and 13–20 s from ~300k items in, at any batch size. Six requests ran past
    the 30 s timeout during the fresh sync, all recovered by retry. The old controller ran this library at
    17–24 items/s; the committed one at 240 items/s in the same spot (§1, Stage G). Offset paging is Emby's to fix.

## 6. Open items

- **Trakt production client ID** (I1) — waiting on you.
- **Watch Party with real remote guests** through a public link — flagged for you.
- **Rebuild UI block** — a first rebuild still blocks UI reads ~3.8 min (swap transaction under `_dbLock`;
  measured on the 711k-row copy, likely longer at the fresh DB's 1.67 M rows, not measured).
  Deferred, kept on the list: the dedup plan's periodic reconcile needs rebuild to be usable. Taking the swap off
  `_dbLock` means running it on its own connection, which turns UI writes into `SQLITE_BUSY` after 5 s; now that E5
  surfaces DB failures, that is the next step. (The 26 s/chunk rebuild read was fixed in C.)
- `trakt_queue.group_id` is not remapped by a rebuild (audit 4.3).
- **Six episode groups wrongly merged on clean data** (§2). An episode with no provider ids matches by season and
  episode number into a group whose members carry another episode's id. A fix would refuse a positional (`sXXeYY`)
  match into a group where the same server already has a member under different numbering, or where every member
  carries an episode id. Not done in this pass.
- Deep-offset sync requests still take 13–20 s each (Emby `StartIndex` paging) and occasionally pass the 30 s
  timeout. G sizes around it; a sync request timeout above 30 s, or paging that avoids large offsets, would be
  the next step.
- The dedup index loads at every sync start: 57 s for 5.36 M keys on the fresh DB (off the UI thread).
- Same-server over-merge from bad server numbering (e.g. ten AEW Dynamite episodes all tagged S20E22 on Valkyrie)
  is invisible to the conflict guard; the cascade writes there are local-only and the server wins on the next sync.
- The live `nocturne.log` that held 19 Emby `api_key` values was deleted with the database; the rotated
  `nocturne.log.1` going forward is redacted.
- Sibling position writes still go through `Sessions/Playing/Stopped` on sibling servers, not the user-data
  endpoint (plan §7C); sibling drain runs only after playback.
- B5 covers the startup path only.
