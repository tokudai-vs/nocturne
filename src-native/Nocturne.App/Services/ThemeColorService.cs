using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.Services;

public class ThemeColorUiOption
{
    public string Name { get; set; } = string.Empty;
    public string HexColor { get; set; } = string.Empty;

    public SolidColorBrush PreviewBrush
    {
        get
        {
            if (string.Equals(HexColor, "System", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sysColor = ApplicationAccentColorManager.GetColorizationColor();
                    if (sysColor.A > 0)
                    {
                        var b = new SolidColorBrush(sysColor);
                        b.Freeze();
                        return b;
                    }
                }
                catch { }
                var defBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4"));
                defBrush.Freeze();
                return defBrush;
            }

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(HexColor));
                brush.Freeze();
                return brush;
            }
            catch
            {
                var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5A00D"));
                fallback.Freeze();
                return fallback;
            }
        }
    }
}

public class ThemeColorService
{
    private readonly SettingsService _settings;

    public static readonly List<ThemeColorUiOption> AvailableColors =
        ThemePalette.AvailableColors
            .Select(c => new ThemeColorUiOption { Name = c.Name, HexColor = c.HexColor })
            .ToList();

    public event Action<string>? ThemeColorChanged;

    public ThemeColorService(SettingsService settings)
    {
        _settings = settings;
    }

    public void ApplyColor(string hexColor)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hexColor)) hexColor = "#E5A00D";
            bool isSystem = string.Equals(hexColor, "System", StringComparison.OrdinalIgnoreCase);

            Color color;
            if (isSystem)
            {
                try
                {
                    color = ApplicationAccentColorManager.GetColorizationColor();
                    if (color.A == 0)
                    {
                        color = (Color)ColorConverter.ConvertFromString("#0078D4");
                    }
                }
                catch
                {
                    color = (Color)ColorConverter.ConvertFromString("#0078D4");
                }
            }
            else
            {
                color = (Color)ColorConverter.ConvertFromString(hexColor);
            }

            // Compute hover (lighter) and muted (darker) variants
            var hoverColor = Color.FromRgb(
                (byte)Math.Min(255, color.R + 25),
                (byte)Math.Min(255, color.G + 25),
                (byte)Math.Min(255, color.B + 25));

            var mutedColor = Color.FromRgb(
                (byte)Math.Max(0, color.R - 40),
                (byte)Math.Max(0, color.G - 40),
                (byte)Math.Max(0, color.B - 40));

            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Resources["NocturneAmberColor"] = color;
                    Application.Current.Resources["NocturneAmberHoverColor"] = hoverColor;
                    Application.Current.Resources["NocturneAmberMutedColor"] = mutedColor;

                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    var hoverBrush = new SolidColorBrush(hoverColor);
                    hoverBrush.Freeze();
                    var mutedBrush = new SolidColorBrush(mutedColor);
                    mutedBrush.Freeze();
                    var tintColor = Color.FromArgb(0x33, color.R, color.G, color.B);
                    var tintBrush = new SolidColorBrush(tintColor);
                    tintBrush.Freeze();

                    Application.Current.Resources["NocturneAmberBrush"] = brush;
                    Application.Current.Resources["NocturneAmberHoverBrush"] = hoverBrush;
                    Application.Current.Resources["NocturneAmberMutedBrush"] = mutedBrush;
                    Application.Current.Resources["NocturneAmberTintBrush"] = tintBrush;

                    // Standard System and Wpf.Ui Accent Brushes
                    Application.Current.Resources["SystemAccentColor"] = color;
                    Application.Current.Resources["SystemAccentColorPrimary"] = color;
                    Application.Current.Resources["SystemAccentColorLight1"] = hoverColor;
                    Application.Current.Resources["SystemAccentColorLight2"] = hoverColor;
                    Application.Current.Resources["SystemAccentColorLight3"] = hoverColor;
                    Application.Current.Resources["SystemAccentColorDark1"] = mutedColor;
                    Application.Current.Resources["SystemAccentColorDark2"] = mutedColor;
                    Application.Current.Resources["SystemAccentColorDark3"] = mutedColor;

                    Application.Current.Resources["SystemAccentColorPrimaryBrush"] = brush;
                    Application.Current.Resources["AccentFillColorDefaultBrush"] = brush;
                    Application.Current.Resources["AccentFillColorSecondaryBrush"] = hoverBrush;
                    Application.Current.Resources["AccentFillColorTertiaryBrush"] = mutedBrush;
                    Application.Current.Resources["AccentTextFillColorPrimaryBrush"] = brush;

                    try
                    {
                        if (isSystem)
                        {
                            ApplicationAccentColorManager.ApplySystemAccent();
                        }
                        else
                        {
                            ApplicationAccentColorManager.Apply(color);
                        }
                    }
                    catch { }
                });
            }

            _settings.Update(s => s.AccentColor = hexColor);
            ThemeColorChanged?.Invoke(hexColor);
            NocturneLogger.Info("ThemeColorService", $"Applied theme accent color: {hexColor} (resolved: #{color.R:X2}{color.G:X2}{color.B:X2})");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("ThemeColorService", $"Failed to apply theme color {hexColor}", ex);
        }
    }

    public void RefreshSystemAccentIfNeeded()
    {
        if (string.Equals(_settings.Current.AccentColor, "System", StringComparison.OrdinalIgnoreCase))
        {
            ApplyColor("System");
        }
    }
}
