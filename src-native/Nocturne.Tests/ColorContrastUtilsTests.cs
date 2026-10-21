using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

public class ColorContrastUtilsTests
{
    [Theory]
    [InlineData("#E5A00D", "#111111")] // Signature Amber -> Dark text
    [InlineData("#FF8C00", "#111111")] // Sunset Orange -> Dark text
    [InlineData("#00B7C3", "#111111")] // Cyber Cyan -> Dark text
    [InlineData("#FFFFFF", "#111111")] // Pure White -> Dark text
    [InlineData("#0078D4", "#FFFFFF")] // Electric Blue -> Light text
    [InlineData("#8A2BE2", "#FFFFFF")] // Ultraviolet -> Light text
    [InlineData("#107C41", "#FFFFFF")] // Emerald Green -> Light text
    [InlineData("#E81123", "#FFFFFF")] // Crimson Rose -> Light text
    [InlineData("#934B28", "#FFFFFF")] // Copper / Rust (screenshot Windows accent) -> Light text
    [InlineData("#000000", "#FFFFFF")] // Pure Black -> Light text
    public void GetContrastingForeground_ReturnsCorrectContrastColor(string bgHex, string expectedFgHex)
    {
        var bg = RgbColor.FromHex(bgHex);
        var expectedFg = RgbColor.FromHex(expectedFgHex);
        var actualFg = ColorContrastUtils.GetContrastingForeground(bg);

        Assert.Equal(expectedFg, actualFg);
    }

    [Fact]
    public void CalculateContrastRatio_BlackAndWhite_Returns21()
    {
        var black = new RgbColor(0, 0, 0);
        var white = new RgbColor(255, 255, 255);
        double cr = ColorContrastUtils.CalculateContrastRatio(black, white);

        Assert.True(cr >= 20.9 && cr <= 21.1);
    }

    [Fact]
    public void CalculateContrastRatio_SameColor_Returns1()
    {
        var color = new RgbColor(128, 64, 32);
        double cr = ColorContrastUtils.CalculateContrastRatio(color, color);

        Assert.Equal(1.0, cr, precision: 2);
    }

    [Fact]
    public void GetAccessibleTextVariant_InLightTheme_EnsuresWcagAaContrast()
    {
        // Signature Amber (#E5A00D) is too light on white (~2.28:1)
        var amber = RgbColor.FromHex("#E5A00D");
        var accessibleAmber = ColorContrastUtils.GetAccessibleTextVariant(amber, isLightTheme: true);
        var white = new RgbColor(255, 255, 255);

        double cr = ColorContrastUtils.CalculateContrastRatio(accessibleAmber, white);
        Assert.True(cr >= 4.5, $"Contrast ratio {cr:F2} is below 4.5:1");
    }

    [Fact]
    public void GetAccessibleTextVariant_InDarkTheme_EnsuresWcagAaContrast()
    {
        // Deep Navy (#001A40) is too dark on dark background
        var darkNavy = RgbColor.FromHex("#001A40");
        var accessibleNavy = ColorContrastUtils.GetAccessibleTextVariant(darkNavy, isLightTheme: false);
        var darkBg = new RgbColor(0x18, 0x18, 0x18);

        double cr = ColorContrastUtils.CalculateContrastRatio(accessibleNavy, darkBg);
        Assert.True(cr >= 4.5, $"Contrast ratio {cr:F2} is below 4.5:1");
    }

    [Fact]
    public void GetAccessibleTextVariant_AlreadyHighContrast_RemainsUnchanged()
    {
        // Dark copper (#934B28) on white already has > 4.5:1 contrast
        var copper = RgbColor.FromHex("#934B28");
        var accessibleCopper = ColorContrastUtils.GetAccessibleTextVariant(copper, isLightTheme: true);

        Assert.Equal(copper, accessibleCopper);
    }

    [Fact]
    public void HslRoundtripConversion_PreservesRgbValues()
    {
        var original = new RgbColor(200, 100, 50);
        var (h, s, l) = ColorContrastUtils.RgbToHsl(original);
        var roundtrip = ColorContrastUtils.HslToRgb(h, s, l);

        Assert.Equal(original.R, roundtrip.R);
        Assert.Equal(original.G, roundtrip.G);
        Assert.Equal(original.B, roundtrip.B);
    }
}
