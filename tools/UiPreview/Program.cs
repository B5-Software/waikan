using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Waikan;
using Waikan.Infrastructure;
using Waikan.Models;
using Waikan.Services;
using Waikan.ViewModels;

namespace UiPreview;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "WaikanUiPreview", "data");
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "WaikanUiPreview", "shots");

    [STAThread]
    private static int Main()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, Root);
        Environment.SetEnvironmentVariable(AppPaths.DisableAutostartEnvironmentVariable, "1");
        PrepareDirectories();

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        AddResources(app);
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunPreviewAsync(app);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Shots, "error.txt"), ex.ToString());
                Console.Error.WriteLine(ex);
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        return 0;
    }

    private static async Task RunPreviewAsync(System.Windows.Application app)
    {
        var paths = new AppPaths(Root);
        var log = new LogService(paths.LogDirectory);
        var settingsService = new SettingsService(paths, log);
        var settings = settingsService.Load();
        settings.StartWithWindows = false;
        settings.RecognitionPaused = true;
        settings.OverlayEnabled = true;
        settings.Theme = AppTheme.Dark;
        await settingsService.SaveAsync(settings);

        var database = new DatabaseService(paths.DatabasePath, log);
        await database.InitializeAsync();
        await SeedAsync(database);

        var theme = new ThemeService(log);
        theme.Apply(AppTheme.Dark);
        var autoStart = new AutoStartService(paths, log);
        var audioDevices = new AudioDeviceService(log);
        var dialogs = new DialogService(log);
        var coordinator = new RecognitionCoordinator(paths, database, log);
        var overlay = new OverlayWindow();

        var viewModel = new MainViewModel(
            paths,
            settings,
            settingsService,
            database,
            log,
            dialogs,
            autoStart,
            audioDevices,
            theme,
            coordinator,
            overlay);

        var window = new MainWindow(viewModel, theme)
        {
            Width = 1180,
            Height = 790,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 20,
            Top = 20,
        };
        app.MainWindow = window;
        window.Show();

        await viewModel.InitializeAsync();
        settings.RecognitionPaused = false;
        SetPrivateProperty(viewModel, "EngineState", EngineState.Listening);
        SetPrivateProperty(viewModel, "StatusDetail", "麦克风 · 系统默认麦克风");
        SetPrivateProperty(viewModel, "InputLevel", 0.42);

        viewModel.CurrentPage = MainPage.Today;
        await SettleAsync(window);
        Capture(window, "01-today-dark.png");

        viewModel.CurrentPage = MainPage.Records;
        var now = DateTime.Now;
        viewModel.RangeStartText = now.Date.AddDays(-6).ToString("yyyy-MM-dd HH:mm:ss");
        viewModel.RangeEndText = now.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss");
        viewModel.Commands.ApplyRangeCommand.Execute(null);
        await Task.Delay(700);
        await SettleAsync(window);
        Capture(window, "02-records-dark.png");

        viewModel.CurrentPage = MainPage.Settings;
        await SettleAsync(window);
        Capture(window, "03-settings-dark.png");

        viewModel.CurrentPage = MainPage.About;
        await SettleAsync(window);
        Capture(window, "04-about-dark.png");

        settings.Theme = AppTheme.Light;
        viewModel.CurrentPage = MainPage.Today;
        await Task.Delay(300);
        await SettleAsync(window);
        Capture(window, "05-today-light.png");

        viewModel.CurrentPage = MainPage.Records;
        await SettleAsync(window);
        Capture(window, "06-records-light.png");

        overlay.SetCount(9);
        overlay.ApplySettings(settings);
        await Task.Delay(200);
        overlay.UpdateLayout();
        Capture(overlay, "07-overlay.png");

        viewModel.Dispose();
        overlay.Shutdown();
        await coordinator.DisposeAsync();
        window.Hide();
        Console.WriteLine("Preview captured.");
    }

    private static async Task SeedAsync(DatabaseService database)
    {
        var now = DateTime.Now;
        var random = new Random(20261022);
        var todayTimes = new List<DateTime>();
        for (var index = 0; index < 9; index++)
        {
            var time = now.AddMinutes(-(13 + index * 31));
            if (time.Date == now.Date)
            {
                todayTimes.Add(time);
            }
        }

        foreach (var time in todayTimes)
        {
            await database.InsertOccurrenceAsync(
                ToOffset(time).ToUniversalTime(),
                ToOffset(time),
                OccurrenceRecord.AudioCaptureSourceMicrophone,
                AppPaths.Keyword);
        }

        for (var daysAgo = 1; daysAgo <= 6; daysAgo++)
        {
            var day = now.Date.AddDays(-daysAgo);
            var count = random.Next(2, 7);
            for (var index = 0; index < count; index++)
            {
                var time = day.AddHours(random.Next(8, 22)).AddMinutes(random.Next(0, 60)).AddSeconds(random.Next(0, 60));
                await database.InsertOccurrenceAsync(
                    ToOffset(time).ToUniversalTime(),
                    ToOffset(time),
                    index % 4 == 0 ? OccurrenceRecord.AudioCaptureSourceSystem : OccurrenceRecord.AudioCaptureSourceMicrophone,
                    AppPaths.Keyword);
            }
        }
    }

    private static DateTimeOffset ToOffset(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(220);
        window.UpdateLayout();
    }

    private static void Capture(Window window, string fileName)
    {
        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        if (content is Panel panel)
        {
            panel.Background = window.Background;
        }

        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Shots, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"captured {path} ({width}x{height})");
    }

    private static void SetPrivateProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                       ?? throw new InvalidOperationException($"Property not found: {propertyName}");
        var setter = property.GetSetMethod(nonPublic: true)
                     ?? throw new InvalidOperationException($"Setter not found: {propertyName}");
        setter.Invoke(target, new[] { value });
    }

    private static void PrepareDirectories()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        if (Directory.Exists(Shots))
        {
            Directory.Delete(Shots, recursive: true);
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Shots);
    }

    private static void AddResources(System.Windows.Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Waikan;component/Themes/Dark.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Waikan;component/Themes/Controls.xaml"),
        });
        app.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        app.Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
        app.Resources["InverseBool"] = new InverseBooleanConverter();
        app.Resources["EnumEquals"] = new EnumEqualsConverter();
        app.Resources["EnumVisibility"] = new EnumVisibilityConverter();
        app.Resources["HexToBrush"] = new HexToBrushConverter();
        app.Resources["BoolToOpacity"] = new BooleanToOpacityConverter();
    }
}
