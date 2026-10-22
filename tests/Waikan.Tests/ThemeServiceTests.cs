using System.Windows;
using System.Windows.Media;
using Waikan.Models;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class ThemeServiceTests
{
    [Fact]
    public void Applying_A_Theme_Removes_The_Previous_Palette()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = TestInfrastructure.CreateTempDirectory();
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    };
                }

                var app = System.Windows.Application.Current
                          ?? throw new InvalidOperationException("WPF Application was not created.");
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Waikan;component/Themes/Dark.xaml"),
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Waikan;component/Themes/Controls.xaml"),
                });

                var service = new ThemeService(TestInfrastructure.CreateLog(root));
                service.Apply(AppTheme.Light);

                var lightPalette = app.Resources.MergedDictionaries
                    .Single(dictionary => dictionary.Source?.OriginalString.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase) == true);
                var background = Assert.IsType<SolidColorBrush>(lightPalette["AppBackgroundBrush"]);
                Assert.Equal(Color.FromRgb(0xF3, 0xF5, 0xF9), background.Color);

                service.Apply(AppTheme.Dark);
                var palettes = app.Resources.MergedDictionaries
                    .Where(dictionary =>
                        dictionary.Source?.OriginalString.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true
                        || dictionary.Source?.OriginalString.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
                Assert.Single(palettes);
                var darkBackground = Assert.IsType<SolidColorBrush>(palettes[0]["AppBackgroundBrush"]);
                Assert.Equal(Color.FromRgb(0x0B, 0x0F, 0x14), darkBackground.Color);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                TestInfrastructure.TryDeleteDirectory(root);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Theme test STA thread timed out.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
