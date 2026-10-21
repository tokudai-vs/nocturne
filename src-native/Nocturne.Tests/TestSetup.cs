using System.IO;
using System.Runtime.CompilerServices;
using Nocturne.Core.Utils;

namespace Nocturne.Tests;

// Runs before any test: logging and every AppPaths default (device.json, default DB and settings paths) go to a
// temp directory, never the user's %APPDATA%\Nocturne.
internal static class TestSetup
{
    internal static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "nocturne-tests");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(DataDirectory);
        Environment.SetEnvironmentVariable("NOCTURNE_DATA_DIR", DataDirectory);
        NocturneLogger.LogPath = Path.Combine(DataDirectory, "nocturne.log");
    }
}
