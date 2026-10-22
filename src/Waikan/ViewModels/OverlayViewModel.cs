using System.Windows.Media;
using Waikan.Infrastructure;
using Waikan.Models;

namespace Waikan.ViewModels;

/// <summary>
/// The overlay deliberately renders only the day's count. No card, label or
/// unit text is drawn so it stays unobtrusive over other windows.
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    private int _count;
    private double _countFontSize = 40;
    private Brush _textBrush = ColorUtilities.Brush(Color.FromArgb(255, 255, 246, 242));

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public double CountFontSize
    {
        get => _countFontSize;
        set => SetProperty(ref _countFontSize, value);
    }

    public Brush TextBrush
    {
        get => _textBrush;
        private set => SetProperty(ref _textBrush, value);
    }

    public void ApplySettings(AppSettings settings)
    {
        CountFontSize = Math.Clamp(settings.OverlayFontSize, 12, 240);
        var text = ColorUtilities.ParseOrFallback(
            settings.OverlayTextColor,
            Color.FromArgb(255, 255, 246, 242));
        TextBrush = ColorUtilities.Brush(text);
    }
}
