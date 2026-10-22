using System.IO.Pipes;
using System.Text;

namespace Waikan.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\Waikan.SingleInstance.6C7F3A91";
    private const string PipeName = "Waikan.SingleInstance.6C7F3A91";

    private readonly LogService _log;
    private readonly CancellationTokenSource _serverCts = new();
    private Mutex? _mutex;
    private Task? _serverTask;
    private bool _acquired;
    private bool _disposed;

    public SingleInstanceService(LogService log)
    {
        _log = log;
    }

    public event Action<string>? CommandReceived;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                _acquired = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous process crashed; we now own the abandoned mutex.
                _acquired = true;
            }

            return _acquired;
        }
        catch (Exception ex)
        {
            _log.Error("创建单实例互斥体失败。", ex);
            return true;
        }
    }

    public void StartPipeServer()
    {
        if (!_acquired || _serverTask is not null)
        {
            return;
        }

        _serverTask = Task.Run(PipeServerLoopAsync);
    }

    public static async Task<bool> SendCommandAsync(string command, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var connectCts = new CancellationTokenSource(remaining);
                await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(command).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(120).ConfigureAwait(false);
            }
        }

        _ = lastError;
        return false;
    }

    private async Task PipeServerLoopAsync()
    {
        var cancellationToken = _serverCts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _log.Info($"收到另一实例的命令：{line}");
                    try
                    {
                        CommandReceived?.Invoke(line.Trim());
                    }
                    catch (Exception ex)
                    {
                        _log.Error("处理单实例命令失败。", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn($"单实例管道服务异常，将重新监听：{ex.Message}");
                try
                {
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
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
            _serverCts.Cancel();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore.
        }

        if (_acquired && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if another thread already released it.
            }
        }

        _mutex?.Dispose();
        _serverCts.Dispose();
    }
}
