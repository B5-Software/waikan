using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Waikan.Models;

namespace Waikan.Services;

public sealed class AudioCaptureStartTimeoutException : TimeoutException
{
    public AudioCaptureStartTimeoutException(string message)
        : base(message)
    {
    }
}

public sealed class AudioCaptureAccessDeniedException : Exception
{
    public AudioCaptureAccessDeniedException(AudioSourceKind source, Exception innerException)
        : base(
            source == AudioSourceKind.Microphone
                ? "Windows 拒绝了麦克风访问。"
                : "Windows 拒绝了系统声音访问。",
            innerException)
    {
        AudioSource = source;
    }

    public AudioSourceKind AudioSource { get; }
}

public sealed class AudioCaptureService : IAsyncDisposable
{
    private const int TargetSampleRate = 16000;
    private const int OutputSamplesPerFrame = TargetSampleRate / 50; // 20 ms

    private readonly LogService _log;
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _sampleProvider;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private volatile bool _stopping;
    private bool _disposed;
    private float _smoothedLevel;
    private long _lastLevelTicks;

    public AudioCaptureService(LogService log)
    {
        _log = log;
    }

    public event Action<AudioFrame>? FrameAvailable;
    public event Action<double>? LevelChanged;
    public event Action<Exception>? Faulted;

    public AudioSourceKind Source { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;
    public bool UsedDefaultFallback { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Audio client initialization is a synchronous COM call on some drivers,
    /// and a broken device can block indefinitely. Run it with a watchdog so the
    /// UI remains responsive and the session can fail fast.
    /// </summary>
    public async Task StartWithWatchdogAsync(
        AudioSourceKind source,
        string? deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var startTask = Task.Run(() => Start(source, deviceId), CancellationToken.None);
        try
        {
            await startTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            DisposeWhenStartCompletes(startTask);
            throw new AudioCaptureStartTimeoutException(
                "打开音频设备超时。可能是设备被其他程序独占、驱动无响应，或 Windows 麦克风权限被关闭。");
        }
        catch (OperationCanceledException)
        {
            DisposeWhenStartCompletes(startTask);
            throw;
        }
        catch
        {
            DisposeWhenStartCompletes(startTask);
            throw;
        }
    }

    private void DisposeWhenStartCompletes(Task startTask)
    {
        _ = startTask.ContinueWith(
            task =>
            {
                _ = task.Exception;
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    _ = DisposeAsync().AsTask();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Start(AudioSourceKind source, string? deviceId)
    {
        ThrowIfDisposed();
        if (IsRunning)
        {
            throw new InvalidOperationException("音频采集已经在运行。");
        }

        Source = source;
        UsedDefaultFallback = false;
        var flow = source == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;

        using var enumerator = new MMDeviceEnumerator();
        _device = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    _device = enumerator.GetDevice(deviceId);
                    if (_device.State != DeviceState.Active)
                    {
                        throw new InvalidOperationException("所选音频设备当前不可用。");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"所选音频设备不可用，将回退到默认设备：{ex.Message}");
                    _device?.Dispose();
                    _device = null;
                    UsedDefaultFallback = true;
                }
            }

            if (_device is null)
            {
                try
                {
                    _device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        source == AudioSourceKind.Microphone
                            ? "没有可用的麦克风。请检查 Windows 的麦克风隐私设置和音频设备。"
                            : "没有可用的扬声器输出设备。", ex);
                }
            }

            DeviceName = SafeFriendlyName(_device);
            if (string.IsNullOrWhiteSpace(DeviceName))
            {
                DeviceName = source == AudioSourceKind.Microphone ? "麦克风" : "系统声音";
            }

            _capture = source == AudioSourceKind.Microphone
                ? new WasapiCapture(_device)
                : new WasapiLoopbackCapture(_device);

            var actualFormat = _capture.WaveFormat ?? throw new InvalidOperationException("音频设备没有返回有效格式。");
            var standardFormat = actualFormat.AsStandardWaveFormat();
            if (standardFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
            {
                throw new NotSupportedException($"音频设备返回了不支持的格式：{standardFormat.Encoding}。");
            }

            _buffered = new BufferedWaveProvider(standardFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };

            _sampleProvider = CreateMono16kProvider(_buffered);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _stopping = false;
            _smoothedLevel = 0;
            _lastLevelTicks = Stopwatch.GetTimestamp();
            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoopAsync(_pumpCts.Token));

            try
            {
                _capture.StartRecording();
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                throw new AudioCaptureAccessDeniedException(source, ex);
            }

            IsRunning = true;
            _log.Info($"音频采集已启动：{source} / {DeviceName} / {actualFormat}");
        }
        catch
        {
            _stopping = true;
            try
            {
                _capture?.StopRecording();
            }
            catch
            {
                // Best effort.
            }

            _pumpCts?.Cancel();
            _capture?.Dispose();
            _capture = null;
            _device?.Dispose();
            _device = null;
            _sampleProvider = null;
            _buffered = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        IsRunning = false;
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // Best effort.
        }

        _pumpCts?.Cancel();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn("音频采集线程未能在 3 秒内停止。");
            }
            catch (Exception ex)
            {
                _log.Warn($"等待音频采集线程停止时出错：{ex.Message}");
            }
        }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;
        _sampleProvider = null;
        _buffered = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping || _buffered is null || e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            ReportFault(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        if (e.Exception is not null)
        {
            ReportFault(e.Exception);
        }
        else
        {
            ReportFault(new InvalidOperationException("音频设备意外停止了采集。"));
        }
    }

    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        var frame = new float[OutputSamplesPerFrame];
        var bytesPerFrameThreshold = Math.Max(
            1,
            (int)((_buffered?.WaveFormat.AverageBytesPerSecond ?? TargetSampleRate * 4) / 50));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_buffered is null || _sampleProvider is null)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_buffered.BufferedBytes < bytesPerFrameThreshold)
                {
                    await Task.Delay(8, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int read;
                try
                {
                    read = _sampleProvider.Read(frame, 0, frame.Length);
                }
                catch (Exception ex)
                {
                    ReportFault(ex);
                    return;
                }

                if (read <= 0)
                {
                    await Task.Delay(8, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var samples = new float[read];
                Array.Copy(frame, samples, read);
                var level = ComputeLevel(samples);
                PublishLevel(level);

                var args = new AudioFrame(samples, DateTimeOffset.Now);
                try
                {
                    FrameAvailable?.Invoke(args);
                }
                catch (Exception ex)
                {
                    _log.Error("音频帧消费者发生异常。", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            ReportFault(ex);
        }
    }

    internal static ISampleProvider CreateMono16kProvider(IWaveProvider waveProvider)
    {
        var sampleProvider = waveProvider.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels > 1)
        {
            sampleProvider = new MultiChannelToMonoSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
        }

        return sampleProvider;
    }

    internal static bool IsAccessDenied(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is System.Runtime.InteropServices.COMException
                && (uint)current.HResult == 0x80070005)
            {
                return true;
            }
        }

        return false;
    }

    private static double ComputeLevel(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        if (rms <= 0.000001)
        {
            return 0;
        }

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    private void PublishLevel(double level)
    {
        _smoothedLevel = (_smoothedLevel * 0.72f) + (float)(level * 0.28);
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastLevelTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsed < 90)
        {
            return;
        }

        _lastLevelTicks = now;
        try
        {
            LevelChanged?.Invoke(_smoothedLevel);
        }
        catch
        {
            // UI subscribers must not be able to break capture.
        }
    }

    private void ReportFault(Exception exception)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _log.Error("音频采集发生错误。", exception);
        try
        {
            Faulted?.Invoke(exception);
        }
        catch (Exception ex)
        {
            _log.Error("音频错误通知失败。", ex);
        }

        try
        {
            _pumpCts?.Cancel();
        }
        catch
        {
            // Best effort.
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioCaptureService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public MultiChannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var read = _source.Read(_buffer, 0, needed);
            if (read <= 0)
            {
                return 0;
            }

            var frames = read / _channels;
            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                var sum = 0f;
                var baseIndex = frameIndex * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[baseIndex + channel];
                }

                buffer[offset + frameIndex] = sum / _channels;
            }

            return frames;
        }
    }
}
