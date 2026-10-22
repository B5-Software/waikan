using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Waikan.Models;
using Waikan.Services;
using Waikan.ViewModels;

namespace Waikan;

public partial class App : System.Windows.Application
{
    private AppPaths? _paths;
    private LogService? _log;
    private SettingsService? _settingsService;
    private AppSettings? _settings;
    private DatabaseService? _database;
    private AutoStartService? _autoStart;
    private AudioDeviceService? _audioDevices;
    private ThemeService? _theme;
    private RecognitionCoordinator? _coordinator;
    private OverlayWindow? _overlay;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private SingleInstanceService? _singleInstance;
    private bool _exitInProgress;
    private bool _systemEventsSubscribed;
    private bool _allowClose;

    public static App? CurrentApp { get; private set; }

    public bool IsExiting => _exitInProgress || _allowClose;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CurrentApp = this;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var options = StartupOptions.Parse(e.Args);
        if (options.NoAutostart)
        {
            Environment.SetEnvironmentVariable(AppPaths.DisableAutostartEnvironmentVariable, "1");
        }

        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            Environment.SetEnvironmentVariable(
                AppPaths.DataDirectoryEnvironmentVariable,
                Path.GetFullPath(options.DataDirectory));
        }

        try
        {
            _paths = new AppPaths();
            _log = new LogService(_paths.LogDirectory);
            _log.Info($"应用启动，版本 {AppPaths.Version}，参数：{string.Join(' ', e.Args)}");
            _log.Info($"数据目录：{_paths.RootDirectory}；可执行文件：{_paths.ExecutablePath}");

            InstallExceptionHandlers();

            _singleInstance = new SingleInstanceService(_log);
            if (!_singleInstance.TryAcquire())
            {
                var command = options.Exit ? "exit" : "show";
                var sent = await SingleInstanceService.SendCommandAsync(command, TimeSpan.FromSeconds(5));
                _log.Info(sent
                    ? $"检测到已有实例，已发送命令：{command}"
                    : $"检测到已有实例，但发送命令失败：{command}");
                if (!sent)
                {
                    System.Windows.MessageBox.Show(
                        "《这个词在外刊中》已经在运行，但无法唤醒它的窗口。请查看系统托盘。",
                        "这个词在外刊中",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }

            if (options.Exit)
            {
                _log.Info("收到 --exit，但当前没有正在运行的实例，直接退出。");
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }

            _settingsService = new SettingsService(_paths, _log);
            _settings = _settingsService.Load();
            _theme = new ThemeService(_log);
            _theme.Apply(_settings.Theme);

            _database = new DatabaseService(_paths.DatabasePath, _log);
            await _database.InitializeAsync();

            _autoStart = new AutoStartService(_paths, _log);
            _audioDevices = new AudioDeviceService(_log);
            var dialogs = new DialogService(_log);
            _overlay = new OverlayWindow();
            _coordinator = new RecognitionCoordinator(_paths, _database, _log);

            _viewModel = new MainViewModel(
                _paths,
                _settings,
                _settingsService,
                _database,
                _log,
                dialogs,
                _autoStart,
                _audioDevices,
                _theme,
                _coordinator,
                _overlay);

            // MainWindow is created lazily. A boot-time background instance only
            // needs the tray and the small overlay, not the full dashboard.
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            _tray = new TrayIconService(
                _log,
                ShowMainWindow,
                () => _viewModel.ToggleOverlay(),
                () => _ = _viewModel.TogglePauseAsync(),
                () => _viewModel.ToggleAutostart(),
                RequestExit)
            {
                IsOverlayEnabled = () => _viewModel.Settings.OverlayEnabled,
                IsPaused = () => _viewModel.Settings.RecognitionPaused,
                IsAutostartEnabled = () => _viewModel.Settings.StartWithWindows,
            };
            _log.Info("系统托盘图标已创建。");

            _singleInstance.CommandReceived += OnSingleInstanceCommand;
            _singleInstance.StartPipeServer();

            SubscribeSystemEvents();

            if (!options.Background)
            {
                EnsureMainWindow().Show();
            }

            await _viewModel.InitializeAsync();
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _log?.Error("应用启动失败。", ex);
            System.Windows.MessageBox.Show(
                $"应用启动失败。\n\n{ex.Message}\n\n数据目录：{_paths?.RootDirectory ?? "未知"}\n日志：{_log?.CurrentLogPath ?? "未知"}",
                "这个词在外刊中",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RequestExit();
        }
    }

    public void ShowMainWindow()
    {
        if (_exitInProgress)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowMainWindow);
            return;
        }

        EnsureMainWindow().ShowAndActivate();
    }

    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }

        if (_viewModel is null || _theme is null)
        {
            throw new InvalidOperationException("应用尚未完成初始化，无法创建主窗口。");
        }

        _mainWindow = new MainWindow(_viewModel, _theme);
        MainWindow = _mainWindow;
        return _mainWindow;
    }

    public void ShowMinimizeHintOnce()
    {
        if (_settings is null || _tray is null || _settings.HasShownTrayHint)
        {
            return;
        }

        _settings.HasShownTrayHint = true;
        _tray.ShowMinimizeHint();
    }

    public void MarkExiting()
    {
        _allowClose = true;
    }

    public void RequestExit()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        try
        {
            _log?.Info("开始退出应用。");
            UnsubscribeSystemEvents();

            if (_mainWindow is not null)
            {
                _allowClose = true;
                try
                {
                    _mainWindow.Close();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"关闭主窗口失败：{ex.Message}");
                }
            }

            if (_tray is not null)
            {
                _tray.Hide();
            }

            _viewModel?.Dispose();

            if (_coordinator is not null)
            {
                try
                {
                    await _coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (TimeoutException)
                {
                    _log?.Warn("停止识别引擎超时，继续退出。");
                }
                catch (Exception ex)
                {
                    _log?.Error("停止识别引擎时出错。", ex);
                }
            }

            if (_overlay is not null)
            {
                try
                {
                    _overlay.Shutdown();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"关闭悬浮窗失败：{ex.Message}");
                }
            }

            _tray?.Dispose();
            _singleInstance?.Dispose();
            _log?.Info("应用已退出。");
        }
        catch (Exception ex)
        {
            _log?.Error("退出流程失败。", ex);
        }
        finally
        {
            _log = null;
            Shutdown(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemEventsUnsubscribeSafe();
        base.OnExit(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TodayCount)
            or nameof(MainViewModel.EngineState)
            or nameof(MainViewModel.StatusDetail)
            or nameof(MainViewModel.IsRecognitionPaused))
        {
            UpdateTrayState();
        }
    }

    private void UpdateTrayState()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateTrayState);
            return;
        }

        _tray.UpdateStatus(_viewModel.EngineState, _viewModel.TodayCount);
    }

    private void OnSingleInstanceCommand(string command)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (string.Equals(command, "exit", StringComparison.OrdinalIgnoreCase))
            {
                RequestExit();
            }
            else
            {
                ShowMainWindow();
            }
        });
    }

    private void SubscribeSystemEvents()
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _systemEventsSubscribed = true;
        }
        catch (Exception ex)
        {
            _log?.Warn($"订阅系统事件失败：{ex.Message}");
        }
    }

    private void UnsubscribeSystemEvents()
    {
        if (!_systemEventsSubscribed)
        {
            return;
        }

        _systemEventsSubscribed = false;
        _systemEventsUnsubscribeSafe();
    }

    private void _systemEventsUnsubscribeSafe()
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
            // Best effort.
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _viewModel is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await _viewModel.HandleSystemResumeAsync();
            }
            catch (Exception ex)
            {
                _log?.Error("系统唤醒后的恢复处理失败。", ex);
            }
        });
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect))
        {
            return;
        }

        if (_viewModel is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                _log?.Info($"会话恢复可用（{e.Reason}），重新启动音频采集。");
                await _viewModel.HandleSystemResumeAsync();
            }
            catch (Exception ex)
            {
                _log?.Error("会话恢复后的音频重启失败。", ex);
            }
        });
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings is null || _theme is null || _settings.Theme != AppTheme.System)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_settings.Theme == AppTheme.System)
            {
                _theme.Apply(AppTheme.System);
            }
        });
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log?.Error("未处理的进程异常。", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("未观察到的后台任务异常。", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) =>
        {
            _allowClose = true;
            try
            {
                _settingsService?.Save(_settings!);
            }
            catch
            {
                // The OS is ending the session; best effort.
            }
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("未处理的界面异常。", e.Exception);
        e.Handled = true;
        if (_exitInProgress)
        {
            return;
        }

        System.Windows.MessageBox.Show(
            $"界面发生了一个错误，已记录到日志。\n\n{e.Exception.Message}\n\n日志：{_log?.CurrentLogPath ?? "未知"}",
            "这个词在外刊中",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private sealed record StartupOptions(bool Background, bool Exit, bool NoAutostart, string? DataDirectory)
    {
        public static StartupOptions Parse(string[] args)
        {
            var background = args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            var exit = args.Any(arg => string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase));
            var noAutostart = args.Any(arg => string.Equals(arg, "--no-autostart", StringComparison.OrdinalIgnoreCase));
            string? dataDirectory = null;
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    dataDirectory = args[++index];
                }
                else if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    dataDirectory = arg["--data-dir=".Length..];
                }
            }

            return new StartupOptions(background, exit, noAutostart, dataDirectory);
        }
    }
}
