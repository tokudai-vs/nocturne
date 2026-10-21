# Review round 4 — v4.3

Seven of the eight R3 items are correct and complete. One was applied half-way
and is now worse than before it was fixed. Two consequences of the `user_data`
re-key weren't followed through. Everything else is minor.

This is the last round worth running — after these, the open questions are
runtime ones.

---

## 1. R3.6 half-applied: `WHERE EXISTS` converted a visible break into a silent one

The swap now reads:
```sql
UPDATE cached_items SET group_id = (SELECT r.group_id FROM item_group_rebuild r …)
 WHERE EXISTS (SELECT 1 FROM item_group_rebuild r …);
```

An item the rebuild never saw is now **left untouched** — so it keeps its
*pre-rebuild* `group_id`. Group ids are reassigned and reused by the rebuild, so
that stale id almost certainly resolves to a different title in the new table.

That is C1 again, for the subset of items that were missing from the mapping:
shelf renders fine, detail page shows the wrong versions, nothing errors.

Before the guard, those rows went NULL — broken, but *detectably* broken. The
guard made them wrong-but-plausible, which is the worse of the two.

My R3.6 note was three parts and only the first landed. The complete form:

```sql
-- 3a. items the rebuild mapped
UPDATE cached_items
   SET group_id = (SELECT r.group_id FROM item_group_rebuild r
                    WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id)
 WHERE EXISTS (SELECT 1 FROM item_group_rebuild r
                WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id);

-- 3b. items it did not: clear deliberately, never leave a stale id
UPDATE cached_items
   SET group_id = NULL
 WHERE NOT EXISTS (SELECT 1 FROM item_group_rebuild r
                    WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id);
```

Then, after `COMMIT`, run incremental ingest over
`SELECT … FROM cached_items WHERE group_id IS NULL`. That re-homes them through
the normal path and makes "rebuild while sync is running" correct rather than
merely forbidden — which matters, because nothing in the plan forbids it.

**Add to Phase 1's gate:** after the swap and the re-ingest,
`SELECT COUNT(*) FROM cached_items WHERE group_id IS NULL` must be 0. That one
assertion covers this whole class.

---

## 2. Finished titles stay in Continue Watching

`user_data` is now per `(server_id, item_id)`, and the query filters
`u.played = 0` **per row**. Finish an episode on Valkyrie and only Valkyrie's row
gets `played = 1`; Raptor's row still has `position_ticks > 0, played = 0`, so it
survives the filter and the group still produces a card.

§7B marks played on all servers at ≥90%, but that is the *remote* push, routed
through `sibling_write_queue` — it can be pending or failing for hours while the
card sits there.

State explicitly: **on completion, write `played = 1` and `position_ticks = 0`
to the local `user_data` row of every `(server_id, item_id)` in the group,
immediately and synchronously.** The queue carries only the remote propagation.
Same for an explicit "mark unwatched".

The natural implementation updates one row, so this needs to be written down or
it won't happen.

## 3. `MAX(u.position_ticks)` returns the wrong resume point

Watch Valkyrie's copy to 90%, then start the Raptor copy from the beginning and
stop at 10%. `MAX(position_ticks)` reports 90% — stale data from a version you
are no longer watching. You want the position belonging to the **most recently
played** version, not the largest one.

SQLite has a documented extension that does exactly this: in an aggregate query
using `max()` or `min()`, bare columns take their values from the row that
produced the extreme. So drop the inner `MAX()`s and leave the columns bare:

```sql
SELECT g.*,
       MAX(u.last_played_utc) AS latest_played,
       u.position_ticks,          -- from the row with the latest last_played_utc
       u.runtime_ticks,
       u.server_id, u.item_id     -- also the right version to resume on
  FROM user_data u
  JOIN cached_items c ON c.id = u.item_id AND c.server_id = u.server_id
  JOIN dedup_groups g ON g.group_id = c.group_id
 WHERE u.position_ticks > 0 AND u.played = 0
 GROUP BY g.group_id
 ORDER BY latest_played DESC
 LIMIT 20;
```

This also hands you the server/item that was last played, which is what the
resume should prefer when it is reachable. If you would rather not depend on the
bare-column extension, a `ROW_NUMBER() OVER (PARTITION BY c.group_id ORDER BY
u.last_played_utc DESC)` subquery is the portable equivalent — but keep the
current `MAX(position_ticks)` out of it either way.

---

## Minor

- **`mark_played` is sticky in the queue.** `mark_played = MAX(existing,
  incoming)` means a queued completion that failed while the server was offline
  survives a later rewatch: finish → queue `played=1` → server down → rewatch to
  5% → upsert writes `position=5%` but `mark_played` stays 1 → server comes back
  and marks it played at 5%. Let a newer `queued_utc` carrying `mark_played = 0`
  win instead of taking the max.
- **Group-size cap still isn't checked on merge.** Flagged in R3; `4 × servers`
  applies to fuzzy attach only, and merging two groups of 5 gives 10 unchecked.
  Flag in drift diagnostics rather than blocking.
- **`DROP TRIGGER IF EXISTS` before the re-create is a harmless no-op** (the
  triggers died with `dedup_groups_old`). Worth keeping for the case where a
  previous swap aborted part-way.

---

## Then freeze

| # | Item | Lands in |
|---|---|---|
| 1 | §1 — NULL + re-ingest + the `IS NULL` assertion | Phase 1 |
| 2 | §2 — local played write across all versions | Phase 5 (state it now) |
| 3 | §3 — bare columns instead of `MAX(position_ticks)` | Phase 5 |
| 4 | Minors | their own phases |

§1 is the only one that corrupts; §2 and §3 are visible-wrong rather than
silent-wrong. After them the plan is done — further passes will keep finding
smaller things, and the bigger remaining risks (sync throughput, 24p pacing,
cross-server behavior with both servers live) only show up on the Windows box.
