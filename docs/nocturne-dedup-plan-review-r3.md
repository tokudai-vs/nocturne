# Review round 3 — v4.2

C1, C2, G2, M1–M7 are all correctly incorporated. The architecture is settled;
everything below is implementation-level, and most of it lives in the two
hot paths the plan now specifies in SQL (the attach UPDATE and the swap
transaction). That is the right place to be after three rounds.

Eight items. The first three are corruption-class.

---

## 1. The rebuild corrupts `user_data` exactly the way C1 corrupted `cached_items`

§8A now remaps `cached_items.group_id` inside the swap. It does **not** remap
`user_data.group_id`. Group ids are reassigned by every rebuild and are reused
(no `AUTOINCREMENT`), so after a rebuild every Continue Watching row points at an
arbitrary different title.

Worse: `user_data.group_id` is `PRIMARY KEY`. If two old groups map onto one new
group, two rows want the same key — constraint violation, and the whole swap
transaction aborts.

The merge path already handles this (`UPDATE user_data SET group_id = Winner`);
the rebuild path doesn't.

**The deeper problem is the key choice.** Emby's user data is per
`(server, item)`. Keying `user_data` on `group_id` is a lossy projection of that,
which then has to be un-projected every time you write to siblings — and it has
to be maintained through every regroup, forever.

**Recommendation: key `user_data` on `(server_id, item_id)` instead.** It is
stable across every merge, rebuild and re-grouping, it matches the source of
truth, and last-write-wins falls out naturally:

```sql
CREATE TABLE user_data (
    server_id       TEXT NOT NULL,
    item_id         TEXT NOT NULL,
    position_ticks  INTEGER NOT NULL DEFAULT 0,
    runtime_ticks   INTEGER,
    played          INTEGER NOT NULL DEFAULT 0,
    last_played_utc INTEGER,
    updated_locally INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (server_id, item_id)
) WITHOUT ROWID;

CREATE INDEX ix_user_data_continue ON user_data(last_played_utc DESC)
    WHERE position_ticks > 0 AND played = 0;
```

Continue Watching then rolls up to the group at query time:

```sql
SELECT g.*, MAX(u.last_played_utc) AS lp,
       MAX(u.position_ticks) AS position_ticks
  FROM user_data u
  JOIN cached_items c ON c.id = u.item_id AND c.server_id = u.server_id
  JOIN dedup_groups g ON g.group_id = c.group_id
 WHERE u.position_ticks > 0 AND u.played = 0
 GROUP BY g.group_id
 ORDER BY lp DESC
 LIMIT 20;
```

Three joins, but over a set of hundreds of rows, not millions — and the group
rollup is what produces the single card. Zero maintenance through rebuilds.

If you keep `group_id` as the key instead, the swap must remap it with an
explicit collision rule (keep the row with the newer `last_played_utc`), and
that rule has to be written down or it will be discovered as a crash.

## 2. The swap destroys the FTS5 triggers

Triggers belong to their table. `ALTER TABLE dedup_groups RENAME TO
dedup_groups_old` carries `dg_fts_ai` / `dg_fts_ad` / `dg_fts_au` across to
`dedup_groups_old`, and `DROP TABLE dedup_groups_old` then deletes them.
`dedup_groups_rebuild` never had triggers, so after the swap the new
`dedup_groups` has none.

Step 3's `VALUES('rebuild')` repopulates the index once, so search works
immediately after a rebuild and then **silently goes stale from the next
incremental sync onward** — new titles never become searchable, renamed ones
keep their old names.

Fix: re-create all three triggers inside the swap transaction, after the renames
and before the FTS rebuild. Worth an explicit test: rebuild, add one item, search
for it.

## 3. `MAX()` propagates NULL and wipes the aggregates

SQLite's scalar `max()` returns NULL if *any* argument is NULL. In §3B:

```sql
max_height  = MAX(max_height, $itemHeight),
max_bitrate = MAX(max_bitrate, $itemBitrate),
has_4k      = MAX(has_4k, $is4k)
```

`max_height` and `max_bitrate` are nullable with no default, and `$itemHeight` /
`$itemBitrate` are frequently NULL (M3 explicitly allows bitrate to be NULL when
it can't be measured). So one version with unknown height **erases** a group's
existing 2160. And `has_4k` is `NOT NULL` — a NULL `$is4k` turns the UPDATE into
a constraint failure inside the single writer.

```sql
max_height  = MAX(COALESCE(max_height, 0),  COALESCE($itemHeight, 0)),
max_bitrate = MAX(COALESCE(max_bitrate, 0), COALESCE($itemBitrate, 0)),
has_4k      = MAX(has_4k, COALESCE($is4k, 0))
```

This silently degrades the shelf (`has_4k` badges disappearing) long before
anyone traces it back.

---

## 4. `instr()` substring matching corrupts `audio_formats`

```sql
WHEN instr(audio_formats, $itemAudio) > 0 THEN audio_formats
```

`instr('EAC3', 'AC3') > 0` is true, so AC3 is never added to a group that
already has EAC3. Same for `DTS` inside `DTS-HD`, `TrueHD` inside anything
containing it. Delimit both sides:

```sql
WHEN instr(',' || audio_formats || ',', ',' || $itemAudio || ',') > 0
```

## 5. `version_count` drifts on re-sync, and the delete path depends on it

`version_count = version_count + 1` runs on every attach. Incremental sync
re-sees changed items, so the same `(id, server_id)` attaches more than once and
the count inflates with no correction until a merge or delete.

§5 then branches on `version_count <= 0` to drop the group. An inflated count
means groups never reach zero — orphan groups and blank cards survive, which is
the exact failure B4 exists to prevent.

Two fixes, do both:
- Only increment when the item is genuinely new: `INSERT … ON CONFLICT(id,
  server_id) DO UPDATE …` and bump `version_count` only on the insert branch.
- Don't let the delete path trust the counter — after deleting, do
  `SELECT COUNT(*) FROM cached_items WHERE group_id = ?` and act on the real
  number. Deletes are rare; the query is free.

## 6. The reassignment UPDATE NULLs any item the rebuild didn't see

```sql
UPDATE cached_items SET group_id = (SELECT r.group_id FROM item_group_rebuild r …)
```

An uncorrelated miss yields NULL, so any row absent from `item_group_rebuild`
loses its group silently and vanishes from every shelf.

That is not hypothetical: the rebuild reads `cached_items`, builds groups, then
swaps — anything written by sync in between is not in the mapping. The plan
doesn't say rebuild and sync are mutually exclusive.

Add, in the same transaction:
```sql
UPDATE cached_items SET group_id = (SELECT …)
 WHERE EXISTS (SELECT 1 FROM item_group_rebuild r
                WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id);
```
then assert `SELECT COUNT(*) FROM cached_items WHERE group_id IS NULL` and feed
any survivors back through incremental ingest. That also makes the
rebuild-during-sync case correct instead of forbidden.

## 7. The sibling queue can rewind a position

`sibling_write_queue` has no uniqueness constraint. A session with several
pauses queues several rows for the same `(server_id, item_id)`. If an attempt
fails and ordering isn't enforced on drain, an older position can be written
after a newer one — the sibling rewinds, and on the next sync last-write-wins
propagates the rewind back.

Add `UNIQUE(server_id, item_id)` with an upsert that replaces
`position_ticks` and bumps `queued_utc`, and drain in `queued_utc` order. One
pending write per item per server is all you ever need.

## 8. Phase 2's gate is unmeetable, same shape as M5 was

> Partition hash matches rebuild on an attach-only test corpus.

Phase 2 is attach-only; the rebuild does full union-find including merges. On
any corpus where a merge is required the two cannot match, by construction.

Either run the rebuild in an attach-only mode for this comparison, or define the
test corpus as one where no merge is reachable (every item carries an
authoritative key, no fuzzy-created groups). State which — otherwise Phase 2
either blocks forever or gets waved through, and Phase 3 inherits whatever
Phase 2 got wrong.

---

## Minor

- **Merges can exceed the group-size cap.** §3C caps fuzzy *attach* at
  `4 × servers`. Merging two groups of 5 produces 10 and isn't checked. Flag it
  in drift diagnostics rather than blocking — a legitimate title with four
  editions on two servers legitimately reaches 8.
- **Deleting a group leaves its `sibling_write_queue` rows behind.** Add the
  cleanup to §5's drop branch.
- **The reassignment UPDATE is 1.5M correlated lookups under the write lock.**
  Fine for a rare rebuild, but expect tens of seconds and make sure the UI shows
  it rather than appearing hung.

---

## Patch order

| # | Item | Why |
|---|---|---|
| 1 | §1 `user_data` key | Decide before Phase 1 — it is a schema change, and re-keying later means migrating live resume state |
| 2 | §2, §6 | Both land inside the Phase 1 swap transaction |
| 3 | §3, §4, §5 | Three lines of SQL in the Phase 2 hot path; all three silently corrupt |
| 4 | §8 | Phase 2 cannot exit without it |
| 5 | §7 | Phase 5 |

After this round the plan is implementable as written. Worth freezing it and
starting Phase 0 rather than running a fourth pass — the remaining risk has moved
to runtime behavior, which only the Windows box can tell you about.
