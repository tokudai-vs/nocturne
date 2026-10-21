namespace Nocturne.Core.Utils;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new RgbColor(229, 160, 13);
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new RgbColor(r, g, b);
        }
        return new RgbColor(229, 160, 13);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

public static class ColorContrastUtils
{
    public static readonly RgbColor DarkText = new(0x11, 0x11, 0x11);
    public static readonly RgbColor LightText = new(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Calculates relative luminance according to WCAG 2.1 specifications (0.0 to 1.0).
    /// </summary>
    public static double CalculateLuminance(RgbColor color)
    {
        double r = Linearize(color.R / 255.0);
        double g = Linearize(color.G / 255.0);
        double b = Linearize(color.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double Linearize(double channel)
    {
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Calculates contrast ratio between two colors according to WCAG 2.1 (1.0 to 21.0).
    /// </summary>
    public static double CalculateContrastRatio(RgbColor color1, RgbColor color2)
    {
        double l1 = CalculateLuminance(color1);
        double l2 = CalculateLuminance(color2);
        double brighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (brighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Returns either DarkText (#111111) or LightText (#FFFFFF) to maximize contrast against the given background.
    /// Directly evaluates WCAG 2.1 contrast ratios and chooses the option with strictly higher contrast.
    /// </summary>
    public static RgbColor GetContrastingForeground(RgbColor background)
    {
        double whiteContrast = CalculateContrastRatio(background, LightText);
        double darkContrast = CalculateContrastRatio(background, DarkText);
        return whiteContrast >= darkContrast ? LightText : DarkText;
    }

    /// <summary>
    /// Converts RGB color to HSL (Hue in [0, 1], Saturation in [0, 1], Lightness in [0, 1]).
    /// </summary>
    public static (double H, double S, double L) RgbToHsl(RgbColor rgb)
    {
        double r = rgb.R / 255.0;
        double g = rgb.G / 255.0;
        double b = rgb.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0.0;
        double s = 0.0;
        double l = (max + min) / 2.0;

        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)
                h = ((g - b) / d) + (g < b ? 6.0 : 0.0);
            else if (max == g)
                h = ((b - r) / d) + 2.0;
            else
                h = ((r - g) / d) + 4.0;
            h /= 6.0;
        }

        return (h, s, l);
    }

    /// <summary>
    /// Converts HSL color to RGB.
    /// </summary>
    public static RgbColor HslToRgb(double h, double s, double l)
    {
        double r, g, b;

        if (s == 0.0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1.0 + s) : l + s - (l * s);
            double p = (2.0 * l) - q;
            r = HueToRgb(p, q, h + (1.0 / 3.0));
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - (1.0 / 3.0));
        }

        return new RgbColor(
            (byte)Math.Clamp(Math.Round(r * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(g * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(b * 255.0), 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0 / 6.0) return p + ((q - p) * 6.0 * t);
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
        return p;
    }

    /// <summary>
    /// Returns an accessible variant of the accent color for rendering as text directly against the theme background,
    /// guaranteeing at least 4.5:1 contrast ratio (WCAG AA) while preserving the accent's hue and saturation.
    /// </summary>
    public static RgbColor GetAccessibleTextVariant(RgbColor accent, bool isLightTheme)
    {
        var targetBg = isLightTheme ? new RgbColor(0xFF, 0xFF, 0xFF) : new RgbColor(0x18, 0x18, 0x18);
        if (CalculateContrastRatio(accent, targetBg) >= 4.5)
        {
            return accent;
        }

        var (h, s, l) = RgbToHsl(accent);
        if (isLightTheme)
        {
            // Darken lightness until contrast reaches 4.5:1
            while (l > 0.05 && CalculateContrastRatio(HslToRgb(h, s, l), targetBg) < 4.5)
            {
                l -= 0.02;
            }
        }
        else
        {
            // Lighten lightness until contrast reaches 4.5:1
            while (l < 0.95 && CalculateContrastRatio(HslToRgb(h, s, l), targetBg) < 4.5)
            {
                l += 0.02;
            }
        }

        return HslToRgb(h, s, l);
    }
}
