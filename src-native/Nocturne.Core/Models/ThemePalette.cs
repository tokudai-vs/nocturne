namespace Nocturne.Core.Models;

public class ThemeColorOption
{
    public string Name { get; set; } = string.Empty;
    public string HexColor { get; set; } = string.Empty;
}

public static class ThemePalette
{
    public static readonly List<ThemeColorOption> AvailableColors = new()
    {
        new() { Name = "Windows Accent Color", HexColor = "System" },
        new() { Name = "Signature Amber", HexColor = "#E5A00D" },
        new() { Name = "Electric Blue", HexColor = "#0078D4" },
        new() { Name = "Ultraviolet", HexColor = "#8A2BE2" },
        new() { Name = "Emerald Green", HexColor = "#107C41" },
        new() { Name = "Crimson Rose", HexColor = "#E81123" },
        new() { Name = "Sunset Orange", HexColor = "#FF8C00" },
        new() { Name = "Cyber Cyan", HexColor = "#00B7C3" }
    };
}
