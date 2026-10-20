using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Tests;

public class ThemeColorServiceTests
{
    [Fact]
    public void AvailableColors_ContainsSignatureAmber()
    {
        var colors = ThemePalette.AvailableColors;
        Assert.NotEmpty(colors);

        var amber = colors.FirstOrDefault(c => c.Name == "Signature Amber");
        Assert.NotNull(amber);
        Assert.Equal("#E5A00D", amber.HexColor);
    }

    [Fact]
    public void AvailableColors_ContainsWindowsAccent()
    {
        var colors = ThemePalette.AvailableColors;
        var winAccent = colors.FirstOrDefault(c => c.Name == "Windows Accent Color");
        Assert.NotNull(winAccent);
        Assert.Equal("System", winAccent.HexColor);
    }

    [Fact]
    public void AvailableColors_AllHexCodesAreValid()
    {
        var colors = ThemePalette.AvailableColors;
        foreach (var c in colors)
        {
            if (string.Equals(c.HexColor, "System", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.StartsWith("#", c.HexColor);
            Assert.Equal(7, c.HexColor.Length);
            Assert.True(int.TryParse(c.HexColor[1..], System.Globalization.NumberStyles.HexNumber, null, out _));
        }
    }
}
