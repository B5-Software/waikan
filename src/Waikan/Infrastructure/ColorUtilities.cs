using System.Globalization;
using System.Windows.Media;

namespace Waikan.Infrastructure;

public static class ColorUtilities
{
    public static bool IsValidHexColor(string? value)
    {
        return TryParse(value, out _);
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length == 7)
        {
            text = "#FF" + text[1..];
        }

        return text.Length == 9
               && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)
               && TryCreate(argb, out color);
    }

    private static bool TryCreate(uint argb, out Color color)
    {
        color = Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
        return true;
    }

    public static Color ParseOrFallback(string? value, Color fallback)
    {
        return TryParse(value, out var color) ? color : fallback;
    }

    public static string ToHex(Color color)
    {
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
