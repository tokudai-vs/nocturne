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

-- Different well-formed ids of one provider among a group's items (their JSON, not the engine's keys).
SELECT 'groups with conflicting same-provider ids (items'' JSON)', COUNT(DISTINCT group_id) FROM (
  SELECT group_id FROM (
    SELECT group_id, type, 'tmdb' p, json_extract(json_data, '$.ProviderIds.Tmdb') v FROM cached_items WHERE group_id IS NOT NULL AND type IN ('Movie', 'Series', 'Episode')
    UNION ALL SELECT group_id, type, 'imdb', json_extract(json_data, '$.ProviderIds.Imdb') FROM cached_items WHERE group_id IS NOT NULL AND type IN ('Movie', 'Series', 'Episode')
    UNION ALL SELECT group_id, type, 'tvdb', json_extract(json_data, '$.ProviderIds.Tvdb') FROM cached_items WHERE group_id IS NOT NULL AND type IN ('Movie', 'Series', 'Episode'))
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
-- share its episode id; a wrong season/episode merge does not.
WITH spans AS (
  SELECT group_id FROM cached_items
  WHERE type = 'Episode' AND group_id IS NOT NULL
  GROUP BY group_id
  HAVING COUNT(DISTINCT COALESCE(parent_index_number, -1) || ':' || COALESCE(index_number, -1)) > 1),
ids AS (
  SELECT c.group_id,
    CASE WHEN COALESCE(json_extract(c.json_data, '$.ProviderIds.Tvdb'), json_extract(c.json_data, '$.ProviderIds.TheTVDB')) GLOB '[0-9]*'
          AND COALESCE(json_extract(c.json_data, '$.ProviderIds.Tvdb'), json_extract(c.json_data, '$.ProviderIds.TheTVDB')) NOT GLOB '*[^0-9]*'
         THEN COALESCE(json_extract(c.json_data, '$.ProviderIds.Tvdb'), json_extract(c.json_data, '$.ProviderIds.TheTVDB')) END tvdb,
    CASE WHEN c.tmdb_id GLOB '[0-9]*' AND c.tmdb_id NOT GLOB '*[^0-9]*' THEN c.tmdb_id END tmdb,
    CASE WHEN c.imdb_id GLOB 'tt[0-9]*' AND substr(c.imdb_id, 3) NOT GLOB '*[^0-9]*' THEN c.imdb_id END imdb
  FROM cached_items c JOIN spans USING (group_id)),
per AS (
  SELECT group_id, COUNT(*) n,
    COUNT(tvdb) n_tvdb, COUNT(DISTINCT tvdb) d_tvdb,
    COUNT(tmdb) n_tmdb, COUNT(DISTINCT tmdb) d_tmdb,
    COUNT(imdb) n_imdb, COUNT(DISTINCT imdb) d_imdb
  FROM ids GROUP BY group_id)
SELECT 'episode groups spanning > 1 (season, episode)', COUNT(*) FROM per
UNION ALL
SELECT '[gate] ... whose items do not all share one episode id', COUNT(*) FROM per
 WHERE NOT ((n_tvdb = n AND d_tvdb = 1) OR (n_tmdb = n AND d_tmdb = 1) OR (n_imdb = n AND d_imdb = 1));

SELECT 'ep:tvdb/tmdb/imdb keys', COUNT(*) FROM dedup_keys WHERE key GLOB 'ep:tvdb:*' OR key GLOB 'ep:tmdb:*' OR key GLOB 'ep:imdb:*';
SELECT 'demoted (suspect) provider keys', COUNT(*) FROM dedup_keys WHERE is_authoritative = 0 AND key NOT GLOB '*@*' AND (key GLOB 'tmdb:*' OR key GLOB 'imdb:*' OR key GLOB 'tvdb:*' OR key GLOB 'ep:tvdb:*' OR key GLOB 'ep:tmdb:*' OR key GLOB 'ep:imdb:*');
SELECT 'evidence keys ({key}@{group})', COUNT(*) FROM dedup_keys WHERE key GLOB '*@*';
SELECT 'Movie/Series/Episode items without a group', COUNT(*) FROM cached_items WHERE type IN ('Movie', 'Series', 'Episode') AND group_id IS NULL;
SELECT 'movies with production_year', COUNT(*) FROM cached_items WHERE type = 'Movie' AND production_year IS NOT NULL;
SELECT 'cascade gate as last measured by the app', key, value FROM migration_state WHERE key IN ('dedup_engine', 'dedup_unshared_renumbered_groups');
