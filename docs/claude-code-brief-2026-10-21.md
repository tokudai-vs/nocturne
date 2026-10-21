# Nocturne — Execution Brief, 2026-10-21

One pass, nine stages. Work in order. Do not reorder to do the interesting parts
first — Stage A is stopping live data loss and everything else can wait an hour.

## Documents

| Path | What it is |
|---|---|
| `docs/AUDIT-2026-10-v4-baseline.md` | The baseline audit. All findings cited below are from it. |
| `docs/post-audit-work-order.md` | Why the stages are in this order, with the P−1 change table. |
| `docs/multi-quality-dedup-plan-v4.4.md` | The frozen dedup plan the code is being built toward. |
| `docs/nocturne-trakt-tmdb-spec.md` | Trakt/TMDB spec v1.0. |
| `docs/AUDIT-2026-07-cross-server-correctness.md` | The v3.5 correctness spec. Still the authority on owning-server resolution. |

## Rules

1. **One commit per stage**, message naming the stage. Do not squash the whole
   pass into one commit — this needs to be bisectable.
2. **Run `dotnet test` after every stage.** 90 tests exist; 89 pass today. The
   failing one (`TraktAuthTests.ClientIdUsedForToken_InvalidationGuard`) is a
   real bug fixed in Stage E. Never finish a stage with a new failure.
3. **Add a test for every behavioural fix** where a test is possible. Stage A in
   particular: the stop paths currently have zero coverage, which is why this
   shipped.
4. **Stages F and H begin with investigation, not edits.** Report what you find
   before changing anything in them.
5. If context runs low, **stop at a stage boundary**, commit, and say where you
   stopped. Do not rush the remaining stages.
6. `NocturneLogger` writes to the user's real `%APPDATA%\Nocturne\nocturne.log`,
   including from tests. Until Stage D fixes that, be aware you are appending to
   production data.

---

# Stage A — Stop writing wrong state to the servers

Every Esc currently reports `PositionTicks = 0` to the owning Emby server and
writes nothing locally; every manual Next marks played across both servers; and
over-merged dedup groups fan each of those across up to 43 unrelated items. This
damage lands on the servers, outside Nocturne's cache, where no rebuild repairs
it. It happens on every use.

| # | Change | Site |
|---|---|---|
| A1 | Move `_lastPlaybackSeconds = 0` to **after** `FinalizeCurrentPlaybackAsync` | `MainViewModel.cs:1408` / `:1413` |
| A2 | Same for the previous-episode path | `MainViewModel.cs:1228` |
| A3 | `PlayNextEpisodeAsync` passes the **real** progress, not `isEof: true` | `MainViewModel.cs:1216` |
| A4 | `PlaybackEnded` checks mpv's `end-file` **reason**; only `eof` counts as completion | `MpvController.cs:583-590` → `MainViewModel.cs:274-302` |
| A5 | Remove-from-Continue-Watching clears the resume position, does **not** mark played | `HomeViewModel.cs:384-395` |
| A6 | **Feature-flag the sibling cascade off** (default off) until dedup is trustworthy | `MainViewModel.cs:1306`, `EnqueueSiblingPlaybackWrite` |
| A7 | Stop deleting `sibling_write_queue` rows when the write failed; honour the bool | `MainViewModel.cs:1373-1379` |
| A8 | Wire `ReportPlaybackProgressAsync` (`EmbyClient.cs:769`, currently zero callers) to the existing position tick so the server sees intermediate progress | `MainViewModel.cs:196-209` |

A6 is not negotiable for this pass. With 87 movie groups holding conflicting
TMDB ids and one group holding 43 distinct episodes, a single completion
currently rewrites watched state for dozens of unrelated titles on both servers.
The flag comes back on at the end of Stage C, gated on the dedup numbers.

**Tests:** cover each stop path (Stop button, Esc, window close, previous, next,
EOF) asserting the position written locally and the ticks reported to the server.

---

# Stage B — Instant startup

Today the window appears only after everything is loaded. Target: something on
screen immediately, then progressive load.

**B1. Measure first.** Log elapsed ms for each startup step: settings load,
`DatabaseService.Initialize` (schema + ALTERs), `ConcurrentDedupIndex.InitializeFromDb`,
first view model construction, first render. Put the numbers in the commit
message. The likely culprit is already visible in the audit: the dedup index
loads every group, key and item tuple into memory at startup (342,523 groups /
955,911 keys logged at 14:56).

**B2. WPF built-in splash for the pre-WPF gap.** Add a PNG with
`Build Action = SplashScreen`. This is drawn by native code before the CLR and
WPF finish initialising, so it is the only thing that can appear "instantly".
Static image, no progress — that's fine, it covers the first ~1s.

**B3. Show `MainWindow` immediately, in a loading state.** Construct and show the
window before any data work. Shelves render skeleton placeholders; the sidebar
and chrome are live. Nothing on the UI thread may block on the database.

**B4. Take the dedup index off the startup path entirely.** It is needed by
sync, not by the first paint. Load it lazily on first sync, or in the background
with sync gated on completion. This is the actual fix; B2 and B3 are presentation.

**B5. Keep the splash honest.** If load fails, the window must show an error
state, not a splash that never goes away. No indefinite spinner.

---

# Stage C — Fix the dedup inputs, then re-measure

The over-merge numbers in the audit were produced by an engine running on
crippled input. Do not tune merge rules against them.

**C1. Episode key null guard.** `ConcurrentDedupIndex.cs:174` turns a null
`IndexNumber` into `e00`, collapsing 1,561 episodes onto shared keys. Never emit
an episode key when `IndexNumber` is null — the same principle the plan applies
to null years for fuzzy keys.

**C2. Resolve series provider ids locally.** Zero `ep:tvdb/tmdb/imdb` keys exist
live because `SeriesProviderIds` is never populated, so every episode falls back
to `ep:series:{EmbyServerSeriesId}`, which is a per-server integer and cannot
dedup across servers. **Do not add an Emby round-trip for this.** 9,738 Series
rows are already cached with their own `ProviderIds`; look the series up by the
episode's `SeriesId` and build `ep:tvdb:{id}:sXXeYY` from it. First confirm
`SeriesId` survives into `json_data` — `parent_id` holds the library view id, so
the parent chain is not trustworthy.

**C3. `ProductionYear`.** Before changing anything, make one
`/Users/{id}/Items?Ids=…` request with and without
`Fields=ProductionYear,PremiereDate` and report which returns the value. If the
field list is the cause, add them to `FullSyncItemFields` (`EmbyClient.cs:75`) —
that restores movie fuzzy keys, `dedup_groups.year`, and the year in the UI. If
the server returns null either way, report it and stop; that is a different
problem.

**C4. Conflicting-id guard on merge.** *The Banker* and *Bunker* merged via a bad
shared `tvdb:movie:131759` while holding **different TMDB ids** — positive proof
they are different films. The spec's rule is incomplete. Add:

> Refuse a merge when the candidate groups hold **conflicting authoritative ids
> of the same provider type** (two different `tmdb:movie` ids, two different
> `imdb` ids). Agreement on one provider does not outweigh disagreement on
> another. Log it, flag the shared key as suspect, attach nothing.

**C5. Enforce the fuzzy cap the comment claims.** `ConcurrentDedupIndex.cs:467-473`
attaches to an authoritative group reached only through a fuzzy key, with no cap,
under a comment describing a remake guard. Enforce it or delete the comment.

**C6. Full rebuild, then re-measure.** Re-derive every Group 4 number: total
groups, multi-version groups, groups with ≥2 distinct same-provider ids, groups
holding >1 `tvdb:ep` key, largest group size. Put the before/after table in the
commit message. Target: **zero** groups with conflicting same-provider ids.

**C7.** If C6 is clean, turn A6's cascade flag back on.

---

# Stage D — The four silent failures

All four come from the audit, none were in any plan, and all four are invisible
when they happen.

**D1. `ConcurrentDedupIndex.InitializeFromDb` can overwrite unrelated groups.**
It clears the index and sets `_nextGroupId = 1` *before* the `try` (`:120-158`),
swallows failure as a warning, and sync then mints ids from 1 while
`SaveDedupBatch` upserts `ON CONFLICT(group_id) DO UPDATE` over existing rows.
Load into a local structure, swap on success, make failure fatal.

**D2. A settings-load failure wipes servers and tokens.** `SettingsService.cs:24-54`
returns defaults on any load failure — logged to `Console`, invisible in a WinExe
— and the next `Update` saves those defaults over the real file, non-atomically.
Fail loudly, never save over a file that failed to load, write via temp + rename.

**D3. `CredentialProtection` silently downgrades DPAPI to fixed-key XOR**
(`:14-29`, `:36-50`) and `Unprotect` returns garbage rather than failing. Worse
than plaintext, because it looks protected. Fail instead.

**D4. v4 drops v3.5's tables on the same database path** (`DatabaseService.cs:50-53`),
so launching v4 destroys the v3.5 cache. If v3.5 is still a fallback, give v4 its
own file. Decide and state which.

Also in this stage: `SwapInRebuild` holds `_dbLock` for the entire rebuild while
deserializing ~470k rows, blocking all UI reads; and `ClearAllCache` deletes a
hard-coded `%APPDATA%` path regardless of the constructor's `dbPath`.

---

# Stage E — Correctness hygiene

**E1.** `DatabaseService.cs:1166` still selects a `year` column that does not
exist; it failed 4 times in production yesterday.

**E2.** `SelfCheckPreparedStatements` validates a **different string** from the
one that runs (`:2535` uses `production_year`, `:1166` uses `year`). A self-check
that certifies text the app never executes is worse than none. Make it enumerate
the actual command strings the code issues.

**E3.** Log redaction: `MpvController.cs:281` and `:319` write full stream URLs
including the Emby `api_key` — 19 occurrences in the live log. Redact `api_key`,
tokens, `device_code`, and PKCE verifiers. Add log rotation.

**E4.** Tests write into the user's real `nocturne.log` because `NocturneLogger`
is static with a fixed path. Make the path injectable; point tests elsewhere.

**E5.** The 19 silent DB catches listed in audit 2.4 — at minimum make them log
with the SQL and rethrow in Debug.

---

# Stage F — Watch Party (investigate, report, then fix)

This worked in v3.5. v4's status is unknown; `WatchPartyService.cs` exists and
three init tests pass, which proves nothing about the end-to-end path.

**Investigate and report before editing:**

1. Is there a UI entry point at all, and is it reachable?
2. Host and guest roles — what does each do, and where is the position sync
   protocol?
3. v3.5 played the shared stream via **hls.js in a `<video>` element**, which has
   no equivalent in v4. What does v4 do instead? If it is meant to go through
   mpv, does the HLS URL reach `MpvController`?
4. Who creates and tears down the Emby transcode/HLS session?
5. Is the cloudflared tunnel still involved, and is it wired?
6. **The guest cap: find the line that enforces it.** v3.5's guest cap appeared
   in the CHANGELOG and the modal and was never enforced anywhere. Do not report
   it as present because a limit constant or a modal exists — follow it to the
   code that rejects the (N+1)th guest.

Then propose the smallest change that makes it work end to end, and say whether
that is a fix or a re-implementation on top of mpv. Do not start a
re-implementation without saying so first.

---

# Stage G — Adaptive batch sizing against a per-server baseline

`AdaptiveBatchController.cs:9-11` compares request latency to a fixed ~3.5 s
threshold. Valkyrie's baseline is ~6 s, so it is permanently in slow-down and
never reaches a useful batch size.

Two defects, and the second is the more important one:

**G1. The threshold must be relative to a per-server baseline**, not absolute.
Each server gets its own baseline; a 6 s server should be allowed to sit at 6 s
without being treated as degraded.

**G2. The metric is confounded by batch size.** A 25,000-item request naturally
takes longer than a 250-item one, so sizing the next batch from total request
duration creates a feedback loop in which growth looks like degradation.
Normalise before comparing — **ms per item**, or measure time-to-first-byte
(server think time) separately from transfer time.

Suggested shape:

```
per server:
  normalised = requestMs / itemCount          // or TTFB, measured separately
  baseline   = EWMA(normalised, alpha ≈ 0.05) // slow-moving
  current    = EWMA(normalised, alpha ≈ 0.3)  // fast-moving

  warm-up: first K requests seed the baseline; do not adapt during warm-up
  grow   when current <  baseline * (1 + growBand)
  shrink when current >  baseline * (1 + shrinkBand)
```

Ratios rather than absolute seconds make the 6 s case work without tuning.
Clamp baseline growth so a slowdown cannot inflate the baseline and ratchet the
batch size down permanently. Log the per-server baseline and current value once
per batch so this is observable.

Existing tests: `AdaptiveBatchController` has 8. Extend them with a 6 s-baseline
server that must still reach a large batch size.

---

# Stage H — Onboarding (investigate, report, then fix)

`RunFullSyncAsync` has two onboarding call sites, so something exists. Compare
against what v3.5 did and report the gaps before editing.

Check each step is present **and reachable**: first-run detection; Emby Connect
PIN linking; manual server entry as the advanced path; connection test with a
real error message on failure; library discovery and mapping; combined vs
separate mode choice; the first sync with visible progress; and what the user
sees if any step fails. Note where each lives.

Then fix the gaps. If an entire step is missing rather than broken, say so
before building it.

---

# Stage I — Trakt wiring

Two Trakt stacks exist. The new one (`TraktAuthService` / `TraktScrobbleService` /
`TraktQueueDrainWorker` / `TraktSyncService`) is spec-shaped but unreachable; the
legacy `TraktService` is still wired into playback, sync, library and cards.

**I1.** `BundledClientId` is `""` (`TraktAuthService.cs:18`) — ask the user for the
production client ID before this stage; do not invent one.
**I2.** `SetCustomClientId` has no callers; the Settings box writes the legacy
`TraktClientIdOverride` (`SettingsViewModel.cs:1489`). Wire the field to the auth
service.
**I3.** `SaveState` throws on UTC+ machines (`TraktAuthService.cs:438`,
`((DateTimeOffset)DateTime.MinValue)`). This is the failing test.
**I4.** Refresh wipes tokens on any exception including a transient network error
(`:314-323`). Only clear on a definitive auth rejection.
**I5.** Call refresh at app start, not only before a queue drain.
**I6.** Retire the legacy stack, or gate it so it can never double-scrobble. Note
that `TraktService.IsConnected` currently gates the watchlist pull and the
sidebar shelf, so both stay dark even when the new auth is connected.

---

# Report

At the end, append to `docs/AUDIT-2026-10-v4-baseline.md` (or a sibling
`docs/REMEDIATION-2026-10.md`) a table of every item above with its outcome:
Fixed / Partially fixed / Not started / Found not to apply, with the commit that
carries it. For Stages F and H include the investigation findings even where no
fix was made.

Call out anything you found along the way that is worse than what the audit
recorded. That has happened once already this week.
