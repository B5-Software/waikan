using System.Diagnostics;
using System.Threading.Channels;
using Waikan.Models;

namespace Waikan.Services;

public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(Exception innerException)
        : base(innerException.Message, innerException)
    {
    }
}

/// <summary>
/// Owns the microphone/capture session and the keyword spotter worker. It
/// serializes start/pause/restart requests, retries transient failures with
/// exponential backoff, and persists each detection before publishing it.
/// </summary>
public sealed class RecognitionCoordinator : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly DatabaseService _database;
    private readonly LogService _log;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _configLock = new();
    private readonly object _statusLock = new();

    private RecognitionConfiguration _configuration = new(AudioSourceKind.Microphone, null, 50);
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private EngineStatus _status = EngineStatus.Stopped;
    private bool _paused;
    private bool _disposed;
    private int _generation;
    private long _lastDetectionUtcTicks;
    private long _droppedFrames;
    private volatile bool _lastSessionUsedFallback;

    public RecognitionCoordinator(
        AppPaths paths,
        DatabaseService database,
        LogService log)
    {
        _paths = paths;
        _database = database;
        _log = log;
    }

    public event Action<EngineStatus>? StatusChanged;
    public event Action<OccurrenceRecord>? OccurrenceRecorded;
    public event Action<double>? InputLevelChanged;

    public EngineStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public bool IsPaused => _paused;

    public void UpdateConfiguration(RecognitionConfiguration configuration)
    {
        lock (_configLock)
        {
            _configuration = configuration;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _paused = false;
            if (_workerTask is { IsCompleted: false })
            {
                return;
            }

            // A worker that exited after a fatal audio startup timeout leaves a
            // completed task and CTS behind. Clean it up before starting again.
            _workerCts?.Dispose();
            _workerCts = null;
            _workerTask = null;
            StartWorkerLocked();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _paused = true;
            await StopWorkerLockedAsync().ConfigureAwait(false);
            PublishStatus(new EngineStatus(EngineState.Paused, "识别已暂停，麦克风当前未占用。"));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_paused)
            {
                return;
            }

            await StopWorkerLockedAsync().ConfigureAwait(false);
            StartWorkerLocked();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartWorkerLocked()
    {
        var generation = Interlocked.Increment(ref _generation);
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;
        _workerTask = Task.Run(() => WorkerLoopAsync(generation, token), CancellationToken.None);
        _log.Info($"识别工作线程已启动（generation={generation}）。");
    }

    private async Task StopWorkerLockedAsync()
    {
        Interlocked.Increment(ref _generation);
        var cts = _workerCts;
        var task = _workerTask;
        _workerCts = null;
        _workerTask = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (task is not null)
            {
                await task.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _log.Warn("识别工作线程未能在 6 秒内停止。");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            _log.Error("停止识别工作线程时发生错误。", ex);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task WorkerLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionStopwatch = Stopwatch.StartNew();
            try
            {
                await RunSessionAsync(generation, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                throw new InvalidOperationException("识别会话意外结束。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (AudioCaptureStartTimeoutException ex)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _log.Error("打开音频设备超时，停止自动重试。", ex);
                    PublishStatus(generation, new EngineStatus(
                        EngineState.Error,
                        "打开音频设备超时。请检查麦克风连接、隐私权限，然后点击“重启引擎”。",
                        ex.Message));
                }

                break;
            }
            catch (AudioCaptureAccessDeniedException ex)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _log.Error("音频设备访问被拒绝，停止自动重试。", ex);
                    var detail = ex.AudioSource == AudioSourceKind.Microphone
                        ? "Windows 拒绝了麦克风访问。请检查麦克风隐私权限或更换输入设备，然后点击“重启引擎”。"
                        : "Windows 拒绝了系统声音访问。请检查输出设备，然后点击“重启引擎”。";
                    PublishStatus(generation, new EngineStatus(EngineState.Error, detail, ex.Message));
                }

                break;
            }
            catch (Exception ex) when (AudioCaptureService.IsAccessDenied(ex))
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _log.Error("音频设备访问被拒绝，停止自动重试。", ex);
                    PublishStatus(generation, new EngineStatus(
                        EngineState.Error,
                        "Windows 拒绝了麦克风访问。请检查麦克风隐私权限或更换输入设备，然后点击“重启引擎”。",
                        ex.Message));
                }

                break;
            }
            catch (Exception ex)
            {
                if (generation != Volatile.Read(ref _generation) || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                failures = sessionStopwatch.Elapsed > TimeSpan.FromSeconds(10) ? 1 : failures + 1;
                var retrySeconds = Math.Min(30, Math.Pow(2, Math.Min(5, failures)));
                var friendly = CreateFriendlyError(ex);
                _log.Error($"识别会话失败（第 {failures} 次），将在 {retrySeconds:F0} 秒后重试。", ex);
                PublishStatus(generation, new EngineStatus(EngineState.Error, friendly, ex.Message));

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSessionAsync(int generation, CancellationToken cancellationToken)
    {
        RecognitionConfiguration configuration;
        lock (_configLock)
        {
            configuration = _configuration;
        }

        if (!_paths.HasModelFiles())
        {
            throw new FileNotFoundException($"缺少识别模型文件，请检查：{_paths.ModelDirectory}");
        }

        PublishStatus(generation, new EngineStatus(EngineState.Loading, "正在加载本地关键词模型…"));
        using var spotter = new KeywordSpotterEngine(
            _paths.ModelDirectory,
            _paths.KeywordsPath,
            configuration.Sensitivity,
            _log);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });

        Exception? captureFault = null;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var capture = new AudioCaptureService(_log);
        var captureStarted = false;

        capture.FrameAvailable += frame =>
        {
            if (!channel.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        };
        capture.LevelChanged += level =>
        {
            if (generation == Volatile.Read(ref _generation))
            {
                try
                {
                    InputLevelChanged?.Invoke(level);
                }
                catch
                {
                    // Never let a UI subscriber break audio.
                }
            }
        };
        capture.Faulted += exception =>
        {
            captureFault = exception;
            try
            {
                sessionCts.Cancel();
            }
            catch
            {
                // Best effort.
            }
        };

        try
        {
            PublishStatus(generation, new EngineStatus(
                EngineState.Loading,
                configuration.Source == AudioSourceKind.Microphone
                    ? "正在打开麦克风…"
                    : "正在连接系统声音…"));
            _log.Info(
                $"正在打开音频设备：source={configuration.Source}, device={configuration.DeviceId ?? "default"}");
            await capture.StartWithWatchdogAsync(
                configuration.Source,
                configuration.DeviceId,
                TimeSpan.FromSeconds(9),
                sessionCts.Token).ConfigureAwait(false);
            captureStarted = true;

            _lastSessionUsedFallback = capture.UsedDefaultFallback;
            var sourceText = configuration.Source == AudioSourceKind.Microphone ? "麦克风" : "系统声音";
            var detail = $"{sourceText} · {capture.DeviceName}";
            if (capture.UsedDefaultFallback)
            {
                detail += "（原设备不可用，已回退默认设备）";
            }

            PublishStatus(generation, new EngineStatus(EngineState.Listening, detail));
            _log.Info($"音频采集已启动：{sourceText} / {capture.DeviceName} / sensitivity={configuration.Sensitivity}");

            var lastDropLog = DateTimeOffset.MinValue;
            while (!sessionCts.IsCancellationRequested)
            {
                AudioFrame frame;
                try
                {
                    frame = await channel.Reader.ReadAsync(sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                var keyword = spotter.Process(frame.Samples);
                if (keyword is not null)
                {
                    await HandleDetectionAsync(keyword, frame, configuration, sessionCts.Token).ConfigureAwait(false);
                }

                var dropped = Interlocked.Read(ref _droppedFrames);
                if (dropped > 0 && DateTimeOffset.UtcNow - lastDropLog > TimeSpan.FromSeconds(30))
                {
                    _log.Warn($"识别处理速度落后，已丢弃 {dropped} 个音频帧。");
                    lastDropLog = DateTimeOffset.UtcNow;
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            if (captureStarted)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (captureFault is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new AudioCaptureException(captureFault);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task HandleDetectionAsync(
        string keyword,
        AudioFrame frame,
        RecognitionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var nowUtcTicks = now.UtcTicks;
        var lastTicks = Interlocked.Read(ref _lastDetectionUtcTicks);
        var cooldown = TimeSpan.FromMilliseconds(Math.Max(250, configuration.CooldownMilliseconds));
        if (lastTicks != 0 && nowUtcTicks - lastTicks < cooldown.Ticks)
        {
            _log.Info($"忽略冷却期内的重复触发：{keyword}");
            return;
        }

        Interlocked.Exchange(ref _lastDetectionUtcTicks, nowUtcTicks);

        // KWS output arrives after the acoustic word. A small fixed offset puts
        // the displayed time closer to the actual word instead of the decode time.
        var occurredLocal = now.AddMilliseconds(-450);
        var occurredUtc = occurredLocal.ToUniversalTime();

        OccurrenceRecord? record = null;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                record = await _database.InsertOccurrenceAsync(
                    occurredUtc,
                    occurredLocal,
                    configuration.Source == AudioSourceKind.Microphone
                        ? OccurrenceRecord.AudioCaptureSourceMicrophone
                        : OccurrenceRecord.AudioCaptureSourceSystem,
                    keyword,
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                _log.Error($"写入识别记录失败（第 {attempt} 次）。", ex);
                if (attempt < 3)
                {
                    try
                    {
                        await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        if (record is null)
        {
            _log.Error("识别记录连续写入失败，已丢弃本次结果。", lastError);
            return;
        }

        _log.Info(
            $"命中「{record.Keyword}」：local={record.LocalTime:yyyy-MM-dd HH:mm:ss.fff}, " +
            $"source={record.Source}, frameAge={(now - frame.CapturedAt).TotalMilliseconds:F0}ms, " +
            $"fallback={_lastSessionUsedFallback}");

        try
        {
            OccurrenceRecorded?.Invoke(record);
        }
        catch (Exception ex)
        {
            _log.Error("通知界面识别结果失败。", ex);
        }
    }

    private void PublishStatus(EngineStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            _log.Error("通知识别状态失败。", ex);
        }
    }

    private void PublishStatus(int generation, EngineStatus status)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        PublishStatus(status);
    }

    private static string CreateFriendlyError(Exception exception)
    {
        return exception switch
        {
            AudioCaptureAccessDeniedException => "Windows 拒绝了麦克风访问，请检查隐私权限。",
            UnauthorizedAccessException => "Windows 拒绝了麦克风访问，请检查隐私权限。",
            AudioCaptureException => "无法读取音频设备。请检查麦克风连接和 Windows 隐私设置。",
            DllNotFoundException => "缺少本地推理组件，无法启动语音识别。",
            FileNotFoundException => "识别模型文件缺失，无法启动语音识别。",
            BadImageFormatException => "本地推理组件与系统架构不匹配。",
            _ => "识别引擎发生异常，正在自动重试。",
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RecognitionCoordinator));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _paused = true;
            await StopWorkerLockedAsync().ConfigureAwait(false);
            PublishStatus(EngineStatus.Stopped);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
