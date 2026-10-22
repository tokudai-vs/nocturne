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
        Assert.Equal(PartitionOracle.HashOf(new[] { ("s1", "a"), ("s2", "b") }), PartitionOracle.HashOf(new[] { ("s2", "b"), ("s1", "a") }));
        Assert.NotEqual(PartitionOracle.HashOf(new[] { ("s1", "a") }), PartitionOracle.HashOf(new[] { ("s2", "a") }));
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

    private static string Ids(PartitionOracle.Group g) => string.Join("+", g.Members.Select(m => m.ItemId).OrderBy(x => x));
}
