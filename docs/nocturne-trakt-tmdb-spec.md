# Nocturne — Trakt & TMDB Integration
## Technical Specification v1.0 (implementation-ready)

Supersedes plan v1/v2/v3. Self-contained: everything needed to implement is here.

**Depends on** the dedup plan (v4.4 Frozen) — specifically the `dedup_groups` /
`dedup_keys` / `cached_items` schema, the authoritative key extractor (§4), the
single-writer rule (V4), and `sibling_write_queue`.

---

# 1. Scope

| In scope | Out of scope |
|---|---|
| Trakt device-flow auth (bundled + BYO client ID) | Trakt collection sync |
| Scrobble push (start/pause/stop) | Trakt lists, ratings, comments |
| Watched-state pull Trakt → local → Emby | Un-watching from Trakt removals (manual reconcile only) |
| Merged watchlist view (Emby favourites + Trakt watchlist) | Any sync *between* those two sources |
| Add/remove to watchlist from inside Nocturne | Person/cast metadata |
| TMDB artwork + metadata fallback | TMDB as a primary metadata source |

---

# 2. Design decisions

**D1 — Device flow for both credential modes.** A user's own Trakt app created
today faces the same production redirect-URI restriction, so BYO gains nothing
from the authorization-code flow and costs a localhost listener or custom scheme.
One implementation, two sources of `client_id`.

**D2 — No client secret.** Trakt stopped issuing secrets on 2026-10-01 and
deprecated them for user sign-in on every `/oauth` endpoint. Nocturne never asks
for one and never sends one.

**D3 — PKCE on the device endpoints.** Optional in the schema; implement it.
Closes the window where a copied client ID completes someone else's pending
authorization.

**D4 — One provider-ID extractor.** The dedup engine's key builder already
produces tmdb/imdb/tvdb + season/episode. Trakt and TMDB both consume it. No
second extractor.

**D5 — Group-level identity.** Scrobbles, watchlist entries and TMDB lookups are
keyed on the title, not the file. Independent of which server streamed.

**D6 — Third-party failure never touches playback.** Queue, retry, surface in
Settings. Never block, never error a play.

**D7 — Never echo to the originating peer.** `user_data.origin` ∈
{`local`, `emby:<server_id>`, `trakt`}. A change is never propagated to the peer
named in its own origin. Covers every pair with one rule.

**D8 — Apply the source's timestamp, never `now()`.** A pulled Trakt watch is
stored with Trakt's `watched_at`. Stamping pull time defeats last-write-wins and
re-triggers outbound pushes forever.

**D9 — Stable refs, never `group_id`.** Group ids are reassigned and reused by
every dedup rebuild. `user_data`, `watchlist_entries` and `tmdb_metadata` are all
keyed on identities that survive: `(server_id, item_id)` or a provider key.

**D10 — Favourites and watchlist do not sync.** Two independent read sources,
merged for display only. The sole writes are user actions taken inside Nocturne,
which go to both peers.

**D11 — TMDB is a fallback, not a source.** Enrichment is attempted only when
*no version on any server* supplies the asset. Emby always wins.

**D12 — TMDB runs after sync completes.** Not concurrent. Sync finishing is what
makes the library usable; enrichment is cosmetic and waits its turn.

---

# 3. Artwork resolution (prerequisite change to the dedup schema)

D11 forces a change to `dedup_groups`. Today the plan copies `image_tag` from the
elected primary — but the primary is elected on *metadata completeness*, and a
different version on a different server may be the one that actually has a
poster. Copying from the primary would send a group to TMDB while a sibling had
artwork all along.

Artwork is therefore an **aggregate over versions**, not a copy from the primary —
and because the audit's image bug was "URLs built from the active server", the
group must record *which version* supplies the image, not just the tag.

### 3.1 Schema delta on `dedup_groups`

```sql
ALTER TABLE dedup_groups ADD COLUMN image_item_id     TEXT;    -- version supplying the poster
ALTER TABLE dedup_groups ADD COLUMN image_server_id   TEXT;
ALTER TABLE dedup_groups ADD COLUMN backdrop_item_id  TEXT;
ALTER TABLE dedup_groups ADD COLUMN backdrop_server_id TEXT;
ALTER TABLE dedup_groups ADD COLUMN tmdb_key          TEXT;    -- 'tmdb:movie:550' | 'tmdb:tv:1396'

CREATE INDEX IF NOT EXISTS ix_groups_needs_art
    ON dedup_groups(group_id)
    WHERE image_tag IS NULL AND tmdb_key IS NOT NULL;
```

`image_tag` stays, now meaning "the tag of the elected image version".
`backdrop_tag` likewise. The partial index is the enrichment work queue — tiny,
and the query is an index-only scan.

### 3.2 Image election

Deterministic, same shape as primary election but on a different criterion:

```
1. versions where image_tag IS NOT NULL
2. order by: server priority ASC, then (server_id, item_id) lexicographic
3. first row wins → image_tag, image_item_id, image_server_id
4. none → all three NULL → group is TMDB-eligible
```

Run on attach (monotonic: only replaces NULL, or a worse server-priority source),
and on full recompute during **delete** and **merge**, alongside the other
aggregates.

### 3.3 Resolution order at render time

| Order | Source | URL built from |
|---|---|---|
| 1 | `image_server_id` + `image_item_id` + `image_tag` | **that version's owning server** |
| 2 | `tmdb_metadata.poster_path` | `image.tmdb.org` |
| 3 | placeholder tile (title text on neutral background) | — |

Never build an image URL from the active server. That is the exact audit bug
(`AUDIT-2026-07`), and the dedup fallback masks it.

---

# 4. Database schema (complete DDL)

```sql
-- 4.1 Trakt / Emby user data. Keyed on the native identity (D9).
CREATE TABLE user_data (
    server_id       TEXT NOT NULL,
    item_id         TEXT NOT NULL,
    position_ticks  INTEGER NOT NULL DEFAULT 0,
    runtime_ticks   INTEGER,
    played          INTEGER NOT NULL DEFAULT 0,
    last_played_utc INTEGER,
    origin          TEXT NOT NULL DEFAULT 'local',  -- 'local'|'emby:<id>'|'trakt'  (D7)
    updated_locally INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (server_id, item_id)
) WITHOUT ROWID;

CREATE INDEX ix_user_data_continue ON user_data(last_played_utc DESC)
    WHERE position_ticks > 0 AND played = 0;

-- 4.2 Merged watchlist. Display-only union (D10).
CREATE TABLE watchlist_entries (
    source     TEXT NOT NULL,    -- 'trakt' | '<server_id>'
    ref        TEXT NOT NULL,    -- trakt: authoritative key; emby: item_id
    item_type  TEXT NOT NULL,    -- 'Movie' | 'Series'
    added_utc  INTEGER NOT NULL,
    title      TEXT,             -- cached; only needed for entries with no group
    year       INTEGER,
    tmdb_key   TEXT,             -- for unowned entries, enables TMDB artwork
    pending_op TEXT,             -- 'add'|'remove'|NULL — Nocturne-originated only
    PRIMARY KEY (source, ref)
) WITHOUT ROWID;

-- 4.3 TMDB fallback cache. Keyed on provider id so rebuilds don't discard it (D9).
CREATE TABLE tmdb_metadata (
    provider_key  TEXT PRIMARY KEY,  -- 'tmdb:movie:550' | 'tmdb:tv:1396' | 'tmdb:tv:1396:s01e01'
    poster_path   TEXT,
    backdrop_path TEXT,
    still_path    TEXT,
    overview      TEXT,
    vote_average  REAL,
    fetched_utc   INTEGER NOT NULL,
    not_found     INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;

-- 4.4 Trakt scrobble queue. Ordered event stream — deliberately NO unique key.
CREATE TABLE trakt_queue (
    queue_id   INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id   INTEGER NOT NULL,
    action     TEXT NOT NULL,    -- 'start'|'pause'|'stop'|'watchlist_add'|'watchlist_remove'
    progress   REAL NOT NULL DEFAULT 0,
    payload    TEXT,             -- serialized ids, resolved at enqueue time
    queued_utc INTEGER NOT NULL,
    attempts   INTEGER NOT NULL DEFAULT 0
);

-- 4.5 Integration state (one row per integration)
CREATE TABLE integration_state (
    name       TEXT PRIMARY KEY,   -- 'trakt' | 'tmdb'
    key        TEXT NOT NULL,
    value      TEXT NOT NULL
);
```

`sibling_write_queue` is reused unchanged for Emby-side writes (it has
`UNIQUE(server_id,item_id)` with upsert — correct there, wrong for scrobbles).

### Why `trakt_queue` has no unique constraint
`sibling_write_queue` collapses to "latest position wins" because that is what an
Emby user-data write means. A scrobble is an **ordered event stream** — collapsing
start/pause/stop into one row destroys the sequence Trakt needs. Drain in
`queued_utc ASC`; drop `start`/`pause` rows older than 5 minutes on drain (a
stale "started" is noise); always deliver `stop`.

---

# 5. Credential model

```csharp
enum CredentialMode { Bundled, Custom }

sealed class TraktAuthState {
    CredentialMode Mode;
    string?  CustomClientId;        // settings.json — not a secret
    byte[]?  AccessTokenDpapi;      // DPAPI, CurrentUser scope
    byte[]?  RefreshTokenDpapi;
    DateTime ExpiresUtc;
    string?  ClientIdUsedForToken;  // invalidation guard
    string?  Username;              // display only
}

sealed class TmdbAuthState {
    CredentialMode Mode;
    string?  CustomApiKey;          // DPAPI if present
}
```

**Rules**

1. Tokens are never written to `settings.json` in plaintext. Wrap with
   `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`.
   Settings files get pasted into issue reports.
2. `ClientIdUsedForToken` is load-bearing. A refresh token is valid only for the
   client ID that minted it. On load, if it differs from
   `GetActiveClientId()`, clear tokens and set state to Disconnected — do not
   discover this later as a 401.
3. `GetActiveClientId()` returns `CustomClientId` when Mode is Custom and
   non-empty, else the compiled-in constant. Everything downstream is identical.

---

# 6. Trakt authentication — device flow

### 6.1 Request codes

```http
POST https://api.trakt.tv/oauth/device/code
Content-Type: application/json

{ "client_id": "<id>",
  "code_challenge": "<base64url(sha256(verifier))>",
  "code_challenge_method": "S256" }
```

Response: `device_code`, `user_code`, `verification_url`, `expires_in`,
`interval`.

### 6.2 Poll for the token

```http
POST https://api.trakt.tv/oauth/device/token
{ "code": "<device_code>", "client_id": "<id>", "code_verifier": "<verifier>" }
```

```csharp
var deadline = DateTime.UtcNow.AddSeconds(resp.ExpiresIn);
var interval = TimeSpan.FromSeconds(resp.Interval);

while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
    await Task.Delay(interval, ct);
    var r = await PollAsync(deviceCode, verifier, ct);
    switch (r.Status) {
        case Authorized:  return r.Tokens;                               // 200
        case Pending:     continue;                                      // normal, every poll
        case SlowDown:    interval += TimeSpan.FromSeconds(1); continue;
        case NotFound:    return Result.Fail("Invalid device code");
        case AlreadyUsed: return Result.Fail("Code already used");
        case Expired:     return Result.Expired;                         // offer restart
        case Denied:      return Result.Denied;                          // user declined
    }
}
return Result.Expired;
```

**Implementation rules**

- **Pending is not an error.** It fires on nearly every poll. Treating any
  non-200 as failure is the standard bug in this flow.
- Map every documented status explicitly against Trakt's device-token endpoint
  reference. Never `catch { fail; }`.
- Poll at the returned `interval`; honour slow-down by increasing it.
- Cancellable; stops the instant the dialog closes.
- **Never log `device_code`, `access_token`, `refresh_token`, or the PKCE
  verifier.** `user_code` is safe. Add this to the file logger's redaction rules.

### 6.3 Token lifecycle

- Read `expires_in`; do not hardcode a lifetime.
- Refresh proactively when within 24h of expiry — on app start and before a
  queue drain, not reactively on a 401.
- Refresh needs `refresh_token` + `client_id`. No secret.
- **Failed refresh is a visible state.** Clear tokens, set Disconnected, show
  "Trakt needs reconnecting" in Settings. Silent token death that only shows up
  as missing scrobbles is the swallowed-exception failure shape again.
- Disconnect calls `/oauth/revoke`, then clears local state regardless of the
  revoke result.

---

# 7. Scrobble push

### 7.1 Payload, from the group's authoritative keys (D4/D5)

| Content | Body |
|---|---|
| Movie | `{ "movie": { "ids": { "tmdb": N, "imdb": "ttN" } }, "progress": P }` |
| Episode | `{ "show": { "ids": { "tvdb": N } }, "episode": { "season": S, "number": E }, "progress": P }` |

`P = position_ticks / runtime_ticks * 100`.

### 7.2 Events

| Playback event | Call |
|---|---|
| Play, resume from pause | `POST /scrobble/start` |
| Pause | `POST /scrobble/pause` |
| Stop, EOF, Esc-to-exit | `POST /scrobble/stop` |

### 7.3 Rules

- **No double completion.** `scrobble/stop` above Trakt's own completion
  threshold already marks watched and writes history. Nocturne's ≥90%
  mark-played is an **Emby** action across sibling servers. Never also call a
  Trakt add-to-history, or every finished episode gets two entries.
- **Provisional groups cannot be scrobbled** — no authoritative ids. Skip
  silently at the scrobble layer; surface a count in Settings; do not log an
  error per play.
- **D7 applies**: a playback whose `user_data.origin` is `trakt` produces no
  Trakt write.
- A repeat-scrobble rejection from Trakt is a **success** — drop the queue row,
  don't retry forever.

---

# 8. Watched-state pull (Trakt → local → Emby)

### 8.1 Cheap change detection

```
every 15 min, on app start, and immediately after Connect:
  GET /sync/last_activities
    movies.watched_at    > stored → GET /sync/watched/movies
    episodes.watched_at  > stored → GET /sync/watched/shows
    watchlist.updated_at > stored → GET /sync/watchlist/movies, /shows
  persist new timestamps in integration_state
```

Steady state is one small request per interval. Volume is sized by watch history,
not by the 1.5M-item library.

### 8.2 Apply

For each watched entry:

```
1. resolve key:  movie   → 'tmdb:movie:{id}'
                 episode → 'ep:tvdb:{showId}:s{n}e{n}'
2. dedup_keys lookup → group_id.  No match → skip (not in library)
3. for each (server_id, item_id) in that group:
     if incoming watched_at <= user_data.last_played_utc → skip      ← idempotence
     else upsert: played=1, position_ticks=0,
                  last_played_utc = <Trakt watched_at>,              ← D8
                  origin='trakt'
4. enqueue each touched (server_id,item_id) to sibling_write_queue
```

Step 3's timestamp comparison is what makes the pull idempotent and loop-free.
Step 4 is safe under D7 because the origin is `trakt`, not any Emby server.

### 8.3 Limits

- **Additions only.** Removal from Trakt history does not un-watch locally;
  detecting it needs a full-set diff every cycle. A **Full Trakt reconcile**
  button in Settings does the diff on demand. Say so in the UI copy.
- First pull on an old Trakt account can be large. Batch through the single
  writer with progress, like the migration.
- Background worker. Paused during playback, like sync.

---

# 9. Watchlist

### 9.1 Model

Two independent read sources, merged for display. **Nothing syncs between them**
(D10). Pull Emby favourites (`IsFavorite`) per server and the Trakt watchlist on
their own schedules into `watchlist_entries`.

### 9.2 Query

```sql
-- Emby-sourced entries
SELECT g.group_id, g.name, g.year, g.image_tag, g.image_server_id, g.image_item_id,
       MIN(w.added_utc) AS added
  FROM watchlist_entries w
  JOIN cached_items c ON c.server_id = w.source AND c.id = w.ref
  JOIN dedup_groups g ON g.group_id = c.group_id
 WHERE w.source <> 'trakt' AND COALESCE(w.pending_op,'') <> 'remove'
 GROUP BY g.group_id
UNION
-- Trakt-sourced entries that exist locally
SELECT g.group_id, g.name, g.year, g.image_tag, g.image_server_id, g.image_item_id,
       w.added_utc
  FROM watchlist_entries w
  JOIN dedup_keys k ON k.key = w.ref
  JOIN dedup_groups g ON g.group_id = k.group_id
 WHERE w.source = 'trakt' AND COALESCE(w.pending_op,'') <> 'remove';
-- plus: Trakt entries with no dedup_keys match → rendered from w.title/w.year/w.tmdb_key
```

Roll up by `group_id` in the view model. **No source badges** — the merged list
is the product.

`pending_op = 'remove'` rows are filtered *before* the queue drains, or a pull
landing mid-flight makes the item reappear and it reads as a missed click.

### 9.3 Writes (Nocturne-originated only)

**Add** → `trakt_queue` (`watchlist_add`) **and** a favourite write to every
server hosting the title. An unowned title has no Emby item, so Trakt only.

**Remove** → remove from both peers wherever present. A no-op against a peer that
doesn't have it is a success.

Optimistic: flip local state, write `pending_op`, drain. Revert only on a
definitive rejection, never on a transport failure (those retry).

### 9.4 Entries not in the library

Text tile: title, year, "Not in your library", no Play affordance. Once TMDB is
present they get artwork automatically via `watchlist_entries.tmdb_key` — no
extra work.

---

# 10. TMDB enrichment

### 10.1 Trigger (D12)

Runs **only after a sync pass reaches `complete`**. Hook the sync state machine's
transition to `complete`; enrichment then processes whatever is currently
missing. Later syncs re-trigger it for newly added items. Never concurrent with
sync, never during playback.

### 10.2 Eligibility (D11)

A group is eligible only when **no version on any server** supplies the asset —
i.e. after §3.2 image election leaves `image_tag IS NULL`. The partial index
`ix_groups_needs_art` is exactly this set.

```sql
SELECT group_id, tmdb_key, item_type
  FROM dedup_groups
 WHERE image_tag IS NULL
   AND tmdb_key IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM tmdb_metadata t
        WHERE t.provider_key = dedup_groups.tmdb_key
          AND (t.not_found = 1 OR t.fetched_utc > $staleCutoff));
```

Field-level, not all-or-nothing: a group with a poster but no overview requests
only the overview. Emby always wins where Emby has something.

### 10.3 Unit of work — group and season, never item

| Content | Unit | Scale |
|---|---|---|
| Movie, Series | one call per **dedup group** | ~32K, not 1.5M |
| Episodes | one `/3/tv/{id}/season/{n}` per **season** (returns every episode with `still_path`) | ~10–20K, not 676K |

Dedup pays for itself here: the same title on two servers in three qualities is
one lookup. Per-item would be 1.5M calls and would not finish.

### 10.4 Matching — authoritative ids only (D11)

Query by the TMDB id already extracted for dedup keys. **No title+year search
fallback.** A fuzzy TMDB match puts the wrong poster on a title permanently and
silently — the same failure class the dedup plan refuses fuzzy merges to avoid.
Provisional groups keep the placeholder.

### 10.5 Endpoints

```
GET /3/configuration                  → image base_url + size lists (cache 30d)
GET /3/movie/{id}                     → poster_path, backdrop_path, overview, vote_average
GET /3/tv/{id}                        → same for series
GET /3/tv/{id}/season/{n}             → episodes[] with still_path, overview
```

Auth: `Authorization: Bearer <read access token>`.

### 10.6 Pipeline

```
sync complete
  → eligibility query (indexed)
  → bounded channel (cap 5,000)
  → N fetchers under one shared token bucket: 20 req/s
       429 → exponential back-off, honour Retry-After, halve the bucket for 60s
  → single writer (the dedup writer) → tmdb_metadata
```

TMDB's documented ceiling sits around 40 req/s and is explicitly subject to
change; 20 is the self-imposed budget. ~32K groups ≈ 27 minutes cold.

`not_found = 1` is mandatory — without it every pass retries the same dead ids
forever. Re-check those monthly, not per sync.

### 10.7 Images

`{base_url}{size}{path}` — `w342` for shelf posters, `w780`/`w1280` for
backdrops, `w300` for stills. **Request the size that matches the drawn
dimension** and feed it into the `DecodePixelWidth` path; pulling `original` and
downscaling reintroduces the bitmap-memory problem. Cache to the existing LRU
disk cache so eviction and the Settings cache-size display keep working.

### 10.8 Attribution — mandatory

Conditions of use, not courtesy:

- TMDB logo, approved form, unaltered in colour/ratio/orientation, **less
  prominent** than Nocturne's own mark.
- Exact notice in About/Credits: *"This product uses the TMDB API but is not
  endorsed or certified by TMDB."*

Free use covers non-commercial projects with attribution. This lands on the About
page already flagged as issue #18 — build it once with the logging affordances
and this together.

---

# 11. Service interfaces

```csharp
interface ITraktAuthService {
    Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct);
    Task<TraktAuthResult>    PollForTokenAsync(DeviceCodeResponse c, CancellationToken ct);
    Task<bool>               RefreshIfNeededAsync(CancellationToken ct);
    Task                     DisconnectAsync(CancellationToken ct);
    TraktConnectionState     State { get; }
    event EventHandler<TraktConnectionState> StateChanged;
}

interface ITraktScrobbleService {           // enqueue only; never awaits the network
    void Start(long groupId, double progress);
    void Pause(long groupId, double progress);
    void Stop (long groupId, double progress);
}

interface ITraktSyncService {
    Task PullWatchedAsync(CancellationToken ct);      // §8
    Task PullWatchlistAsync(CancellationToken ct);    // §9
    Task FullReconcileAsync(IProgress<int> p, CancellationToken ct);
}

interface IWatchlistService {
    Task<IReadOnlyList<WatchlistCard>> GetAsync(CancellationToken ct);
    Task AddAsync(long groupId, CancellationToken ct);
    Task RemoveAsync(long groupId, CancellationToken ct);
    Task AddUnownedAsync(string tmdbKey, CancellationToken ct);
}

interface ITmdbEnrichmentService {
    Task RunAsync(IProgress<EnrichProgress> p, CancellationToken ct);  // called on sync complete
    Task<TmdbMetadata?> GetAsync(string providerKey, CancellationToken ct);
}

interface IQueueDrainWorker {               // one instance per queue
    Task DrainAsync(CancellationToken ct);
}
```

All network-touching services take a `CancellationToken` and are cancelled on
playback start where D6/D12 require it.

---

# 12. UI specification

### 12.1 Settings → Integrations → Trakt

**Disconnected:** one primary **Connect Trakt** button, nothing else.

**Connect dialog:**
- `user_code` large and monospace, with a copy button
- `verification_url` as a link, plus a QR code (people reach for a phone)
- countdown from `expires_in`; Restart button on lapse
- Cancel cancels the polling task

**Connected:** username + avatar, "Scrobble playback" toggle, Disconnect,
last-pull timestamp, queued/failed counts, **Full Trakt reconcile**.

**Advanced** (`Expander`, collapsed):
> **Use my own Trakt app**
> Nocturne connects through its own Trakt application by default. To use your
> own, create one at trakt.tv, choose Production, and paste the Client ID. No
> client secret is needed.

Editing the field invalidates tokens (§5 rule 2) and returns to Disconnected with
a one-line explanation. Clearing it reverts to Bundled.

### 12.2 Settings → Integrations → TMDB

Status line (`Artwork filled for N titles · M pending`), a **Re-run enrichment**
button, and the same Advanced "use my own API key" expander.

### 12.3 Navigation

`Home │ Watchlist │ Libraries` — Watchlist inserted between the two existing
items.

### 12.4 Watchlist page

Poster grid identical to a library shelf. Owned and unowned side by side; unowned
render as text tiles until TMDB fills them. No source indicators.

**Empty state:** *"Nothing here yet — add titles from any poster, or connect
Trakt to bring yours across."*

### 12.5 Add/remove affordances

**Poster card:** `+` / filled-bookmark in the top-right, revealed on hover **and
on keyboard focus**. Outside normal layout flow so cards don't reflow on hover.

**Detail page:** toggle beside Play, labelled by state ("Add to Watchlist" /
"In Watchlist").

Both are optimistic and reflect `pending_op` immediately.

---

# 13. Migration

On first run of the new build:

1. Detect legacy Trakt credentials (user-entered client ID **and** secret).
2. Preserve the client ID into `CustomClientId`, keep Mode = Custom. That user
   deliberately set it up; do not silently move them to the shared app.
3. Discard the stored secret. It is no longer used anywhere.
4. Discard stored tokens — minted under the old flow, may not carry over.
5. One non-blocking notice: *"Trakt needs reconnecting — one click, and you no
   longer need a client secret."*

Users with no prior Trakt setup land on Bundled with nothing to configure.

Schema migration: `user_data.origin` defaults to `'local'` for existing rows,
which is correct — anything already there came from local playback.

---

# 14. Logging and diagnostics

**Never logged:** `device_code`, `access_token`, `refresh_token`, PKCE verifier,
TMDB API key. Add them to the file logger's redaction list.
**Safe to log:** `user_code`, group ids, provider keys, HTTP status codes.

**Surfaced in Settings, not only in the log file:**
`trakt_queue` depth + failures · `sibling_write_queue` depth + failures ·
last successful Trakt pull · scrobbles skipped for want of provider ids ·
TMDB enriched/pending/not-found counts.

Failure states that are only visible in a log file are the flying-blind phase
repeating.

---

# 15. Phasing and gates

| Phase | Deliverables | Gate |
|---|---|---|
| **T0** | Register Trakt production app. Redirect URI = an `https://` URL you control (never `oob`). Confirm no secret is issued. | `POST /oauth/device/code` returns a `user_code` from curl |
| **T1** | `TraktAuthService`: device code, polling with every status mapped, PKCE, cancellation. No UI, no storage. | End-to-end authorize against the live endpoint; a declined authorization returns Denied, not an exception |
| **T2** | Credential model, DPAPI storage, `ClientIdUsedForToken` invalidation, refresh, revoke. | Kill mid-session, relaunch: still connected. Change the custom ID: resets to Disconnected, no 401 anywhere |
| **T3** | Connect dialog, connected state, Advanced expander. | Fresh profile connects with zero configuration; expiry shows Restart and works |
| **A0** | §3 artwork election: schema delta, aggregate on attach, recompute on delete/merge, render resolution order. | A group whose primary lacks a poster but whose sibling has one renders the sibling's, from the **sibling's** server URL |
| **T4** | Scrobble service, `trakt_queue`, drain worker, provisional skip. | Play → pause → resume → finish: exactly one Trakt history entry, correct progress. Kill connectivity mid-episode: playback unaffected, queue drains on reconnect |
| **T5** | `user_data.origin` + migration. D7 enforced in one place, not per call site. | Unit test: `origin='trakt'` produces no Trakt write; `origin='emby:A'` writes to B and Trakt but not A |
| **T6** | `last_activities` polling, watched pull, D8 timestamps, push on via `sibling_write_queue`. Additions only. | Mark watched on trakt.tv → watched in Nocturne and both servers within one cycle → **and no further Trakt traffic** (the loop test) |
| **T7** | `watchlist_entries`, Trakt watchlist pull, Emby favourites pull, rollup query. No cross-writes. | A title favourited on Valkyrie and on the Trakt watchlist is one row; neither peer is written to |
| **T8** | Watchlist page, nav item, text tiles, empty state. | Owned and unowned render together; no source indicators anywhere |
| **T9** | Add/remove affordances, optimistic toggle, both queues, `pending_op` filter. | Add from a poster offline → UI updates at once → both peers receive it on reconnect. Remove → no flicker-back when a pull lands mid-flight |
| **M0** | TMDB key (bundled + advanced), About page with logo and the required notice. | Attribution visible before any TMDB call ships |
| **M1** | `tmdb_metadata`, provider-id lookup only, `not_found`, field-level Emby-wins. | A group with any version's poster is never enriched; one with none gets a poster; a provisional group is skipped |
| **M2** | Enrichment pipeline on sync-complete: token bucket, 429 back-off, single-writer persistence, progress. | Cold-library pass completes; triggered only after sync completes; network loss mid-pass leaves the library usable and resumes |
| **M3** | Season-batched episode stills. | One `/season` call populates a whole season — 20 episodes, one request |

**Dependencies:** A0 before M1 (eligibility is defined by image election).
T5 before T6 (the origin rule must exist before anything pulls, or the loop
ships and is hard to unpick). T4 needs the dedup plan's Phase 2 (provider IDs)
and Phase 5 (playback events). M0–M3 are otherwise independent of all Trakt work.

---

# 16. Measure before building M2/M3

```sql
SELECT item_type,
       SUM(CASE WHEN image_tag IS NULL THEN 1 ELSE 0 END) AS missing,
       COUNT(*) AS total
  FROM dedup_groups
 GROUP BY item_type;
```

A few thousand missing movie/series groups makes M2 a twenty-minute background
pass and M3 possibly not worth building. Most of the episode layer missing makes
M3 the whole feature and the season batching the thing that makes it possible.
Run it after A0 lands — before that, `image_tag` reflects only the primary and
will overstate what's missing.
