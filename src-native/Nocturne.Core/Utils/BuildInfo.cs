using System.Reflection;

namespace Nocturne.Core.Utils;

// Which build is running (audit open question 12): a build from 10-22 run on 10-27 caused the 5,645 drift and took a
// day to identify. The SDK stamps the commit into the informational version ("4.2.0+<sha>", read from .git by Source
// Link); Release builds also carry their build time, which tells a build with uncommitted changes from a clean one.
public static class BuildInfo
{
    private static readonly Assembly Stamped = Assembly.GetEntryAssembly() is { } entry && entry.GetName().Name?.StartsWith("Nocturne") == true
        ? entry : typeof(BuildInfo).Assembly;

    public static string InformationalVersion =>
        Stamped.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string Version => InformationalVersion.Split('+')[0];
    public static string? Commit => InformationalVersion.Split('+') is [_, var sha, ..] ? sha : null;
    public static string? ShortCommit => Commit is { Length: >= 7 } c ? c[..7] : Commit;
    public static string? BuiltUtc => Stamped.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value;

    // "Nocturne 4.2.0 (commit 31a2cac, built 2026-10-28 01:41 UTC)"
    public static string Describe =>
        $"Nocturne {Version} (commit {ShortCommit ?? "unknown"}{(BuiltUtc is { } built ? $", built {built} UTC" : "")})";
}
