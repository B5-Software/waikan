using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Waikan.Infrastructure;
using Waikan.Models;
using Waikan.Services;

namespace Waikan.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly DatabaseService _database;
    private readonly CloudSyncService _cloudSync;
    private readonly LogService _log;
    private readonly IDialogService _dialogs;
    private readonly AutoStartService _autoStart;
    private readonly AudioDeviceService _audioDevices;
    private readonly ThemeService _theme;
    private readonly RecognitionCoordinator _coordinator;
    private readonly OverlayWindow _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _engineRestartTimer;
    private readonly DispatcherTimer _cloudSyncTimer;
    private readonly DispatcherTimer _cloudSyncDebounceTimer;
    private readonly SemaphoreSlim _cloudSyncGate = new(1, 1);

    private const int MaxHistoryBars = 31;

    private MainPage _currentPage = MainPage.Today;
    private EngineState _engineState = EngineState.Stopped;
    private string _statusDetail = "正在初始化本地识别引擎…";
    private double _inputLevel;
    private bool _initialized;
    private bool _updatingSourceSelection;
    private bool _updatingThemeSelection;
    private bool _updatingAudioDevices;
    private bool _disposed;

    private int _todayCount;
    private string _todayDateTitle = string.Empty;
    private string _todayLastTime = "今天还没有出现";
    private string _todayFirstTime = "—";
    private string _todayPeakHour = "—";
    private string _todaySource = "—";
    private string _todayWeekday = string.Empty;
    private bool _todayHasOccurrences;
    private IReadOnlyList<DateTime> _todayTimelineTimes = Array.Empty<DateTime>();
    private readonly List<OccurrenceRecord> _todayRecords = new();

    private string _rangeStartText = string.Empty;
    private string _rangeEndText = string.Empty;
    private string _rangeError = string.Empty;
    private bool _hasRangeError;
    private string _rangeCaption = string.Empty;
    private int _rangeTotal;
    private int _rangeDayCount;
    private string _rangePeakDay = "—";
    private bool _rangeHasRecords;
    private DateTimeOffset _rangeFrom;
    private DateTimeOffset _rangeTo;
    private List<OccurrenceRecord> _rangeRecords = new();
    private string? _lastDefaultDeviceId;
    private string _deviceSignature = string.Empty;

    private DateTime _lastRefreshDate = DateTime.Today;
    private Brush _overlayPreviewTextBrush = ColorUtilities.Brush(Color.FromArgb(255, 255, 246, 242));

    private bool _isCloudSyncBusy;
    private DateTimeOffset? _cloudSyncLastSyncAt;
    private int _cloudSyncPendingCount;
    private string _cloudSyncStatusTitle = "未配置";
    private string _cloudSyncStatusDetail = "填写查看页地址和配对密钥后即可开启云端同步。";
    private string _cloudSyncStatusTone = "idle";

    private SelectionOption<AudioSourceKind> _selectedSourceOption;
    private SelectionOption<AppTheme> _selectedThemeOption;
    private AudioDeviceOption? _selectedAudioDevice;

    public MainViewModel(
        AppPaths paths,
        AppSettings settings,
        SettingsService settingsService,
        DatabaseService database,
        CloudSyncService cloudSync,
        LogService log,
        IDialogService dialogs,
        AutoStartService autoStart,
        AudioDeviceService audioDevices,
        ThemeService theme,
        RecognitionCoordinator coordinator,
        OverlayWindow overlay)
    {
        _paths = paths;
        Settings = settings;
        _settingsService = settingsService;
        _database = database;
        _cloudSync = cloudSync;
        _log = log;
        _dialogs = dialogs;
        _autoStart = autoStart;
        _audioDevices = audioDevices;
        _theme = theme;
        _coordinator = coordinator;
        _overlay = overlay;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        AudioSourceOptions = new ObservableCollection<SelectionOption<AudioSourceKind>>
        {
            new(AudioSourceKind.Microphone, "麦克风"),
            new(AudioSourceKind.SystemLoopback, "系统声音"),
        };
        ThemeOptions = new ObservableCollection<SelectionOption<AppTheme>>
        {
            new(AppTheme.System, "跟随系统"),
            new(AppTheme.Light, "浅色"),
            new(AppTheme.Dark, "深色"),
        };
        PresetOverlayColors = new ObservableCollection<string>
        {
            "#FFF6F2EE",
            "#FFFF7A59",
            "#FFFFD166",
            "#FF7EE0B8",
            "#FF8EC5FF",
        };

        _selectedSourceOption = AudioSourceOptions.First(option => option.Value == settings.AudioSource);
        _selectedThemeOption = ThemeOptions.First(option => option.Value == settings.Theme);

        Commands = new CommandSet(this);
        TodayOccurrences = new ObservableCollection<OccurrenceItemViewModel>();
        RecentOccurrences = new ObservableCollection<OccurrenceItemViewModel>();
        RangeDayGroups = new ObservableCollection<DayOccurrenceGroupViewModel>();
        RangeBars = new ObservableCollection<DailyBarViewModel>();

        var now = DateTime.Now;
        _todayDateTitle = $"{now:M月d日}";
        _todayWeekday = GetWeekday(now.DayOfWeek);
        SetRangeInput(now.Date, now.Date.AddDays(1).AddSeconds(-1));

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = SaveSettingsSafelyAsync();
        };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => OnClockTick();

        _deviceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _deviceTimer.Tick += (_, _) => OnDeviceTimerTick();

        _engineRestartTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        _engineRestartTimer.Tick += (_, _) =>
        {
            _engineRestartTimer.Stop();
            _ = RestartEngineFromSettingsAsync();
        };

        _cloudSyncTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _cloudSyncTimer.Tick += (_, _) =>
        {
            if (_initialized && !_disposed)
            {
                _ = SyncCloudAsync(manual: false);
            }
        };

        _cloudSyncDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _cloudSyncDebounceTimer.Tick += (_, _) =>
        {
            _cloudSyncDebounceTimer.Stop();
            if (_initialized && !_disposed)
            {
                _ = SyncCloudAsync(manual: false);
            }
        };

        Settings.PropertyChanged += OnSettingsChanged;
        _coordinator.StatusChanged += OnEngineStatusChanged;
        _coordinator.OccurrenceRecorded += OnOccurrenceRecorded;
        _coordinator.InputLevelChanged += OnInputLevelChanged;

        VersionText = $"版本 {AppPaths.Version}";
        DataDirectory = _paths.RootDirectory;
        LogDirectory = _paths.LogDirectory;
        DatabasePath = _paths.DatabasePath;
        ModelDirectory = _paths.ModelDirectory;
        UpdateOverlayPreviewBrushes();
    }

    public AppSettings Settings { get; }

    public CommandSet Commands { get; }

    public ObservableCollection<SelectionOption<AudioSourceKind>> AudioSourceOptions { get; }
    public ObservableCollection<SelectionOption<AppTheme>> ThemeOptions { get; }
    public ObservableCollection<string> PresetOverlayColors { get; }
    public ObservableCollection<AudioDeviceOption> AudioDevices { get; } = new();
    public ObservableCollection<OccurrenceItemViewModel> TodayOccurrences { get; }
    public ObservableCollection<OccurrenceItemViewModel> RecentOccurrences { get; }
    public ObservableCollection<DayOccurrenceGroupViewModel> RangeDayGroups { get; }
    public ObservableCollection<DailyBarViewModel> RangeBars { get; }

    public string VersionText { get; }
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath { get; }
    public string ModelDirectory { get; }
    public string ModelDescription => "sherpa-onnx KWS · 中英双语 3M · int8 · 单线程 CPU";
    public string PrivacyText => "语音只在本机内存中处理，不上传、不录音、不联网。";

    public string EngineStateText => EngineState switch
    {
        EngineState.Listening => "正在聆听",
        EngineState.Loading => "启动中",
        EngineState.Paused => "已暂停",
        EngineState.Error => "识别异常",
        _ => "已停止",
    };

    public string EngineStatusGlyph => EngineState switch
    {
        EngineState.Listening => "\uE768",
        EngineState.Loading => "\uE895",
        EngineState.Paused => "\uE769",
        EngineState.Error => "\uE783",
        _ => "\uE71A",
    };

    public MainPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!SetProperty(ref _currentPage, value))
            {
                return;
            }

            if (value == MainPage.Records && _initialized)
            {
                _ = RefreshRangeIfNeededAsync();
            }
            else if (value == MainPage.Settings)
            {
                RefreshAudioDevices();
            }
        }
    }

    public bool IsTodayPage => CurrentPage == MainPage.Today;
    public bool IsRecordsPage => CurrentPage == MainPage.Records;
    public bool IsSettingsPage => CurrentPage == MainPage.Settings;
    public bool IsAboutPage => CurrentPage == MainPage.About;

    public EngineState EngineState
    {
        get => _engineState;
        private set
        {
            if (_engineState == value)
            {
                return;
            }

            _engineState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EngineStateText));
            OnPropertyChanged(nameof(EngineStatusGlyph));
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(HasEngineError));
            OnPropertyChanged(nameof(IsEnginePaused));
            if (value != EngineState.Listening)
            {
                InputLevel = 0;
            }
        }
    }

    public bool IsListening => EngineState == EngineState.Listening;
    public bool IsBusy => EngineState == EngineState.Loading;
    public bool HasEngineError => EngineState == EngineState.Error;
    public bool IsEnginePaused => EngineState == EngineState.Paused;
    public bool IsRecognitionPaused => Settings.RecognitionPaused;
    public string PauseButtonText => Settings.RecognitionPaused ? "继续识别" : "暂停识别";
    public string PauseButtonGlyph => Settings.RecognitionPaused ? "\uE768" : "\uE769";

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public double InputLevel
    {
        get => _inputLevel;
        private set => SetProperty(ref _inputLevel, Math.Clamp(value, 0, 1));
    }

    public int TodayCount
    {
        get => _todayCount;
        private set => SetProperty(ref _todayCount, value);
    }

    public string TodayDateTitle
    {
        get => _todayDateTitle;
        private set => SetProperty(ref _todayDateTitle, value);
    }

    public string TodayWeekday
    {
        get => _todayWeekday;
        private set => SetProperty(ref _todayWeekday, value);
    }

    public string TodayLastTime
    {
        get => _todayLastTime;
        private set => SetProperty(ref _todayLastTime, value);
    }

    public string TodayFirstTime
    {
        get => _todayFirstTime;
        private set => SetProperty(ref _todayFirstTime, value);
    }

    public string TodayPeakHour
    {
        get => _todayPeakHour;
        private set => SetProperty(ref _todayPeakHour, value);
    }

    public string TodaySource
    {
        get => _todaySource;
        private set => SetProperty(ref _todaySource, value);
    }

    public bool TodayHasOccurrences
    {
        get => _todayHasOccurrences;
        private set => SetProperty(ref _todayHasOccurrences, value);
    }

    public IReadOnlyList<DateTime> TodayTimelineTimes
    {
        get => _todayTimelineTimes;
        private set => SetProperty(ref _todayTimelineTimes, value);
    }

    public string RangeStartText
    {
        get => _rangeStartText;
        set
        {
            if (SetProperty(ref _rangeStartText, value))
            {
                ClearRangeError();
            }
        }
    }

    public string RangeEndText
    {
        get => _rangeEndText;
        set
        {
            if (SetProperty(ref _rangeEndText, value))
            {
                ClearRangeError();
            }
        }
    }

    public string RangeError
    {
        get => _rangeError;
        private set => SetProperty(ref _rangeError, value);
    }

    public bool HasRangeError
    {
        get => _hasRangeError;
        private set => SetProperty(ref _hasRangeError, value);
    }

    public string RangeCaption
    {
        get => _rangeCaption;
        private set => SetProperty(ref _rangeCaption, value);
    }

    public int RangeTotal
    {
        get => _rangeTotal;
        private set => SetProperty(ref _rangeTotal, value);
    }

    public int RangeDayCount
    {
        get => _rangeDayCount;
        private set => SetProperty(ref _rangeDayCount, value);
    }

    public string RangePeakDay
    {
        get => _rangePeakDay;
        private set => SetProperty(ref _rangePeakDay, value);
    }

    public bool RangeHasRecords
    {
        get => _rangeHasRecords;
        private set => SetProperty(ref _rangeHasRecords, value);
    }

    public SelectionOption<AudioSourceKind> SelectedSourceOption
    {
        get => _selectedSourceOption;
        set
        {
            if (!SetProperty(ref _selectedSourceOption, value) || _updatingSourceSelection)
            {
                return;
            }

            if (Settings.AudioSource != value.Value)
            {
                Settings.AudioSource = value.Value;
            }
        }
    }

    public SelectionOption<AppTheme> SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (!SetProperty(ref _selectedThemeOption, value) || _updatingThemeSelection)
            {
                return;
            }

            if (Settings.Theme != value.Value)
            {
                Settings.Theme = value.Value;
            }
        }
    }

    public AudioDeviceOption? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (!SetProperty(ref _selectedAudioDevice, value) || _updatingAudioDevices || value is null)
            {
                return;
            }

            if (!string.Equals(Settings.AudioDeviceId, value.Id, StringComparison.Ordinal))
            {
                Settings.AudioDeviceId = value.Id;
                ScheduleEngineRestart();
            }
        }
    }

    public bool IsCloudSyncBusy
    {
        get => _isCloudSyncBusy;
        private set
        {
            if (SetProperty(ref _isCloudSyncBusy, value))
            {
                OnPropertyChanged(nameof(IsCloudSyncIdle));
            }
        }
    }

    public bool IsCloudSyncIdle => !IsCloudSyncBusy;

    public bool HasCloudSyncConfiguration => Settings.HasCloudSyncConfiguration;

    public string CloudSyncStatusTitle
    {
        get => _cloudSyncStatusTitle;
        private set => SetProperty(ref _cloudSyncStatusTitle, value);
    }

    public string CloudSyncStatusDetail
    {
        get => _cloudSyncStatusDetail;
        private set => SetProperty(ref _cloudSyncStatusDetail, value);
    }

    /// <summary>idle, ok, warn, busy or error. Drives the settings badge colors.</summary>
    public string CloudSyncStatusTone
    {
        get => _cloudSyncStatusTone;
        private set => SetProperty(ref _cloudSyncStatusTone, value);
    }

    public string CloudSyncLastSyncText => _cloudSyncLastSyncAt is null
        ? "本次运行尚未成功同步"
        : $"上次成功同步 {_cloudSyncLastSyncAt.Value.ToLocalTime():MM-dd HH:mm:ss}";

    public string CloudSyncPendingText => _cloudSyncPendingCount <= 0
        ? "没有待同步记录"
        : $"{_cloudSyncPendingCount} 条待同步";

    public Brush OverlayPreviewTextBrush
    {
        get => _overlayPreviewTextBrush;
        private set => SetProperty(ref _overlayPreviewTextBrush, value);
    }

    public string SourceHint => Settings.AudioSource == AudioSourceKind.Microphone
        ? "默认使用麦克风，适合持续识别身边的人声。所有音频只在本机处理。"
        : "识别扬声器正在播放的声音，适合视频和会议。不会录制系统声音。";

    public string AutoStartStateText => _autoStart.IsDisabledByEnvironment
        ? "测试环境中已禁用"
        : Settings.StartWithWindows ? "已加入当前用户启动项" : "未启用";

    public async Task InitializeAsync()
    {
        _log.Info("开始初始化主视图。");
        EnsureAutoStart();
        _theme.Apply(Settings.Theme);
        RefreshAudioDevices();
        UpdateOverlayPreviewBrushes();
        _overlay.ApplySettings(Settings);

        await RefreshTodayAsync();
        await LoadRangeAsync(_rangeFrom, _rangeTo, silent: true);
        _initialized = true;
        await RefreshCloudSyncSummaryAsync();
        InitializeCloudSync();

        _clockTimer.Start();
        _deviceTimer.Start();
        _ = Task.Run(() => MidnightLoopAsync(_lifetimeCts.Token));

        if (Settings.RecognitionPaused)
        {
            EngineState = EngineState.Paused;
            StatusDetail = "识别已暂停，麦克风当前未占用。";
            _overlay.SetCount(TodayCount);
        }
        else
        {
            UpdateCoordinatorConfiguration();
            try
            {
                await _coordinator.StartAsync(_lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                _log.Error("启动识别引擎失败。", ex);
                StatusDetail = "启动失败，可点击“重启引擎”再试。";
                EngineState = EngineState.Error;
            }
        }

        _log.Info("主视图初始化完成。");
    }

    public async Task TogglePauseAsync()
    {
        try
        {
            if (Settings.RecognitionPaused)
            {
                Settings.RecognitionPaused = false;
                UpdateCoordinatorConfiguration();
                await _coordinator.StartAsync(_lifetimeCts.Token);
            }
            else
            {
                await _coordinator.PauseAsync(_lifetimeCts.Token);
                Settings.RecognitionPaused = true;
                EngineState = EngineState.Paused;
                StatusDetail = "识别已暂停，麦克风当前未占用。";
            }

            ScheduleSave();
        }
        catch (Exception ex)
        {
            _log.Error("切换识别暂停状态失败。", ex);
            _dialogs.ShowError("无法切换识别状态", ex.Message);
        }
    }

    public async Task RestartEngineAsync()
    {
        try
        {
            if (Settings.RecognitionPaused)
            {
                Settings.RecognitionPaused = false;
            }

            UpdateCoordinatorConfiguration();
            await _coordinator.RestartAsync(_lifetimeCts.Token);
            ScheduleSave();
        }
        catch (Exception ex)
        {
            _log.Error("手动重启识别引擎失败。", ex);
            _dialogs.ShowError("无法重启识别引擎", ex.Message);
        }
    }

    public async Task HandleSystemResumeAsync()
    {
        if (!_initialized || Settings.RecognitionPaused)
        {
            return;
        }

        _log.Info("系统恢复可用，重新启动音频采集。");
        await RestartEngineAsync();
        await RefreshTodayAsync();
    }

    public void ToggleOverlay() => Settings.OverlayEnabled = !Settings.OverlayEnabled;

    public void ToggleAutostart() => Settings.StartWithWindows = !Settings.StartWithWindows;

    public void Navigate(MainPage page) => CurrentPage = page;

    public string BuildDiagnosticsText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("这个词在外刊中 · 诊断信息");
        builder.AppendLine($"版本: {AppPaths.Version}");
        builder.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"系统: {Environment.OSVersion.VersionString} ({Environment.OSVersion.Platform})");
        builder.AppendLine($"运行时: {Environment.Version}");
        builder.AppendLine($"引擎: {ModelDescription}");
        builder.AppendLine($"状态: {EngineStateText} / {StatusDetail}");
        builder.AppendLine($"今日次数: {TodayCount}");
        builder.AppendLine($"音频源: {(Settings.AudioSource == AudioSourceKind.Microphone ? "麦克风" : "系统声音")}");
        builder.AppendLine($"设备: {SelectedAudioDevice?.Name ?? "系统默认"}");
        builder.AppendLine($"数据目录: {DataDirectory}");
        builder.AppendLine($"模型目录: {ModelDirectory}");
        builder.AppendLine($"云端同步: {CloudSyncStatusTitle} / {CloudSyncStatusDetail}");
        builder.AppendLine($"隐私: {PrivacyText}");
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lifetimeCts.Cancel();
        }
        catch
        {
            // Ignore.
        }

        _saveTimer.Stop();
        _clockTimer.Stop();
        _deviceTimer.Stop();
        _engineRestartTimer.Stop();
        _cloudSyncTimer.Stop();
        _cloudSyncDebounceTimer.Stop();

        Settings.PropertyChanged -= OnSettingsChanged;
        _coordinator.StatusChanged -= OnEngineStatusChanged;
        _coordinator.OccurrenceRecorded -= OnOccurrenceRecorded;
        _coordinator.InputLevelChanged -= OnInputLevelChanged;

        _settingsService.Save(Settings);
        _lifetimeCts.Dispose();
        _refreshGate.Dispose();
    }

    private void EnsureAutoStart()
    {
        if (_autoStart.IsDisabledByEnvironment)
        {
            _log.Info("当前环境已禁用开机自启写入。");
            return;
        }

        try
        {
            _autoStart.SetEnabled(Settings.StartWithWindows);
            OnPropertyChanged(nameof(AutoStartStateText));
        }
        catch (Exception ex)
        {
            _log.Error("初始化开机自启失败。", ex);
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                _updatingThemeSelection = true;
                try
                {
                    SelectedThemeOption = ThemeOptions.First(option => option.Value == Settings.Theme);
                }
                finally
                {
                    _updatingThemeSelection = false;
                }

                _theme.Apply(Settings.Theme);
                break;

            case nameof(AppSettings.AudioSource):
                _updatingSourceSelection = true;
                try
                {
                    SelectedSourceOption = AudioSourceOptions.First(option => option.Value == Settings.AudioSource);
                }
                finally
                {
                    _updatingSourceSelection = false;
                }

                RefreshAudioDevices();
                OnPropertyChanged(nameof(SourceHint));
                UpdateCoordinatorConfiguration();
                ScheduleEngineRestart();
                break;

            case nameof(AppSettings.AudioDeviceId):
                SelectDeviceFromSettings();
                OnPropertyChanged(nameof(SourceHint));
                UpdateCoordinatorConfiguration();
                ScheduleEngineRestart();
                break;

            case nameof(AppSettings.DetectionSensitivity):
                UpdateCoordinatorConfiguration();
                ScheduleEngineRestart();
                break;

            case nameof(AppSettings.OverlayEnabled):
                _overlay.ApplySettings(Settings);
                break;

            case nameof(AppSettings.OverlayTextColor):
                UpdateOverlayPreviewBrushes();
                _overlay.ApplySettings(Settings);
                break;

            case nameof(AppSettings.OverlayFontSize):
                _overlay.ApplySettings(Settings);
                break;

            case nameof(AppSettings.StartWithWindows):
                OnPropertyChanged(nameof(AutoStartStateText));
                try
                {
                    _autoStart.SetEnabled(Settings.StartWithWindows);
                }
                catch (Exception ex)
                {
                    _log.Error("更新开机自启失败。", ex);
                    _dialogs.ShowError(
                        "无法更新开机自启",
                        "没有权限写入当前用户的启动项。应用仍可正常使用，请检查安全软件设置。\n\n" + ex.Message);
                }
                break;

            case nameof(AppSettings.CloudSyncEnabled):
            case nameof(AppSettings.CloudSyncEndpoint):
            case nameof(AppSettings.CloudSyncKey):
                OnCloudSyncSettingsChanged();
                break;

            case nameof(AppSettings.RecognitionPaused):
                OnPropertyChanged(nameof(IsRecognitionPaused));
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(PauseButtonGlyph));
                break;
        }

        if (_initialized)
        {
            ScheduleSave();
        }
    }

    private void OnEngineStatusChanged(EngineStatus status)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            EngineState = status.State;
            StatusDetail = status.Detail;
            if (status.Error is not null)
            {
                _log.Error($"识别引擎状态异常：{status.Error}");
            }
        });
    }

    private void OnOccurrenceRecorded(OccurrenceRecord record)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _log.Info($"界面收到新记录：{record.LocalTime:HH:mm:ss} / {record.SourceLabel}");
                await RefreshTodayAsync();
                if (CurrentPage == MainPage.Records && IsInsideCurrentRange(record.LocalTime))
                {
                    await LoadRangeAsync(_rangeFrom, _rangeTo, silent: true);
                }

                if (Settings.CloudSyncEnabled && Settings.HasCloudSyncConfiguration)
                {
                    _cloudSyncPendingCount = Math.Max(1, _cloudSyncPendingCount + 1);
                    OnPropertyChanged(nameof(CloudSyncPendingText));
                    ScheduleCloudSync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                _log.Error("刷新新识别结果失败。", ex);
            }
        });
    }

    private void OnInputLevelChanged(double level)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && IsListening)
            {
                InputLevel = level;
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateCoordinatorConfiguration()
    {
        _coordinator.UpdateConfiguration(new RecognitionConfiguration(
            Settings.AudioSource,
            Settings.AudioDeviceId,
            Settings.DetectionSensitivity,
            CooldownMilliseconds: 350));
    }

    private void ScheduleEngineRestart()
    {
        if (!_initialized || Settings.RecognitionPaused || _disposed)
        {
            return;
        }

        _engineRestartTimer.Stop();
        _engineRestartTimer.Start();
    }

    private async Task RefreshCloudSyncSummaryAsync()
    {
        try
        {
            _cloudSyncLastSyncAt = await _database.GetLastSyncedAtAsync(_lifetimeCts.Token);
            _cloudSyncPendingCount = await _database.GetPendingCountAsync(_lifetimeCts.Token);
            OnPropertyChanged(nameof(CloudSyncLastSyncText));
            OnPropertyChanged(nameof(CloudSyncPendingText));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.Warn($"读取云端同步状态失败：{ex.Message}");
        }
    }

    private void InitializeCloudSync()
    {
        RefreshCloudSyncStatus();
        UpdateCloudSyncTimer();
        if (Settings.CloudSyncEnabled && Settings.HasCloudSyncConfiguration)
        {
            ScheduleCloudSync(TimeSpan.FromSeconds(4));
        }
    }

    private void OnCloudSyncSettingsChanged()
    {
        OnPropertyChanged(nameof(HasCloudSyncConfiguration));
        UpdateCloudSyncTimer();
        if (!IsCloudSyncBusy)
        {
            RefreshCloudSyncStatus();
        }

        if (Settings.CloudSyncEnabled && Settings.HasCloudSyncConfiguration)
        {
            ScheduleCloudSync(TimeSpan.FromSeconds(6));
        }
    }

    private void UpdateCloudSyncTimer()
    {
        if (!_initialized || _disposed
            || !Settings.CloudSyncEnabled
            || !Settings.HasCloudSyncConfiguration)
        {
            _cloudSyncTimer.Stop();
            return;
        }

        _cloudSyncTimer.Start();
    }

    private void ScheduleCloudSync(TimeSpan delay)
    {
        if (_disposed || !Settings.CloudSyncEnabled || !Settings.HasCloudSyncConfiguration)
        {
            return;
        }

        _cloudSyncDebounceTimer.Stop();
        _cloudSyncDebounceTimer.Interval = delay;
        _cloudSyncDebounceTimer.Start();
    }

    private void RefreshCloudSyncStatus()
    {
        if (!Settings.CloudSyncEnabled)
        {
            SetCloudSyncStatus(
                "已关闭",
                Settings.HasCloudSyncConfiguration
                    ? "打开开关后，本机记录会每 60 秒自动同步一次。"
                    : "填写查看页地址和配对密钥后可开启云端同步。",
                "idle");
            return;
        }

        if (!Settings.HasCloudSyncConfiguration)
        {
            SetCloudSyncStatus(
                "待配置",
                "请填写查看页地址和配对密钥，然后点击“测试连接”。",
                "warn");
            return;
        }

        SetCloudSyncStatus(
            "已启用",
            $"{CloudSyncPendingText} · 后台每 60 秒同步一次。",
            "ok");
    }

    private void SetCloudSyncStatus(string title, string detail, string tone)
    {
        CloudSyncStatusTitle = title;
        CloudSyncStatusDetail = detail;
        CloudSyncStatusTone = tone;
    }

    private async Task SyncCloudAsync(bool manual)
    {
        if (_disposed)
        {
            return;
        }

        if (!Settings.HasCloudSyncConfiguration)
        {
            SetCloudSyncStatus("待配置", "请先填写查看页地址和配对密钥。", "warn");
            if (manual)
            {
                _dialogs.ShowError("云端同步未配置", "请先在设置中填写 Cloudflare 查看页地址和配对密钥。");
            }

            return;
        }

        if (!manual && !Settings.CloudSyncEnabled)
        {
            return;
        }

        if (!await _cloudSyncGate.WaitAsync(0))
        {
            if (manual)
            {
                _dialogs.ShowInfo("正在同步", "上一次云端同步还没有结束，请稍候。");
            }

            return;
        }

        try
        {
            IsCloudSyncBusy = true;
            SetCloudSyncStatus("同步中", "正在把本地记录推送到云端…", "busy");
            var result = await _cloudSync.SyncAsync(
                Settings.CloudSyncEndpoint,
                Settings.CloudSyncKey,
                _lifetimeCts.Token);

            if (result.Ok)
            {
                _cloudSyncLastSyncAt = result.CompletedAt ?? DateTimeOffset.UtcNow;
                _cloudSyncPendingCount = result.Pending >= 0 ? result.Pending : 0;
                OnPropertyChanged(nameof(CloudSyncLastSyncText));
                OnPropertyChanged(nameof(CloudSyncPendingText));

                var detail = result.Uploaded == 0
                    ? "云端已是最新状态。"
                    : $"已上传 {result.Uploaded} 条，云端接收 {result.Accepted} 条"
                      + (result.Duplicates > 0 ? $"，重复 {result.Duplicates} 条。" : "。");
                if (result.Pending > 0)
                {
                    detail += $" 仍有 {result.Pending} 条待同步。";
                }

                SetCloudSyncStatus("已同步", detail, "ok");
                if (manual)
                {
                    _dialogs.ShowInfo("云端同步完成", detail);
                }
            }
            else
            {
                if (result.Pending >= 0)
                {
                    _cloudSyncPendingCount = result.Pending;
                    OnPropertyChanged(nameof(CloudSyncPendingText));
                }

                SetCloudSyncStatus(
                    result.Kind == CloudSyncResultKind.Unauthorized ? "密钥无效" : "同步失败",
                    result.Message,
                    "error");
                _log.Warn($"云端同步未完成：{result.Message}");
                if (manual)
                {
                    _dialogs.ShowError("云端同步失败", result.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown or a superseded request.
        }
        catch (Exception ex)
        {
            SetCloudSyncStatus("同步失败", $"同步失败：{ex.Message}", "error");
            _log.Error("云端同步出现异常。", ex);
            if (manual)
            {
                _dialogs.ShowError("云端同步失败", ex.Message);
            }
        }
        finally
        {
            IsCloudSyncBusy = false;
            _cloudSyncGate.Release();
        }
    }

    private async Task VerifyCloudConnectionAsync()
    {
        if (!Settings.HasCloudSyncConfiguration)
        {
            SetCloudSyncStatus("待配置", "请先填写查看页地址和配对密钥。", "warn");
            _dialogs.ShowError("云端同步未配置", "请先填写 Cloudflare 查看页地址和配对密钥。");
            return;
        }

        if (!await _cloudSyncGate.WaitAsync(0))
        {
            _dialogs.ShowInfo("正在同步", "后台同步还没有结束，请稍候再测试连接。");
            return;
        }

        try
        {
            IsCloudSyncBusy = true;
            SetCloudSyncStatus("验证中", "正在验证站点地址与配对密钥…", "busy");
            var result = await _cloudSync.VerifyAsync(
                Settings.CloudSyncEndpoint,
                Settings.CloudSyncKey,
                _lifetimeCts.Token);

            if (result.Ok)
            {
                var pair = string.IsNullOrWhiteSpace(result.Pair) ? "云端查看页" : result.Pair;
                var detail = $"已连接到「{pair}」";
                if (!string.IsNullOrWhiteSpace(result.TimeZone))
                {
                    detail += $" · 时区 {result.TimeZone}";
                }

                SetCloudSyncStatus("已连接", detail, "ok");
                _dialogs.ShowInfo("连接成功", detail + "\n\n这个密钥只对这一个站点有效。");
            }
            else
            {
                SetCloudSyncStatus("连接失败", result.Message, "error");
                _log.Warn($"云端连接验证失败：{result.Message}");
                _dialogs.ShowError("连接失败", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            SetCloudSyncStatus("连接失败", ex.Message, "error");
            _log.Error("验证云端连接失败。", ex);
            _dialogs.ShowError("连接失败", ex.Message);
        }
        finally
        {
            IsCloudSyncBusy = false;
            _cloudSyncGate.Release();
        }
    }

    private void OpenCloudViewer()
    {
        if (!CloudSyncService.TryNormalizeEndpoint(
                Settings.CloudSyncEndpoint,
                out var endpoint,
                out var error))
        {
            _dialogs.ShowError("无法打开查看页", error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(endpoint) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开云端查看页失败。", ex);
            _dialogs.ShowError("无法打开查看页", ex.Message);
        }
    }

    private async Task RefreshTodayAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var now = DateTime.Now;
            _lastRefreshDate = now.Date;
            TodayDateTitle = $"{now:M月d日}";
            TodayWeekday = GetWeekday(now.DayOfWeek);

            var dayStart = TimeRangeParser.ToDateTimeOffset(now.Date);
            var dayEnd = TimeRangeParser.ToDateTimeOffset(now.Date.AddDays(1).AddMilliseconds(-1));
            var records = await _database.GetOccurrencesAsync(dayStart, dayEnd);
            _todayRecords.Clear();
            _todayRecords.AddRange(records);

            TodayCount = records.Count;
            TodayHasOccurrences = TodayCount > 0;
            UpdateTodayViewModels(now);
            _overlay.SetCount(TodayCount);
            OnPropertyChanged(nameof(TodayHasOccurrences));
        }
        catch (Exception ex)
        {
            _log.Error("刷新今日记录失败。", ex);
            StatusDetail = "读取今日记录失败，请检查数据目录。";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void UpdateTodayViewModels(DateTime now)
    {
        var ordered = _todayRecords.OrderBy(record => record.LocalTime).ToList();
        TodayOccurrences.Clear();
        foreach (var record in ordered)
        {
            TodayOccurrences.Add(new OccurrenceItemViewModel(record, now));
        }

        RecentOccurrences.Clear();
        foreach (var record in ordered.TakeLast(5).Reverse())
        {
            RecentOccurrences.Add(new OccurrenceItemViewModel(record, now));
        }

        TodayTimelineTimes = ordered.Select(record => record.LocalTime.DateTime).ToList();
        TodayHasOccurrences = ordered.Count > 0;

        if (ordered.Count == 0)
        {
            TodayLastTime = "今天还没有出现";
            TodayFirstTime = "—";
            TodayPeakHour = "—";
            TodaySource = Settings.AudioSource == AudioSourceKind.Microphone ? "麦克风" : "系统声音";
            return;
        }

        TodayLastTime = $"最近一次 {ordered[^1].LocalTime:HH:mm:ss}";
        TodayFirstTime = ordered[0].LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var peak = ordered
            .GroupBy(record => record.LocalTime.Hour)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First();
        TodayPeakHour = $"{peak.Key:00}:00 · {peak.Count()} 次";

        var sources = ordered
            .Select(record => record.SourceLabel)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        TodaySource = sources.Length == 1 ? sources[0] : string.Join(" / ", sources);
    }

    private async Task LoadRangeAsync(DateTimeOffset from, DateTimeOffset to, bool silent)
    {
        await _refreshGate.WaitAsync();
        try
        {
            _rangeFrom = from;
            _rangeTo = to;
            var records = await _database.GetOccurrencesAsync(from, to);
            _rangeRecords = records.ToList();
            RangeTotal = records.Count;
            RangeHasRecords = RangeTotal > 0;
            RangeCaption = $"{from:yyyy.MM.dd HH:mm:ss} 至 {to:yyyy.MM.dd HH:mm:ss}";

            RangeDayGroups.Clear();
            RangeBars.Clear();
            var byDay = records
                .GroupBy(record => record.LocalTime.Date)
                .OrderByDescending(group => group.Key)
                .ToList();
            RangeDayCount = byDay.Count;

            var today = DateTime.Today;
            foreach (var group in byDay)
            {
                var items = group
                    .OrderByDescending(record => record.LocalTime)
                    .Select(record => new OccurrenceItemViewModel(record, DateTime.Now))
                    .ToList();
                RangeDayGroups.Add(new DayOccurrenceGroupViewModel(
                    DateOnly.FromDateTime(group.Key),
                    items,
                    group.Key == today));
            }

            if (byDay.Count > 0)
            {
                var peak = byDay.OrderByDescending(group => group.Count()).First();
                RangePeakDay = $"{peak.Key:M月d日} · {peak.Count()} 次";
            }
            else
            {
                RangePeakDay = "—";
            }

            var barGroups = byDay
                .OrderBy(group => group.Key)
                .TakeLast(MaxHistoryBars)
                .ToList();
            var maxCount = Math.Max(1, barGroups.Count == 0 ? 1 : barGroups.Max(group => group.Count()));
            foreach (var group in barGroups)
            {
                var height = Math.Max(4, 64.0 * group.Count() / maxCount);
                RangeBars.Add(new DailyBarViewModel(
                    DateOnly.FromDateTime(group.Key),
                    group.Count(),
                    height,
                    group.Key == today));
            }

            Commands.RefreshCanExecute();
            if (!silent)
            {
                _log.Info($"已加载时间区间记录：{from:O} - {to:O}, total={RangeTotal}.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("加载时间区间失败。", ex);
            if (!silent)
            {
                _dialogs.ShowError("无法读取记录", "读取所选时间区间的记录失败。请检查数据文件是否可访问。\n\n" + ex.Message);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshRangeIfNeededAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await LoadRangeAsync(_rangeFrom, _rangeTo, silent: true);
    }

    private async Task ApplyRangeAsync()
    {
        ClearRangeError();
        if (!TimeRangeParser.TryParseRange(RangeStartText, RangeEndText, out var range, out var error))
        {
            SetRangeError(error);
            return;
        }

        await LoadRangeAsync(range.From, range.To, silent: false);
    }

    private async Task SetPresetAndApplyAsync(DateTime start, DateTime end)
    {
        SetRangeInput(start, end);
        await ApplyRangeAsync();
    }

    private void SetRangeInput(DateTime start, DateTime end)
    {
        RangeStartText = start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RangeEndText = end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private async Task DeleteOccurrenceAsync(object? parameter)
    {
        if (!TryGetLong(parameter, out var id))
        {
            return;
        }

        var item = _rangeRecords.FirstOrDefault(record => record.Id == id)
                   ?? _todayRecords.FirstOrDefault(record => record.Id == id);
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "删除这条记录",
                $"确定删除 {item.LocalTime:yyyy-MM-dd HH:mm:ss} 的这次记录吗？\n\n这项操作无法撤销。"))
        {
            return;
        }

        try
        {
            var deleted = await _database.DeleteOccurrenceAsync(id, _lifetimeCts.Token);
            if (!deleted)
            {
                _dialogs.ShowInfo("记录不存在", "这条记录可能已经被删除。");
            }

            await RefreshTodayAsync();
            await LoadRangeAsync(_rangeFrom, _rangeTo, silent: true);
        }
        catch (Exception ex)
        {
            _log.Error("删除单条记录失败。", ex);
            _dialogs.ShowError("删除失败", ex.Message);
        }
    }

    private async Task DeleteRangeAsync()
    {
        if (!RangeHasRecords)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "删除区间内的记录",
                $"将删除当前时间区间内的 {RangeTotal} 条记录。\n\n这项操作无法撤销，确定继续吗？"))
        {
            return;
        }

        try
        {
            var count = await _database.DeleteRangeAsync(_rangeFrom, _rangeTo, _lifetimeCts.Token);
            _log.Info($"已删除时间区间内的 {count} 条记录。");
            await RefreshTodayAsync();
            await LoadRangeAsync(_rangeFrom, _rangeTo, silent: true);
        }
        catch (Exception ex)
        {
            _log.Error("删除时间区间记录失败。", ex);
            _dialogs.ShowError("删除失败", ex.Message);
        }
    }

    private async Task ExportRangeAsync()
    {
        if (!RangeHasRecords)
        {
            return;
        }

        var file = _dialogs.PickSaveFile(
            "导出记录",
            "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            $"外刊记录_{_rangeFrom:yyyyMMdd_HHmm}_{_rangeTo:yyyyMMdd_HHmm}.csv");
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("序号,日期,时间,关键词,来源,UTC时间");
            var index = 1;
            foreach (var record in _rangeRecords)
            {
                builder.Append(index++).Append(',')
                    .Append(EscapeCsv(record.LocalTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append(',')
                    .Append(EscapeCsv(record.LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                    .Append(EscapeCsv(record.Keyword)).Append(',')
                    .Append(EscapeCsv(record.SourceLabel)).Append(',')
                    .AppendLine(EscapeCsv(record.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture)));
            }

            await File.WriteAllTextAsync(file, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _dialogs.ShowInfo("导出完成", $"已导出 {RangeTotal} 条记录：\n{file}");
        }
        catch (Exception ex)
        {
            _log.Error("导出记录失败。", ex);
            _dialogs.ShowError("导出失败", ex.Message);
        }
    }

    private void PickOverlayColor()
    {
        var color = _dialogs.PickColor(Settings.OverlayTextColor);
        if (!string.IsNullOrWhiteSpace(color))
        {
            Settings.OverlayTextColor = color;
        }
    }

    private void SetOverlayPresetColor(object? parameter)
    {
        if (parameter is string color && ColorUtilities.IsValidHexColor(color))
        {
            Settings.OverlayTextColor = color;
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticsText());
            _dialogs.ShowInfo("已复制", "诊断信息已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _log.Error("复制诊断信息失败。", ex);
            _dialogs.ShowError("复制失败", ex.Message);
        }
    }

    private void OpenMicrophoneSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error("打开 Windows 麦克风设置失败。", ex);
            _dialogs.ShowError("无法打开 Windows 设置", ex.Message);
        }
    }

    private void RefreshAudioDevices()
    {
        if (_disposed)
        {
            return;
        }

        var devices = _audioDevices.GetDevices(Settings.AudioSource);
        var signature = BuildDeviceSignature(devices);
        _deviceSignature = signature;
        _lastDefaultDeviceId = _audioDevices.GetDefaultDeviceId(Settings.AudioSource);

        _updatingAudioDevices = true;
        try
        {
            AudioDevices.Clear();
            foreach (var device in devices)
            {
                AudioDevices.Add(device);
            }

            var selected = AudioDevices.FirstOrDefault(device =>
                               string.Equals(device.Id, Settings.AudioDeviceId, StringComparison.Ordinal))
                           ?? AudioDevices.FirstOrDefault(device => device.Id is null)
                           ?? AudioDevices.FirstOrDefault();

            SelectedAudioDevice = selected;
            if (selected is not null && !string.Equals(Settings.AudioDeviceId, selected.Id, StringComparison.Ordinal))
            {
                Settings.AudioDeviceId = selected.Id;
            }
        }
        finally
        {
            _updatingAudioDevices = false;
        }
    }

    private void SelectDeviceFromSettings()
    {
        _updatingAudioDevices = true;
        try
        {
            SelectedAudioDevice = AudioDevices.FirstOrDefault(device =>
                                     string.Equals(device.Id, Settings.AudioDeviceId, StringComparison.Ordinal))
                                 ?? AudioDevices.FirstOrDefault(device => device.Id is null)
                                 ?? AudioDevices.FirstOrDefault();
        }
        finally
        {
            _updatingAudioDevices = false;
        }
    }

    private void OnDeviceTimerTick()
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        try
        {
            var defaultId = _audioDevices.GetDefaultDeviceId(Settings.AudioSource);
            var devices = _audioDevices.GetDevices(Settings.AudioSource);
            var signature = BuildDeviceSignature(devices);
            var defaultChanged = !string.Equals(defaultId, _lastDefaultDeviceId, StringComparison.Ordinal);
            if (!string.Equals(signature, _deviceSignature, StringComparison.Ordinal) || defaultChanged)
            {
                _log.Info("检测到音频设备变化，正在刷新设备列表。");
                var previousSelection = Settings.AudioDeviceId;
                _deviceSignature = signature;
                _lastDefaultDeviceId = defaultId;

                _updatingAudioDevices = true;
                try
                {
                    AudioDevices.Clear();
                    foreach (var device in devices)
                    {
                        AudioDevices.Add(device);
                    }

                    var selected = AudioDevices.FirstOrDefault(device =>
                                       string.Equals(device.Id, previousSelection, StringComparison.Ordinal))
                                   ?? AudioDevices.FirstOrDefault(device => device.Id is null)
                                   ?? AudioDevices.FirstOrDefault();
                    SelectedAudioDevice = selected;

                    if (selected is not null && !string.Equals(previousSelection, selected.Id, StringComparison.Ordinal))
                    {
                        Settings.AudioDeviceId = selected.Id;
                    }
                }
                finally
                {
                    _updatingAudioDevices = false;
                }

                ScheduleEngineRestart();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"刷新音频设备失败：{ex.Message}");
        }
    }

    private void OnClockTick()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.Now;
        if (now.Date != _lastRefreshDate)
        {
            _log.Info("检测到日期变化，刷新今日计数。");
            _ = RefreshTodayAsync();
            return;
        }

        if (_todayRecords.Count > 0)
        {
            UpdateTodayViewModels(now);
        }
    }

    private async Task MidnightLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var next = now.Date.AddDays(1).AddMilliseconds(300);
                var delay = next - now;
                if (delay < TimeSpan.FromMilliseconds(500))
                {
                    delay = TimeSpan.FromMilliseconds(500);
                }

                await Task.Delay(delay, cancellationToken);
                await _dispatcher.InvokeAsync(() => { _ = RefreshTodayAsync(); });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.Error("跨日刷新计时器失败。", ex);
        }
    }

    private async Task RestartEngineFromSettingsAsync()
    {
        if (!_initialized || Settings.RecognitionPaused || _disposed)
        {
            return;
        }

        try
        {
            UpdateCoordinatorConfiguration();
            await _coordinator.RestartAsync(_lifetimeCts.Token);
            _log.Info("识别配置已更新，识别引擎已重启。");
        }
        catch (Exception ex)
        {
            _log.Error("应用设置后重启识别引擎失败。", ex);
        }
    }

    private void ScheduleSave()
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveSettingsSafelyAsync()
    {
        try
        {
            await _settingsService.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            _log.Error("自动保存设置失败。", ex);
        }
    }

    private void UpdateOverlayPreviewBrushes()
    {
        var text = ColorUtilities.ParseOrFallback(
            Settings.OverlayTextColor,
            Color.FromArgb(255, 255, 246, 242));
        OverlayPreviewTextBrush = ColorUtilities.Brush(text);
    }

    private bool IsInsideCurrentRange(DateTimeOffset localTime)
    {
        return localTime >= _rangeFrom && localTime <= _rangeTo;
    }

    private void SetRangeError(string message)
    {
        RangeError = message;
        HasRangeError = true;
    }

    private void ClearRangeError()
    {
        if (HasRangeError)
        {
            HasRangeError = false;
            RangeError = string.Empty;
        }
    }

    private static bool TryGetLong(object? parameter, out long value)
    {
        switch (parameter)
        {
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static string BuildDeviceSignature(IEnumerable<AudioDeviceOption> devices)
    {
        return string.Join(
            "|",
            devices.Select(device => $"{device.Id ?? "default"}={device.Name}"));
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private static string GetWeekday(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日",
        };
    }

    public sealed class CommandSet
    {
        private readonly MainViewModel _owner;

        internal CommandSet(MainViewModel owner)
        {
            _owner = owner;
            NavigateCommand = new RelayCommand(parameter =>
            {
                if (parameter is MainPage page)
                {
                    _owner.Navigate(page);
                }
                else if (parameter is string text && Enum.TryParse<MainPage>(text, true, out var parsed))
                {
                    _owner.Navigate(parsed);
                }
            });

            TogglePauseCommand = new AsyncRelayCommand(
                _owner.TogglePauseAsync,
                onException: ex => _owner._log.Error("暂停命令失败。", ex));
            RestartEngineCommand = new AsyncRelayCommand(
                _owner.RestartEngineAsync,
                onException: ex => _owner._log.Error("重启引擎命令失败。", ex));
            RefreshTodayCommand = new AsyncRelayCommand(
                _owner.RefreshTodayAsync,
                onException: ex => _owner._log.Error("刷新今日命令失败。", ex));
            ApplyRangeCommand = new AsyncRelayCommand(
                _owner.ApplyRangeAsync,
                onException: ex => _owner._log.Error("应用时间区间命令失败。", ex));
            PresetTodayCommand = new AsyncRelayCommand(
                () => _owner.SetPresetAndApplyAsync(DateTime.Today, DateTime.Today.AddDays(1).AddSeconds(-1)),
                onException: ex => _owner._log.Error("今天区间命令失败。", ex));
            PresetYesterdayCommand = new AsyncRelayCommand(
                () => _owner.SetPresetAndApplyAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddSeconds(-1)),
                onException: ex => _owner._log.Error("昨天区间命令失败。", ex));
            PresetLast7Command = new AsyncRelayCommand(
                () => _owner.SetPresetAndApplyAsync(DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1).AddSeconds(-1)),
                onException: ex => _owner._log.Error("近七天区间命令失败。", ex));
            PresetLast30Command = new AsyncRelayCommand(
                () => _owner.SetPresetAndApplyAsync(DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1).AddSeconds(-1)),
                onException: ex => _owner._log.Error("近三十天区间命令失败。", ex));
            PresetThisMonthCommand = new AsyncRelayCommand(
                () => _owner.SetPresetAndApplyAsync(
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
                    DateTime.Today.AddDays(1).AddSeconds(-1)),
                onException: ex => _owner._log.Error("本月区间命令失败。", ex));
            ExportRangeCommand = new AsyncRelayCommand(
                _owner.ExportRangeAsync,
                () => _owner.RangeHasRecords,
                ex => _owner._log.Error("导出命令失败。", ex));
            DeleteRangeCommand = new AsyncRelayCommand(
                _owner.DeleteRangeAsync,
                () => _owner.RangeHasRecords,
                ex => _owner._log.Error("删除区间命令失败。", ex));
            DeleteOccurrenceCommand = new AsyncRelayCommand(
                _owner.DeleteOccurrenceAsync,
                onException: ex => _owner._log.Error("删除单条命令失败。", ex));
            PickOverlayColorCommand = new RelayCommand(_owner.PickOverlayColor);
            SetOverlayPresetColorCommand = new RelayCommand(_owner.SetOverlayPresetColor);
            OpenDataFolderCommand = new RelayCommand(() => _owner._dialogs.OpenFolder(_owner.DataDirectory));
            OpenLogFolderCommand = new RelayCommand(() => _owner._dialogs.OpenFolder(_owner.LogDirectory));
            OpenModelFolderCommand = new RelayCommand(() => _owner._dialogs.OpenFolder(_owner.ModelDirectory));
            CopyDiagnosticsCommand = new RelayCommand(_owner.CopyDiagnostics);
            OpenMicrophoneSettingsCommand = new RelayCommand(_owner.OpenMicrophoneSettings);
            VerifyCloudConnectionCommand = new AsyncRelayCommand(
                _owner.VerifyCloudConnectionAsync,
                onException: ex => _owner._log.Error("验证云端连接命令失败。", ex));
            SyncCloudCommand = new AsyncRelayCommand(
                () => _owner.SyncCloudAsync(manual: true),
                onException: ex => _owner._log.Error("云端同步命令失败。", ex));
            OpenCloudViewerCommand = new RelayCommand(_owner.OpenCloudViewer);
        }

        public RelayCommand NavigateCommand { get; }
        public AsyncRelayCommand TogglePauseCommand { get; }
        public AsyncRelayCommand RestartEngineCommand { get; }
        public AsyncRelayCommand RefreshTodayCommand { get; }
        public AsyncRelayCommand ApplyRangeCommand { get; }
        public AsyncRelayCommand PresetTodayCommand { get; }
        public AsyncRelayCommand PresetYesterdayCommand { get; }
        public AsyncRelayCommand PresetLast7Command { get; }
        public AsyncRelayCommand PresetLast30Command { get; }
        public AsyncRelayCommand PresetThisMonthCommand { get; }
        public AsyncRelayCommand ExportRangeCommand { get; }
        public AsyncRelayCommand DeleteRangeCommand { get; }
        public AsyncRelayCommand DeleteOccurrenceCommand { get; }
        public RelayCommand PickOverlayColorCommand { get; }
        public RelayCommand SetOverlayPresetColorCommand { get; }
        public RelayCommand OpenDataFolderCommand { get; }
        public RelayCommand OpenLogFolderCommand { get; }
        public RelayCommand OpenModelFolderCommand { get; }
        public RelayCommand CopyDiagnosticsCommand { get; }
        public RelayCommand OpenMicrophoneSettingsCommand { get; }
        public AsyncRelayCommand VerifyCloudConnectionCommand { get; }
        public AsyncRelayCommand SyncCloudCommand { get; }
        public RelayCommand OpenCloudViewerCommand { get; }

        public void RefreshCanExecute()
        {
            ExportRangeCommand.RaiseCanExecuteChanged();
            DeleteRangeCommand.RaiseCanExecuteChanged();
        }
    }
}
