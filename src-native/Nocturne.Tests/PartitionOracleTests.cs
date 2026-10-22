using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #20 item 2: the Phase 3 partition-identity oracle.
public class PartitionOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");

    public PartitionOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Db(string name, params (string Id, string Server, string? Group)[] items)
    {
        var path = Path.Combine(_dir, name);
        using var db = new DatabaseService(path);
        foreach (var (id, srv, group) in items)
            db.UpsertItem(new EmbyItem { Id = id, ServerId = srv, Name = id, Type = "Movie", DedupGroupId = group }, srv);
        return path;
    }

    [Fact]
    public void Hash_IgnoresMemberOrder_AndSeparatesServers()
    {
        Assert.Equal(PartitionOracle.HashOf(new[] { ("s1", "a"), ("s2", "b") }, ("s1", "a")), PartitionOracle.HashOf(new[] { ("s2", "b"), ("s1", "a") }, ("s1", "a")));
        Assert.NotEqual(PartitionOracle.HashOf(new[] { ("s1", "a") }, null), PartitionOracle.HashOf(new[] { ("s2", "a") }, null));
        Assert.NotEqual(PartitionOracle.HashOf(new[] { ("s1", "a"), ("s2", "b") }, ("s1", "a")), PartitionOracle.HashOf(new[] { ("s1", "a"), ("s2", "b") }, ("s2", "b")));
    }

    [Fact]
    public void SamePartition_UnderDifferentGroupIds_IsIdentical()
    {
        var a = Db("a.db", ("x", "s1", "1"), ("y", "s2", "1"), ("z", "s1", "2"));
        var b = Db("b.db", ("x", "s1", "7"), ("y", "s2", "7"), ("z", "s1", "3"));

        Assert.True(PartitionOracle.Compare(PartitionOracle.Read(a), PartitionOracle.Read(b)).Identical);
    }

    [Fact]
    public void Divergence_ReportsTheGroupsOnEachSide()
    {
        var a = Db("a.db", ("x", "s1", "1"), ("y", "s2", "1"), ("z", "s1", "2"));
        var b = Db("b.db", ("x", "s1", "1"), ("y", "s2", "2"), ("z", "s1", "2"), ("w", "s2", null));

        var d = PartitionOracle.Compare(PartitionOracle.Read(a), PartitionOracle.Read(b));

        Assert.Equal(new[] { "x+y", "z" }, d.OnlyInA.Select(Ids).OrderBy(x => x));
        Assert.Equal(new[] { "w", "x", "y+z" }, d.OnlyInB.Select(Ids).OrderBy(x => x)); // w: ungrouped, a group of one
    }

    // #22 item 3: Settings' rebuild passed no server priority, so it elected other primaries (the card, its poster and
    // title) than a sync, with the grouping unchanged; the oracle compared members only and could not see it.
    [Fact]
    public void ARebuildWithoutTheServerPriority_ElectsOtherPrimaries_AndTheOracleSeesIt()
    {
        string Rebuilt(string name, IList<string>? priority)
        {
            var path = Path.Combine(_dir, name);
            using var db = new DatabaseService(path);
            foreach (var srv in new[] { "s1", "s2" })
                db.UpsertItem(new EmbyItem { Id = "m-" + srv, ServerId = srv, Name = "Arrival", Type = "Movie", ProviderIds = new() { ["Tmdb"] = "329865" } }, srv);
            new DedupEngine().RebuildIndex(db, priority);
            return path;
        }
        var without = PartitionOracle.Read(Rebuilt("a.db", null));
        var with = PartitionOracle.Read(Rebuilt("b.db", new List<string> { "s2", "s1" }));

        Assert.Equal(Ids(Assert.Single(without)), Ids(Assert.Single(with)));      // same grouping
        Assert.Equal(("s1", "m-s1"), without[0].Primary);                       // first come
        Assert.Equal(("s2", "m-s2"), with[0].Primary);                          // the preferred server's copy
        Assert.False(PartitionOracle.Compare(without, with).Identical);
    }

    private static string Ids(PartitionOracle.Group g) => string.Join("+", g.Members.Select(m => m.ItemId).OrderBy(x => x));
}
