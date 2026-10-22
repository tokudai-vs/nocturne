-- Standing dedup baseline. Run read-only against a v4 database (WAL readers need the -wal file beside it):
--   sqlite3 -readonly nocturne-v4.db < scripts/dedup-baseline.sql
-- Rows marked [gate] must be 0: the sibling cascade (FeatureFlags.SiblingCascade) measures the same condition
-- after every sync and rebuild and stays off while it is not.
.mode list
.separator " | "

SELECT 'groups', COUNT(*) FROM dedup_groups;
SELECT 'multi-version groups', COUNT(*) FROM (SELECT group_id FROM cached_items WHERE group_id IS NOT NULL GROUP BY group_id HAVING COUNT(*) >= 2);
SELECT 'items in multi-version groups', SUM(c) FROM (SELECT COUNT(*) c FROM cached_items WHERE group_id IS NOT NULL GROUP BY group_id HAVING COUNT(*) >= 2);
SELECT 'groups spanning both servers', COUNT(*) FROM (SELECT group_id FROM cached_items WHERE group_id IS NOT NULL GROUP BY group_id HAVING COUNT(DISTINCT server_id) >= 2);
SELECT 'largest group (items)', MAX(c) FROM (SELECT COUNT(*) c FROM cached_items WHERE group_id IS NOT NULL GROUP BY group_id);
SELECT 'groups > 8 items', COUNT(*) FROM (SELECT group_id FROM cached_items WHERE group_id IS NOT NULL GROUP BY group_id HAVING COUNT(*) > 8);

-- Different well-formed ids of one provider among a group's items (their JSON, not the engine's keys; provider
-- names in any case, "Imdb" and "IMDB" both occur).
SELECT 'groups with conflicting same-provider ids (items'' JSON)', COUNT(DISTINCT group_id) FROM (
  SELECT group_id FROM (
    SELECT c.group_id, CASE lower(j.key) WHEN 'themoviedb' THEN 'tmdb' WHEN 'thetvdb' THEN 'tvdb' ELSE lower(j.key) END p, j.value v
    FROM cached_items c, json_each(c.json_data, '$.ProviderIds') j
    WHERE c.group_id IS NOT NULL AND c.type IN ('Movie', 'Series', 'Episode')
      AND lower(j.key) IN ('tmdb', 'themoviedb', 'imdb', 'tvdb', 'thetvdb'))
  WHERE v IS NOT NULL AND v != '' AND ((p = 'imdb' AND v GLOB 'tt[0-9]*' AND substr(v, 3) NOT GLOB '*[^0-9]*') OR (p != 'imdb' AND v NOT GLOB '*[^0-9]*'))
  GROUP BY group_id, p HAVING COUNT(DISTINCT v) >= 2);
SELECT 'movie groups with >= 2 distinct tmdb_id', COUNT(*) FROM (SELECT group_id FROM cached_items WHERE type = 'Movie' AND group_id IS NOT NULL AND tmdb_id IS NOT NULL AND tmdb_id != '' GROUP BY group_id HAVING COUNT(DISTINCT tmdb_id) >= 2);

-- The engine's own view: >= 2 authoritative ids of one key type in a group. Season/episode keys
-- (ep:tvdb:{series}:sXXeYY) count by series, as the conflict guard compares them.
SELECT 'groups with >= 2 authoritative ids of one key type', COUNT(*) FROM (SELECT DISTINCT group_id FROM (
  SELECT group_id, t, CASE WHEN t GLOB 'ep:*' THEN substr(v, 1, instr(v, ':s') - 1) ELSE v END sv FROM (
    SELECT group_id, t, substr(key, length(t) + 2) v FROM (
      SELECT group_id, key, substr(key, 1, instr(key, ':') + instr(substr(key, instr(key, ':') + 1), ':') - 1) t
      FROM dedup_keys WHERE is_authoritative = 1)))
  GROUP BY group_id, t HAVING COUNT(DISTINCT sv) >= 2);
SELECT 'groups holding > 1 tvdb:ep key', COUNT(*) FROM (SELECT group_id FROM dedup_keys WHERE key GLOB 'tvdb:ep:*' GROUP BY group_id HAVING COUNT(*) > 1);

-- Episode groups whose items span more than one (season, episode). Servers numbering one episode differently
-- share its id; a wrong season/episode merge does not.
WITH spans AS (
  SELECT group_id FROM cached_items
  WHERE type = 'Episode' AND group_id IS NOT NULL
  GROUP BY group_id
  HAVING COUNT(DISTINCT COALESCE(parent_index_number, -1) || ':' || COALESCE(index_number, -1)) > 1),
raw AS (
  SELECT c.group_id g, c.server_id srv, c.id, COALESCE(c.parent_index_number, -1) s, COALESCE(c.index_number, -1) e,
    (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) IN ('tvdb', 'thetvdb') AND value != '' LIMIT 1) tvdb,
    (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) IN ('tmdb', 'themoviedb') AND value != '' LIMIT 1) tmdb,
    (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) = 'imdb' AND value != '' LIMIT 1) imdb
  FROM cached_items c JOIN spans ON spans.group_id = c.group_id),
ep AS (
  SELECT g, srv, id, s, e,
    CASE WHEN tvdb GLOB '[0-9]*' AND tvdb NOT GLOB '*[^0-9]*' THEN tvdb END tvdb,
    CASE WHEN tmdb GLOB '[0-9]*' AND tmdb NOT GLOB '*[^0-9]*' THEN tmdb END tmdb,
    CASE WHEN imdb GLOB 'tt[0-9]*' AND substr(imdb, 3) NOT GLOB '*[^0-9]*' THEN imdb END imdb
  FROM raw),
per AS (
  SELECT g, COUNT(*) n, COUNT(tvdb) n_tvdb, COUNT(DISTINCT tvdb) d_tvdb, COUNT(tmdb) n_tmdb,
         COUNT(DISTINCT tmdb) d_tmdb, COUNT(imdb) n_imdb, COUNT(DISTINCT imdb) d_imdb
  FROM ep GROUP BY g)
SELECT 'episode groups spanning > 1 (season, episode)', COUNT(*) FROM per
UNION ALL
SELECT '... whose items do not all share one episode id', COUNT(*) FROM per
 WHERE NOT ((n_tvdb = n AND d_tvdb = 1) OR (n_tmdb = n AND d_tmdb = 1) OR (n_imdb = n AND d_imdb = 1))
UNION ALL
-- One server holding two items under different numbering that share no episode id: the server's own numbering
-- contradicts the merge. The first count also includes a legitimate untagged copy of an episode another server
-- keeps under several numberings.
SELECT '[gate] ... where one server holds two items under different numbering sharing no episode id', COUNT(DISTINCT a.g)
  FROM ep a JOIN ep b ON b.g = a.g AND b.srv = a.srv AND a.id < b.id AND (a.s != b.s OR a.e != b.e)
 WHERE NOT ((a.tvdb = b.tvdb OR a.tmdb = b.tmdb OR a.imdb = b.imdb) IS TRUE);

-- An untagged episode grouped with another server's tagged episode while its own server holds that episode id
-- only under a different numbering (in any group): the same contradiction, where the gate's pair is split apart.
CREATE TEMP TABLE baseline_ep AS
  SELECT group_id g, server_id srv, parent_index_number s, index_number e,
         (SELECT value FROM json_each(json_data, '$.ProviderIds') WHERE lower(key) IN ('tvdb', 'thetvdb') AND value != '' LIMIT 1) tvdb
  FROM cached_items WHERE type = 'Episode' AND group_id IS NOT NULL;
CREATE INDEX temp.baseline_ep_g ON baseline_ep(g);
CREATE INDEX temp.baseline_ep_t ON baseline_ep(srv, tvdb);
SELECT 'untagged episodes contradicted by their own server''s numbering (groups)', COUNT(DISTINCT u.g) FROM baseline_ep u
  JOIN baseline_ep t ON t.g = u.g AND t.srv != u.srv AND t.tvdb IS NOT NULL
 WHERE u.tvdb IS NULL
   AND EXISTS (SELECT 1 FROM baseline_ep o WHERE o.srv = u.srv AND o.tvdb = t.tvdb AND (o.s IS NOT u.s OR o.e IS NOT u.e))
   AND NOT EXISTS (SELECT 1 FROM baseline_ep o WHERE o.srv = u.srv AND o.tvdb = t.tvdb AND o.s IS u.s AND o.e IS u.e);

SELECT 'ep:tvdb/tmdb/imdb keys', COUNT(*) FROM dedup_keys WHERE key GLOB 'ep:tvdb:*' OR key GLOB 'ep:tmdb:*' OR key GLOB 'ep:imdb:*';
SELECT 'demoted (suspect) provider keys', COUNT(*) FROM dedup_keys WHERE is_authoritative = 0 AND key NOT GLOB '*@*' AND (key GLOB 'tmdb:*' OR key GLOB 'imdb:*' OR key GLOB 'tvdb:*' OR key GLOB 'ep:tvdb:*' OR key GLOB 'ep:tmdb:*' OR key GLOB 'ep:imdb:*');
SELECT 'evidence keys ({key}@{group})', COUNT(*) FROM dedup_keys WHERE key GLOB '*@*';
SELECT 'Movie/Series/Episode items without a group', COUNT(*) FROM cached_items WHERE type IN ('Movie', 'Series', 'Episode') AND group_id IS NULL;
SELECT 'movies with production_year', COUNT(*) FROM cached_items WHERE type = 'Movie' AND production_year IS NOT NULL;
SELECT 'cascade gate as last measured by the app', key, value FROM migration_state WHERE key IN ('dedup_engine', 'dedup_unshared_renumbered_groups');
