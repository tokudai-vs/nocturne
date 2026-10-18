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
    public SolidColorBrush PreviewBrush => new((Color)ColorConverter.ConvertFromString(HexColor));
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
            var color = (Color)ColorConverter.ConvertFromString(hexColor);

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

                    Application.Current.Resources["NocturneAmberBrush"] = brush;
                    Application.Current.Resources["NocturneAmberHoverBrush"] = hoverBrush;
                    Application.Current.Resources["NocturneAmberMutedBrush"] = mutedBrush;

                    try
                    {
                        ApplicationAccentColorManager.Apply(color);
                    }
                    catch { }
                });
            }

            _settings.Update(s => s.AccentColor = hexColor);
            ThemeColorChanged?.Invoke(hexColor);
            NocturneLogger.Info("ThemeColorService", $"Applied theme accent color: {hexColor}");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("ThemeColorService", $"Failed to apply theme color {hexColor}", ex);
        }
    }
}
