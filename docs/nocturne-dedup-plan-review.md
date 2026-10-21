# Review — Multi-Quality Dedup & Server Convergence Plan

Plan is structurally sound: group/version split is right, the authoritative-key
invariant is right, rebuild-as-oracle is right. Below are the things that will
break, ordered by severity. Numbers refer to the plan's sections.

---

## BLOCKERS — internal contradictions and correctness holes

### B1. §8 deletes the data that §4, §5 and §7 depend on
"Reset Database & Full Sync ... re-syncs with `IncludeItemTypes=Movie,Series`,
eliminating the 676,000 recursive episode bloat."

If episodes aren't synced there are no episode rows, so:
- §4 episode dedup has nothing to dedup
- §7 Continue Watching has no episode to show (the screenshot card is
  `Slow Horses S06E05`)
- next-episode auto-advance and the episode version picker have no source
- sibling progress broadcast has no sibling episode IDs to write to

The §1 root cause was that the dedup index **skipped** episodes
(`if (item.Type != "Movie" && item.Type != "Series") continue;`), not that
episodes shouldn't exist. §4 already fixes that. Dropping episodes fixes the
count by deleting the evidence.

**Decide explicitly, and write it into the plan:**
- **(a) Keep episodes synced.** 676K rows is fine with the new schema. Simplest,
  everything else in the plan works as written.
- **(b) Sync Series as the shelf entity, fetch episodes lazily on series open.**
  Legitimate and much lighter — but then episode dedup becomes a *runtime*
  operation, not a sync-time one, and Continue Watching needs episode rows
  materialized separately for whatever is in-progress. That is a different
  design and §4/§5/§7 all need rewriting for it.

Currently the plan reads as (a) everywhere except §8, which assumes (b).

### B2. The episode composite key is built on a mutable value
`ep:{series_group_id}:s01e01` keys episodes by the **internal group id** of the
series. But group ids change: merging series groups G1 and G2 makes G2's id
disappear. Every `ep:G2:*` key is then stale, and where `ep:G1:s01e01` already
exists the two *episode* groups must also merge.

One series merge can cascade into hundreds of episode merges, inside the single
write transaction. The plan has no cascade path, so after any series merge the
episode layer silently diverges.

**Fix — key episodes on an authoritative series id, not the group id:**
```
ep:tvdb:81189:s01e01        (authoritative, stable forever)
ep:tmdb:1396:s01e01         (authoritative)
ep:grp:{series_group_id}:s01e01   (is_authoritative = 0, fallback only)
```
Authoritative episode keys then survive series merges untouched, and only the
fallback-keyed episodes (series with no provider id) need re-keying. Cap that
work or queue it rather than doing it inline.

### B3. §3's own motivating example is refused by §3's guard
The example: A (fuzzy only) → G1. B (`tmdb:550`) → G2. C has `tmdb:550` **and**
A's name+year.

Walk C through the flowchart: two groups matched, `CheckAuth` asks "are 2+
groups matched via authoritative keys?" — no, G1 was matched fuzzily — so the
branch is **3A Guarded Attach**. C joins G2. **G1 and item A stay split
forever.** The scenario the section exists to solve is not solved.

The guard is too coarse. The actual risk being defended against is fusing two
*independently identified* titles (remakes) on fuzzy evidence. Absorbing a group
that has **no authoritative identity at all** is a different, much lower risk —
and you already accepted it when you let the fuzzy key attach in the first place.

**Replace the merge rule with:**

| Side A keys | Side B keys | Link | Action |
|---|---|---|---|
| authoritative | authoritative | shared authoritative key | **MERGE** |
| authoritative | authoritative | fuzzy only | **REFUSE** (remake guard) |
| authoritative | fuzzy only (provisional) | any | **MERGE** (absorb provisional) |
| fuzzy only | fuzzy only | fuzzy | **ATTACH only**, never merge |

Add `is_provisional` to `dedup_groups` (true = no authoritative key yet; cleared
the moment one arrives). Row 3 is what fixes the §3 example.

### B4. There is no delete path anywhere in the plan
Item removed from a server, library unmapped, server removed in Settings. Needed:
decrement `version_count`; if the removed item was the primary, re-elect and
re-copy display fields; delete the group at zero; delete its `dedup_keys` rows.

Without this you get groups whose `primary_item_id` points at nothing — which
renders as the empty poster card already on the issue list (#17). "Remove server"
is the common trigger and it currently orphans every group it was primary for.

### B5. §7 sibling progress broadcast reintroduces the audited bug class
Three separate problems:

1. **Wrong API for siblings.** `ReportPlaybackProgressAsync` announces a
   *playback session* to a server that is not streaming anything. That likely
   opens a phantom session on the sibling (visible in its dashboard, held open,
   with its own session bookkeeping). For a server you are not playing from, set
   user-data directly (`.../PlayedItems/{id}` and the user-data position
   endpoint) instead of reporting a session. Verify the session side-effect on
   Raptor before shipping this.
2. **Every 10 s × N servers is unnecessary write amplification.** The streaming
   server needs 10 s ticks to keep its session alive. Siblings only need the
   position at pause, stop and EOF.
3. **No failure policy.** If a sibling is unreachable mid-broadcast the plan is
   silent — which is exactly how "500s swallowed" happened in v3.5. Queue failed
   sibling writes for retry, never block or fail playback on them, and never
   advance any "synced" marker for a server that errored.

Also missing: **conflict resolution.** If the title was watched directly on
Raptor's own web UI, Raptor's position is newer than the local one. On the next
sync, which wins? Define last-write-wins by timestamp, and make sure sync cannot
clobber a newer local position with an older server one. That is precisely the
bug that broke Continue Watching before.

---

## SCHEMA

### S1. `json_data` on `dedup_groups` is the biggest footgun in the schema
It is a *copy* of the primary's `json_data`, so the full EmbyItem payload is
stored twice — once per item, once per group — and has to be rewritten on every
primary re-election. At ~1.5M items × 2–4 KB that is gigabytes, and it
guarantees the row-overflow page chaining that §2 says it is avoiding.

**Drop it.** The detail page already does one indexed lookup into
`cached_items`; read the primary's JSON there. The group row should stay narrow
so the shelf index actually pays off.

### S2. "Copied from the primary" is wrong for three of the listed columns
`max_height`, `has_4k`, `max_bitrate` are **aggregates over all versions**, not
properties of the primary. A group whose primary is the 1080p copy must still
report `has_4k = 1`. Split the group's columns explicitly into *copied from
primary* (name, overview, image_tag, …) and *aggregated over versions*
(max_height, has_4k, max_bitrate, audio_formats, version_count), and recompute
the aggregates on every attach, merge and delete.

### S3. Primary election is undefined, and "highest score" is the wrong basis
§3 says "re-elect primary if item has higher score"; no score is defined.

The best *stream* and the best *metadata* are often different items. Electing by
stream quality means a bare-metadata 4K remux can replace a copy with a proper
overview and artwork, and the shelf card visibly degrades. Elect the primary on
**metadata completeness, then server priority, then stable id tiebreak** — and
keep stream choice entirely in §5, computed per play. Write the function down;
it must be deterministic or rebuild and incremental will disagree forever.

### S4. The shelf index is not covering
`ix_dedup_groups_shelf (item_type, date_created DESC)` does not contain the
display columns, so SQLite seeks the index then fetches each row from the table.
For 20-row shelves that is irrelevant — but then don't claim "sub-millisecond
index-only scan" in §2. Either fix the claim or add the display columns to the
index and accept the write cost.

### S5. Missing indexes and cheap storage wins
- `dedup_keys` is a TEXT primary key table → make it **`WITHOUT ROWID`**. Roughly
  halves its storage and removes a level of indirection on every lookup. At
  ~1.5M+ keys this is the single best free win in the schema.
- `group_id INTEGER PRIMARY KEY AUTOINCREMENT` → drop `AUTOINCREMENT`. It adds a
  `sqlite_sequence` write on every insert and forbids rowid reuse, and merges
  delete rows constantly. Plain `INTEGER PRIMARY KEY` is correct here.
- Add `cached_items(server_id, last_synced)` — needed by the incremental sync
  watermark and by "remove server".
- Add `dedup_groups(primary_item_id, primary_server)` — needed by re-election
  and the delete path (B4), which otherwise full-scans.
- **Search has no index at all.** The UI has a prominent search bar over 1.5M
  items; `name` is unindexed. Add FTS5 over `(name, sort_name)` on
  `dedup_groups`, or at minimum an index on `sort_name`. Not mentioned anywhere
  in the plan.

### S6. Guard fuzzy key generation
`name:movie:{cleantitle}:{year}` with a null year, or an empty/generic title, is
a false-positive magnet and can grow one pathological group to thousands of
members — each later merge then rewrites thousands of rows inside the serialized
write transaction.

Rule: **never emit a fuzzy key when year is null or the clean title is empty or
generic.** Cap group size and log outliers; a group over ~50 members is a bug,
not a title.

---

## PLAYBACK (§5)

### P1. No capability filter before the quality sort
Resolution tier is applied first, so a 4K AV1 beats a 4K HEVC even on hardware
with no AV1 decode — giving software decode and dropped frames on the highest-
quality pick. Filter sources by **decodability on this machine** *before*
tiering, not after.

### P2. The 15% bitrate rule compares across codecs
20 Mbps HEVC and 40 Mbps H.264 are comparable quality; the rule would pick the
H.264. Either normalize by codec efficiency or only compare bitrate **within the
same codec** and fall through to server priority otherwise. Also define
null-bitrate behavior — Emby frequently returns null or a container average, and
as written a null silently picks arbitrarily.

### P3. Health check is on the latency-critical path
A probe before every play adds a round-trip to the most latency-sensitive action
in the app. Keep server health in a background poller and have the play path read
cached state, probing only if the state is stale (>30 s).

### P4. Only pre-launch failover is covered
The common real failure is the server dropping *mid-playback* at 40 minutes, not
being down at launch. Add: detect mpv stall/EOF-with-error, re-resolve the source
through the matrix excluding the failed server, resume at the current tick.

### P5. §6 "zero server info" will hurt during failures
Hiding server names in the pill label is right. Hiding them *everywhere* means a
silently downgraded pick (4K server offline → 1080p) and an unactionable error
message. Keep server identity in the tooltip/details and in error text:
"Raptor unreachable — playing 1080p from Valkyrie" is what makes automatic
selection feel trustworthy rather than arbitrary.

Also: pill disambiguation "by audio or bitrate" still leaves true ties. Give the
list a deterministic final tiebreak (server priority) or pills will reorder
between visits.

---

## PROCESS & VALIDATION

### V1. §10's criteria are qualitative; the oracle test is better than that
"Merges into 1 group" and "accurate count" can both pass while assignments are
wrong. Counts are a weak check — two different partitions can have identical
group counts.

**Real test:** run incremental over a full sync, then run the batch rebuild, and
compare the **partitions**, not the counts: for each group, hash the sorted list
of `(server_id, item_id)` members; compare the sorted multiset of hashes. Equal
multisets = incremental is correct. Any difference prints the specific groups
that diverged.

Baseline to hold against: **1,480,383 items / 32,366 groups / 73,895 merged.**

**Define the drift counter** as the number of items whose group assignment
differs between incremental and the most recent rebuild. That is computable,
displayable, and actually means something in Settings.

### V2. Rebuild should swap in, not mutate in place
A rebuild that rewrites `dedup_groups` live will delete group ids the UI is
currently holding (detail page open → blank page), and a failure mid-rebuild
leaves the table in a partial state. Build into `dedup_groups_new` /
`dedup_keys_new`, then rename inside one transaction. Failure leaves the old
data intact.

### V3. Migration is missing from Phase 1
Phase 1 says "implement the schema" but not what happens to the ~950K rows
already cached, and §8 implies the answer is "wipe and re-sync" — hours of
syncing as a migration strategy. Migrate in place: add the new tables, backfill
`group_id` with a batch rebuild, keep `cached_items`. Reserve the wipe for a
user-initiated reset.

### V4. Merge must run inside the sync pipeline's single writer
The new sync design has one serialized writer. In-transaction merge must happen
*in that writer*, not from a parallel enricher, or you will get `SQLITE_BUSY`
under load. Worth stating explicitly in Phase 3 so it isn't discovered at
runtime.

### V5. §1.A.4 names the real bug and then fixes the symptom
"`AND year = $year` ... throwing a silent SQL syntax exception" — a wrong column
name fails at *prepare* time, so that query never worked, ever, and nobody saw
it. The column name is the symptom; **the swallowed exception is the bug**, and
it is the same class the v3.5 audit was written about.

Two fixes, both cheap:
- Stop catching broadly around DB calls; let schema errors be fatal in debug.
- Add a startup self-check that *prepares* (does not run) every SQL statement in
  the app and fails fast. Catches every future typo of this kind in one shot.

---

## REVISED PHASING

Unchanged where the plan is already right; the additions are what's missing.

| Phase | Content | Gate |
|---|---|---|
| **0** | Decide B1 (episodes in or out). Fix B2 key design. SQL prepare self-check (V5). Stop swallowing DB exceptions. | Decision written down; self-check fails on a deliberately broken query |
| **1** | Schema (with S1/S2/S5 applied), **in-place migration** (V3), batch rebuild as swap-in (V2). Toggle fix. | Rebuild reproduces the 32,366 / 73,895 baseline |
| **2** | Incremental attach + provisional flag. No merging yet. | Partition hash matches rebuild on an attach-only corpus |
| **3** | Merge with the B3 four-row rule, inside the single writer (V4). Episode cascade bounded (B2). | Partition hash matches rebuild after a full incremental sync |
| **4** | **Delete / unsync path (B4).** Then server priority, pills, decision matrix with P1–P4. | Remove a server; zero orphan groups, zero empty cards |
| **5** | Continue Watching unification + sibling user-data writes (B5) with retry queue and conflict rule. | Stop at 50% on Valkyrie → correct position on Raptor and locally; no phantom session on Raptor |

Two changes to the order: B4 (delete) moves ahead of the presentation work
because the presentation layer renders its bugs, and the partition-hash oracle
gates **every** phase rather than appearing only in Phase 3.
