# Post-Audit Work Order

Response to `docs/AUDIT-2026-10-v4-baseline.md`. Three things changed my view of
the sequencing, and I owe two corrections to the frozen specs.

---

## P−1. Stop writing wrong state to the Emby servers (before anything else)

Risks #1, #2 and #3 compose into **active, ongoing data destruction on live
servers**. Every Esc reports `PositionTicks = 0` to the owning server. Every
manual Next marks played across both servers. Over-merged groups fan each of
those across up to 43 unrelated items. That damage lands outside Nocturne's
cache, where no rebuild or resync can repair it — and it is happening every time
the app is used.

Nothing else should be built until this stops. These are small, local changes,
not the fix — they are the tourniquet.

| # | Change | Site |
|---|---|---|
| 1 | Move `_lastPlaybackSeconds = 0` to **after** `FinalizeCurrentPlaybackAsync` | `MainViewModel.cs:1408` / `:1413` |
| 2 | Same for the previous-episode path | `MainViewModel.cs:1228` |
| 3 | `PlayNextEpisodeAsync` must pass the **real** progress, not `isEof: true` | `MainViewModel.cs:1216` |
| 4 | `PlaybackEnded` must check mpv's `end-file` **reason**; only `eof` counts as completion | `MpvController.cs:583-590` → `MainViewModel.cs:274-302` |
| 5 | Remove-from-Continue-Watching must clear the resume position, not mark played | `HomeViewModel.cs:384-395` |
| 6 | **Feature-flag the sibling cascade off** until dedup grouping is trustworthy | `MainViewModel.cs:1306`, `EnqueueSiblingPlaybackWrite` |
| 7 | Stop deleting sibling-queue rows when the write failed | `MainViewModel.cs:1373-1379` |

(6) is the one to resist softening. With 87 movie groups holding conflicting
TMDB ids and a 43-episode group, a single completion currently rewrites watched
state for dozens of unrelated titles on both servers. Flag it off; turn it back
on at the end of dedup Phase 3, gated on the partition-hash check.

Before (1), settle the open question it depends on: does Emby **reset** a stored
resume point on `Stopped` with `PositionTicks = 0`? Play a known-resume item,
press Esc, read `UserData` before and after. If it does, the damage is already
widespread and a one-off repair pass is worth considering.

---

## P0. Fix the inputs before the algorithm

The audit's most consequential finding is buried in 4.1: **the dedup engine's
authoritative episode keys produce zero rows live.**

```
0 live  ep:tvdb / ep:tmdb / ep:imdb keys   ← SeriesProviderIds never populated
0 live  name:{type}:{title}:{year} keys    ← ProductionYear null on 128,555/128,557 movies
```

So B2 — the stable provider episode keys I pushed hardest for, the thing that
makes series merges non-cascading — is **dead code**. Every episode is keyed on
`ep:series:{EmbyServerSeriesId}:sXXeYY`, which is a per-server integer. Cross-
server episode dedup cannot work, and within-server collisions are what produce
the 43-episode group.

That reframes the over-merge problem. It has three root causes, and only one of
them is the merge algorithm:

| Cause | Effect | Fix |
|---|---|---|
| Null `IndexNumber` → `e00` (`:174`) | 1,561 episodes collapse onto shared keys | Never emit an episode key when `IndexNumber` is null — same principle as the S6 fuzzy guard, which the spec applies to year but not to episode number |
| `SeriesProviderIds` never populated | server-local series keys, cross-server dedup impossible | See below — this does **not** need a new Emby field |
| No conflicting-key check on merge | *The Banker* + *Bunker* via a shared bad `tvdb:movie` id | Spec correction, §C1 below |

**On the series ids:** don't wait on a `Fields=` change. Series rows are already
synced (9,738 of them) with their own `ProviderIds`. Resolve the episode's series
provider ids by looking up the already-cached Series row via the episode's
`SeriesId`. No new request, no server round-trip, and it works on data already
on disk. Confirm `SeriesId` survives into `json_data` first — the audit shows
`parent_id` holds the library view id rather than the season, so the parent chain
is not trustworthy.

**On `ProductionYear`:** 10-minute check before changing anything. One
`/Users/{id}/Items?Ids=…` request with and without
`Fields=ProductionYear,PremiereDate`. If the field list is the cause, that single
line restores movie fuzzy keys, `dedup_groups.year`, and the year shown in the
UI. If the server isn't returning it either way, that is a different problem and
worth knowing before building around it.

**Re-run dedup after P0, then re-measure.** Every number in Group 4 — the 43-item
group, the 87 conflicting-TMDB groups, 3,732 groups with multiple `tvdb:ep` keys —
was produced by an engine running on crippled input. Tuning the merge rules
against those numbers would be tuning against an artefact.

---

## Spec corrections I owe you

**C1 — The merge rule needs a *disagreement* check, not just a shared key.**

My R3 §M1 restated row 1 as "the inbound item's authoritative keys resolve to ≥2
distinct authoritative groups → MERGE". The code implements exactly that, and
the live data shows why it isn't enough: *The Banker* and *Bunker* share a bad
`tvdb:movie:131759` on the server, so they merge — while holding **different
TMDB ids**, which is positive proof they are different films.

Add to the merge path, before committing:

> Refuse the merge if the candidate groups hold **conflicting authoritative ids
> of the same provider type** (two different `tmdb:movie` ids, two different
> `imdb` ids). Agreement on one provider does not outweigh disagreement on
> another. Log it and flag the key as suspect; do not silently attach either.

This is the authoritative-side equivalent of the remake guard, and the plan has
no equivalent rule. It also makes the engine resilient to bad provider data on
the server, which the audit proves exists.

**C2 — `integration_state` is a spec bug, not a code bug.** Spec §4.5 declares
`name TEXT PRIMARY KEY` while §8/§10 store several markers per integration. The
code's `PRIMARY KEY (name, key)` is correct. Amend the spec.

**C3 — Fuzzy attach needs the cap the comment claims.** `ConcurrentDedupIndex.cs:467-473`
attaches to an authoritative group reached **only through a fuzzy key**, with no
cap, under a comment describing a remake guard. Either enforce the guard or
delete the comment — a comment that misdescribes the code is worse than none.

---

## Four risks no plan covered

These came out of the audit, not the specs, and all four are silent:

1. **`ConcurrentDedupIndex.InitializeFromDb` can overwrite unrelated groups.**
   It clears the index and sets `_nextGroupId = 1` *before* the `try`, then
   swallows the failure (`:120-158`). Sync then mints ids from 1 and
   `SaveDedupBatch` upserts `ON CONFLICT(group_id) DO UPDATE` over existing rows.
   A transient read error silently rewrites the group table. Fix: load into a
   local, swap on success, and make failure fatal rather than a warning.
2. **A settings-load failure wipes servers and tokens.** `SettingsService.cs:24-54`
   returns defaults on any load failure (logged to `Console`, invisible in a
   WinExe), and the next `Update` saves those defaults over the real file, which
   is written non-atomically. Fix: fail loudly, never save over a file that
   failed to load, and write via temp-file + rename.
3. **`CredentialProtection` silently downgrades DPAPI to fixed-key XOR** on any
   exception, and `Unprotect` returns garbage rather than failing. That is worse
   than plaintext, because it looks protected.
4. **v4 drops v3.5's tables on the same database path** (`:50-53`). Launching v4
   destroys the v3.5 cache. If v3.5 is still a fallback, v4 needs its own file.

Plus two that are operational rather than corrupting: `SwapInRebuild` holds
`_dbLock` for the entire rebuild (JSON-deserializing ~470k rows with the UI
blocked), and `ClearAllCache` deletes a hard-coded `%APPDATA%` path regardless of
the constructor's `dbPath`.

---

## What's genuinely good

Worth recording, because it says the review rounds paid off where they were
applied: the schema is complete and matches spec; FTS5 triggers exist, are
re-created in the swap, and the index is in sync (413,103 = 413,103); the swap-in
rebuild does the 3a/3b reassignment correctly; `version_count` shows **zero**
drift; the R3 #3 `MAX()` NULL-propagation bug is absent; the rebuild row cap and
the ToggleSwitch are fixed. Every schema-level review item that was implemented
was implemented correctly. The gap is entirely between the schema and the code
that writes through it.

---

## Revised order

| Stage | Content | Exit |
|---|---|---|
| **P−1** | The seven tourniquet changes above | No path writes `PositionTicks = 0`; no non-completion path marks played; cascade flagged off |
| **P0a** | Episode key null guard; series provider ids resolved from cached Series rows; `ProductionYear` field check | `ep:tvdb:*` keys exist in the live DB; `production_year` non-null on movies |
| **P0b** | Full dedup rebuild, then **re-measure** Group 4's numbers | New baseline recorded; the 43-group and 87-conflict counts re-derived on good input |
| **P0c** | C1 conflict check, C3 fuzzy cap | Zero groups with conflicting same-provider ids |
| **P1** | The four silent risks above | Each has a test |
| **P2** | `year` SQL bug + make `SelfCheckPreparedStatements` validate the **actual** strings, not copies | A deliberately broken query fails at boot |
| **P3** | Log redaction (api_key URLs), tests stop writing to the production log | `nocturne.log` contains zero `api_key` |
| **P4** | Trakt wiring: bundled ID, `SetCustomClientId` call site, UTC+ `SaveState` crash, retire the legacy stack | Connect works on a clean profile; one Trakt stack, not two |
| **P5+** | Sync refresh/delete paths, virtualization, memory | Per the existing specs |

P2's self-check finding deserves a note: it validates a **different string** from
the one that runs (`:2535` uses `production_year`, `:1166` uses `year`). A
self-check that certifies text the app never executes is worse than no
self-check, because it buys false confidence. It must enumerate the actual
command strings.

---

## Housekeeping

- **Commit the frozen dedup plan v4.4 to `docs/`.** The audit had to reconstruct
  it from the four review rounds. Every future pass pays that cost again.
- **Commit the audit** as-is; it's versioned alongside the code it describes.
- A GitHub issue is worth opening for the **Top 10 risks only**, linking the
  report. The full document is reference, not a work queue.
- No need to publish it as a page — it's a solo repo and the report contains no
  secrets, so the repo is the right home.
- The tests writing into the production `nocturne.log` is itself a bug (static
  logger, fixed path). Fold it into P3.
