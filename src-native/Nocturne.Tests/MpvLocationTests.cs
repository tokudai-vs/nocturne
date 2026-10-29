using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #40: mpv is looked for beside the exe, or at the root of the repo a build output sits in, found from the exe's own
// path (src-native\Nocturne.App\bin\...). Never in any other folder above the exe, where another user could plant one.
public class MpvLocationTests
{
    [Fact]
    public void OutsideNocturneApp_NothingAboveTheExeIsSearched()
    {
        // These tests run from src-native\Nocturne.Tests\bin\...: not under Nocturne.App, so the repo root's
        // resources\mpv (present on a dev machine) must not be found by walking up.
        Assert.Null(MpvController.FindMpvExecutable());
    }
}
