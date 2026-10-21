namespace Nocturne.Core.Utils;

public static class AppPaths
{
    // %APPDATA%\Nocturne, or NOCTURNE_DATA_DIR when set: redirects settings, database and logs as a unit so the
    // app can run against a copied profile. (GetFolderPath ignores an APPDATA override on Windows.)
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("NOCTURNE_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nocturne");
}
