using Waikan.Models;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task Save_And_Load_RoundTrips_Settings()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var log = TestInfrastructure.CreateLog(root);
            var service = new SettingsService(paths, log);
            var settings = new AppSettings
            {
                AudioSource = AudioSourceKind.SystemLoopback,
                AudioDeviceId = "device-123",
                DetectionSensitivity = 81,
                OverlayEnabled = false,
                OverlayTextColor = "#FFFFD166",
                OverlayFontSize = 73,
                StartWithWindows = false,
                MinimizeToTrayOnClose = false,
                Theme = AppTheme.Light,
            };

            await service.SaveAsync(settings);
            var loaded = service.Load();

            Assert.Equal(AudioSourceKind.SystemLoopback, loaded.AudioSource);
            Assert.Equal("device-123", loaded.AudioDeviceId);
            Assert.Equal(81, loaded.DetectionSensitivity);
            Assert.False(loaded.OverlayEnabled);
            Assert.Equal("#FFFFD166", loaded.OverlayTextColor);
            Assert.Equal(73, loaded.OverlayFontSize);
            Assert.False(loaded.StartWithWindows);
            Assert.False(loaded.MinimizeToTrayOnClose);
            Assert.Equal(AppTheme.Light, loaded.Theme);
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Corrupt_File_Falls_Back_To_Defaults_And_Creates_Backup()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var log = TestInfrastructure.CreateLog(root);
            File.WriteAllText(paths.SettingsPath, "{ this is not json");
            var service = new SettingsService(paths, log);

            var settings = service.Load();

            Assert.NotNull(settings);
            Assert.NotEmpty(Directory.GetFiles(root, "settings.json.corrupt-*"));
            Assert.Equal(AudioSourceKind.Microphone, settings.AudioSource);
            Assert.Equal(AppDefaultTestValue.Sensitivity, settings.DetectionSensitivity);
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Normalize_Clamps_Out_Of_Range_Values()
    {
        var settings = new AppSettings
        {
            DetectionSensitivity = 500,
            OverlayFontSize = 5,
            OverlayTextColor = "not-a-color",
        };

        settings.Normalize();

        Assert.Equal(100, settings.DetectionSensitivity);
        Assert.Equal(12, settings.OverlayFontSize);
        Assert.Equal(AppSettings.DefaultOverlayColor, settings.OverlayTextColor);

        settings.OverlayFontSize = 999;
        settings.Normalize();
        Assert.Equal(240, settings.OverlayFontSize);
    }

    private static class AppDefaultTestValue
    {
        public const int Sensitivity = 50;
    }
}
