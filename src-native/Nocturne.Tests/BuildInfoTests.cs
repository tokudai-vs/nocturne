using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// Audit open question 12: every build names its commit, so a stale binary is evident from its first log line.
public class BuildInfoTests
{
    [Fact]
    public void TheBuildCarriesItsCommit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{40}$", BuildInfo.InformationalVersion); // Source Link's stamp
        Assert.Matches(@"^Nocturne \d+\.\d+\.\d+ \(commit [0-9a-f]{7}", BuildInfo.Describe);
    }
}
