using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Nocturne.Core.Services;

// The dedup plan's Phase 3 gate: the partition a sync produces must equal the one a rebuild of the same items
// produces. A group is identified by SHA-256 over its sorted (server_id, item_id) members, and two partitions are
// compared as multisets of those hashes, so group ids (which differ between runs) do not matter. An item without a
// group counts as a group of one.
public static class PartitionOracle
{
    public sealed record Group(string Hash, int? GroupId, IReadOnlyList<(string ServerId, string ItemId)> Members);

    public sealed record Divergence(IReadOnlyList<Group> OnlyInA, IReadOnlyList<Group> OnlyInB)
    {
        public bool Identical => OnlyInA.Count == 0 && OnlyInB.Count == 0;
    }

    public static string HashOf(IEnumerable<(string ServerId, string ItemId)> members)
    {
        var text = new StringBuilder();
        foreach (var (srv, id) in members.OrderBy(m => m.ServerId, StringComparer.Ordinal).ThenBy(m => m.ItemId, StringComparer.Ordinal))
            text.Append(srv).Append('\u001f').Append(id).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // The Movie/Series/Episode partition of a database file, read on its own read-only connection.
    public static List<Group> Read(string dbPath)
    {
        var byGroup = new Dictionary<int, List<(string, string)>>();
        var singles = new List<Group>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id, server_id, id FROM cached_items WHERE type IN ('Movie', 'Series', 'Episode');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var member = (reader.GetString(1), reader.GetString(2));
            if (reader.IsDBNull(0)) { singles.Add(new Group(HashOf(new[] { member }), null, new[] { member })); continue; }
            var gid = reader.GetInt32(0);
            if (!byGroup.TryGetValue(gid, out var list)) byGroup[gid] = list = new List<(string, string)>();
            list.Add(member);
        }
        singles.AddRange(byGroup.Select(kv => new Group(HashOf(kv.Value), kv.Key, kv.Value)));
        return singles;
    }

    // Groups whose exact membership appears more often in one partition than in the other.
    public static Divergence Compare(IReadOnlyList<Group> a, IReadOnlyList<Group> b)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in a) counts[g.Hash] = counts.GetValueOrDefault(g.Hash) + 1;
        foreach (var g in b) counts[g.Hash] = counts.GetValueOrDefault(g.Hash) - 1;
        List<Group> Surplus(IReadOnlyList<Group> side, int sign)
        {
            var left = counts.Where(kv => kv.Value * sign > 0).ToDictionary(kv => kv.Key, kv => kv.Value * sign, StringComparer.Ordinal);
            var result = new List<Group>();
            foreach (var g in side)
                if (left.TryGetValue(g.Hash, out var n) && n > 0) { result.Add(g); left[g.Hash] = n - 1; }
            return result;
        }
        return new Divergence(Surplus(a, 1), Surplus(b, -1));
    }
}
