# Review round 2 — v4.1 Master Plan

All five blockers are genuinely resolved, not papered over. B2 (stable provider
episode keys) and B3 (`is_provisional` + absorb rule) are implemented correctly
and the §3 example now resolves. Delete lifecycle, sibling user-data split,
partition-hash oracle, in-place migration: all correct.

What follows is what the revision introduced or still leaves open. Two are
corruption-class; the rest are precision and hot-path issues.

---

## C1. The swap-in rebuild silently repoints every item at the wrong group

§8B renames `dedup_groups` → `_old` and `dedup_groups_rebuild` → `dedup_groups`,
but `cached_items.group_id` is never touched. The rebuild assigns **fresh**
group ids, and since `AUTOINCREMENT` was dropped (correctly, S5) ids get reused.
So after the swap, `cached_items.group_id = 4817` points at whatever title the
*new* table happens to have at 4817.

Nothing errors. Shelves render fine — they read `dedup_groups` alone. The
breakage only shows on a detail page, as the wrong version list. That is the
worst possible failure shape and it is exactly the class this plan exists to
kill.

**Fix:** the rebuild must produce the item→group mapping, and the reassignment
must happen inside the same transaction as the rename:

```sql
-- built alongside dedup_groups_rebuild
CREATE TABLE item_group_rebuild (
    id TEXT NOT NULL, server_id TEXT NOT NULL, group_id INTEGER NOT NULL,
    PRIMARY KEY (id, server_id)
) WITHOUT ROWID;

BEGIN;
  ...renames...
  UPDATE cached_items
     SET group_id = (SELECT r.group_id FROM item_group_rebuild r
                      WHERE r.id = cached_items.id
                        AND r.server_id = cached_items.server_id);
  DROP TABLE item_group_rebuild;
COMMIT;
```

1.5M-row UPDATE in one transaction is heavy but correct, and a rebuild is rare.
Verify the partition hash **after** the swap, not before — the hash over
`cached_items.group_id` is what the app actually reads.

## C2. `ALTER TABLE RENAME` rewrites the foreign key, then the DROP breaks

SQLite ≥3.25 rewrites references to a renamed table inside *other* tables' FK
clauses. `cached_items.group_id REFERENCES dedup_groups(group_id)` therefore
becomes `REFERENCES dedup_groups_old(group_id)` the moment §8B's first rename
runs — and then `DROP TABLE dedup_groups_old` either fails or leaves
`cached_items` pointing at a table that no longer exists.

Three ways out, pick one and write it into §8B:
- wrap the swap in `PRAGMA legacy_alter_table = ON` (and restore after), or
- drop the FK constraint on `cached_items.group_id` and enforce the invariant in
  code (reasonable here — the rebuild rewrites it wholesale anyway), or
- don't rename at all: `DELETE FROM dedup_groups; INSERT INTO dedup_groups
  SELECT * FROM dedup_groups_rebuild;` inside one transaction. Slower, no
  schema-object churn, no FK rewriting.

Also check `PRAGMA foreign_keys` state — if it is ON, the rename/drop sequence
interacts with it; if it is OFF, the FK declarations in the plan are decorative
and worth knowing that.

---

## G1. User-data state has no table anywhere in the schema

§7C specifies last-write-wins on `last_played_timestamp`, and B5.3 specifies an
offline retry queue. Neither has a home: there is no `position_ticks`, no
`played`, no `last_played_*`, and no queue table in §2.

This is the state the v3.5 audit was written about — `playback_position_ticks`
never written back is the bug that broke Continue Watching entirely. It cannot
live in `json_data` (stale, per-server, rewritten on every sync).

Continue Watching also needs to *query* it: ordered by last-played, filtered to
in-progress, joined to the group. That needs columns and an index.

```sql
CREATE TABLE user_data (
    group_id          INTEGER PRIMARY KEY REFERENCES dedup_groups(group_id),
    position_ticks    INTEGER NOT NULL DEFAULT 0,
    runtime_ticks     INTEGER,
    played            INTEGER NOT NULL DEFAULT 0,
    last_played_utc   INTEGER,                 -- drives last-write-wins
    updated_locally   INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_user_data_continue
    ON user_data(last_played_utc DESC)
    WHERE position_ticks > 0 AND played = 0;   -- partial index = tiny, exactly Continue Watching

CREATE TABLE sibling_write_queue (
    queue_id    INTEGER PRIMARY KEY,
    server_id   TEXT NOT NULL,
    item_id     TEXT NOT NULL,
    position_ticks INTEGER,
    mark_played INTEGER NOT NULL DEFAULT 0,
    queued_utc  INTEGER NOT NULL,
    attempts    INTEGER NOT NULL DEFAULT 0
);
```

Keying `user_data` on `group_id` is what makes Continue Watching one card for
free. The queue table is what makes B5.3 survive an app restart — an in-memory
queue loses the write when the app closes mid-playback, which is precisely when
it matters.

## G2. FTS5 is declared but never populated, and the delete is the wrong syntax

`content='dedup_groups'` makes this an **external content** table: FTS5 does not
populate or maintain it. As written, search returns zero rows forever.

And §5's `DELETE FROM dedup_groups_fts WHERE rowid = $gid` is not how external
content tables are deleted from — FTS5 needs the old column values to remove the
terms, via the `'delete'` command. A plain DELETE corrupts the index.

```sql
CREATE TRIGGER dg_fts_ai AFTER INSERT ON dedup_groups BEGIN
  INSERT INTO dedup_groups_fts(rowid, name, sort_name)
    VALUES (new.group_id, new.name, new.sort_name);
END;
CREATE TRIGGER dg_fts_ad AFTER DELETE ON dedup_groups BEGIN
  INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
    VALUES ('delete', old.group_id, old.name, old.sort_name);
END;
CREATE TRIGGER dg_fts_au AFTER UPDATE ON dedup_groups BEGIN
  INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
    VALUES ('delete', old.group_id, old.name, old.sort_name);
  INSERT INTO dedup_groups_fts(rowid, name, sort_name)
    VALUES (new.group_id, new.name, new.sort_name);
END;
```

Two consequences worth deciding now:
- Those triggers fire per row on the single writer during a 1.5M-row sync, and
  primary re-election updates `name` often. If the writer slows measurably,
  drop the triggers and populate FTS in bulk at end-of-sync instead.
- `dedup_groups_rebuild` has no triggers, so after the C1/C2 swap the FTS index
  is stale. Run `INSERT INTO dedup_groups_fts(dedup_groups_fts) VALUES('rebuild');`
  as the last statement of the swap.

---

## M1. Merge matrix row 1 describes a condition that cannot occur

`dedup_keys.key` is `PRIMARY KEY`, so one key maps to exactly one group. Two
distinct groups can therefore **never share a key**, and "Side A / Side B /
Shared Authoritative Key → MERGE" can never fire as written.

The real trigger is the *item*, not the groups: the incoming item carries two
authoritative keys that resolve to two different authoritative groups. That is
the proof they are the same title.

Restate row 1 as: **"the inbound item's authoritative keys resolve to ≥2
distinct authoritative groups → MERGE."** Row 2 is already correct (auth group
reached by an authoritative key, second auth group reached only by the item's
fuzzy key → refuse).

Also: the matrix is pairwise but the flowchart feeds it an N-way match. Spell
out the ≥3 case, since it will happen:
1. Partition matched groups into authoritative and provisional.
2. If ≥2 authoritative and the item has authoritative keys into each → merge
   them all into the lowest id.
3. If ≥2 authoritative but only one is reached authoritatively → attach there,
   merge nothing.
4. Absorb **all** matched provisional groups into the winner.
5. If no authoritative group matched, attach to the lowest-id provisional.

## M2. Aggregate recompute on every attach is a 1.5M-query hot path

§3B's attach step says "update aggregated columns", and §5 re-reads siblings on
delete. Done as a `SELECT … WHERE group_id = ?` per attach, that is an extra
query per item on the serialized writer during full sync.

`max_height`, `max_bitrate`, `has_4k` are monotonic under attach — update them
in place with `MAX(existing, incoming)` and no read. `audio_formats` is a set
union, also incremental. Reserve the full recompute for **delete and merge**,
where a maximum can decrease. Same answer for `version_count` (increment, don't
count).

## M3. Synthetic bitrates let fabricated numbers beat measured ones

§6A: "Null/zero bitrates default to standard container averages (20 Mbps for 4K,
8 Mbps for 1080p)." A real 18 Mbps 4K remux then loses to an unknown-bitrate
file credited with 20. A guess should never outrank a measurement.

`cached_items` already stores `size_bytes` — derive it:
`bitrate ≈ size_bytes × 8 / runtime_seconds`. That is a real measurement and it
is available for every file. Only if runtime is also missing should bitrate drop
out of the comparison entirely and fall through to server priority. No constants.

## M4. Explicit pill selection bypasses the hardware filter and has no failover rule

`PickExplicit` goes straight to the health check, skipping P1. Choosing a 4K AV1
pill on hardware without AV1 decode gives software decode and dropped frames
with no warning. Either apply the filter and grey out undecodable pills, or warn
on selection.

Also undefined: when an explicitly chosen source fails mid-playback, does
failover keep the *edition* (same edition on another server) or the *quality*
(best available)? Prefer same edition, then nearest quality, and say which in
the toast.

## M5. Phase 1's gate cannot be met as written

"Batch rebuild reproduces the baseline partition hash over existing items" — the
32,366 / 73,895 baseline came from v3's algorithm. The new algorithm has
provider-keyed episodes, provisional groups and a different fuzzy guard, so it
*should* produce a different partition. There is no prior hash to reproduce.

Replace with two checks that are actually meetable:
- **Determinism:** run the rebuild twice over the same `cached_items`; the two
  partition hashes must be identical. This is the real property Phase 1 needs,
  and it catches non-deterministic primary election and iteration-order bugs.
- **Sanity vs. v3:** group count within a stated band of 32,366, with the
  delta explained (episodes now merge across servers, so expect *fewer* groups
  and *more* merges). Record the new numbers as the v4 baseline.

## M6. Group size cap of 50 is far too loose for two servers

With 2 servers, a legitimate group is 2–6 members. A group of 49 is already
corruption and the cap would never notice. Set it to something like
`4 × connected_server_count` (8 today), and surface tripped groups in the
Settings drift panel rather than only logging — a silent warning in a log file
is the v3.5 failure mode.

## M7. Migration needs to be resumable

§8C says migration runs the rebuild over existing `cached_items` — but the
existing rows predate `height`, `video_codec`, `bitrate`, `hdr_type`, `edition`
and `group_id`. Those have to be backfilled by parsing `json_data` for ~950K
rows, which is minutes of work, not seconds.

Give it a watermark row and batch it, so a close/crash at 400K resumes rather
than restarting. And put it behind the same progress UI as sync — a first launch
that appears frozen for several minutes is the kind of thing that gets reported
as a hang.

---

## Suggested patch order

| # | Item | Why first |
|---|---|---|
| 1 | C1, C2 | Silent cross-group corruption; both land in Phase 1's rebuild |
| 2 | G1 | Phase 5 cannot be built without the table; add it to the Phase 1 schema |
| 3 | G2 | Search is dead and the delete path corrupts the index |
| 4 | M1 | Phase 3 is unimplementable from the current wording |
| 5 | M5 | Otherwise Phase 1 cannot exit |
| 6 | M2, M3, M4, M6, M7 | Correct in their phases |

Everything else in v4.1 stands. The structure is right; these are the places
where the specification is thin enough that an implementer would guess.
