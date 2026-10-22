using System.Windows;
using Microsoft.Win32;
using Waikan.Interop;
using Waikan.Models;

namespace Waikan.Services;

public sealed class ThemeService
{
    private readonly LogService _log;
    private ResourceDictionary? _palette;

    public ThemeService(LogService log)
    {
        _log = log;
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeApplied;

    public void Apply(AppTheme requestedTheme)
    {
        IsDark = requestedTheme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        var uri = new Uri(
            IsDark
                ? "pack://application:,,,/Waikan;component/Themes/Dark.xaml"
                : "pack://application:,,,/Waikan;component/Themes/Light.xaml",
            UriKind.Absolute);
        var newPalette = new ResourceDictionary { Source = uri };

        // Remove all palette dictionaries, including one merged by App.xaml or
        // by an embedding host, so the new brushes always win over stale ones.
        var existingPalettes = application.Resources.MergedDictionaries
            .Where(dictionary => IsPaletteSource(dictionary.Source))
            .ToList();
        foreach (var existing in existingPalettes)
        {
            application.Resources.MergedDictionaries.Remove(existing);
        }

        if (_palette is not null)
        {
            application.Resources.MergedDictionaries.Remove(_palette);
        }

        application.Resources.MergedDictionaries.Insert(0, newPalette);
        _palette = newPalette;

        foreach (Window window in application.Windows)
        {
            ApplyWindowTheme(window);
        }

        _log.Info($"界面主题已切换为 {(IsDark ? "深色" : "浅色")}。");
        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyWindowTheme(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetDarkTitleBar(hwnd, IsDark);
        NativeMethods.SetRoundedCorners(hwnd);
    }

    private static bool IsPaletteSource(Uri? source)
    {
        if (source is null)
        {
            return false;
        }

        var text = source.OriginalString.Replace('\\', '/');
        return text.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
               || text.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }
}
