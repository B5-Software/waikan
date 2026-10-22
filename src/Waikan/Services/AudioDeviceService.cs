using NAudio.CoreAudioApi;
using Waikan.Models;

namespace Waikan.Services;

public sealed class AudioDeviceService
{
    private readonly LogService _log;

    public AudioDeviceService(LogService log)
    {
        _log = log;
    }

    public IReadOnlyList<AudioDeviceOption> GetDevices(AudioSourceKind source)
    {
        var flow = source == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
        var options = new List<AudioDeviceOption>();
        string? defaultId = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }
            catch (Exception ex)
            {
                _log.Warn($"未找到默认音频设备：{ex.Message}");
            }

            var defaultLabel = source == AudioSourceKind.Microphone
                ? "系统默认麦克风（自动跟随）"
                : "系统默认扬声器（自动跟随）";
            options.Add(new AudioDeviceOption(null, defaultLabel, isDefault: true));

            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    var id = device.ID;
                    var name = SafeFriendlyName(device);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "未命名音频设备";
                    }

                    options.Add(new AudioDeviceOption(id, name, string.Equals(id, defaultId, StringComparison.Ordinal)));
                }
                catch (Exception ex)
                {
                    _log.Warn($"读取音频设备失败：{ex.Message}");
                }
                finally
                {
                    try
                    {
                        device.Dispose();
                    }
                    catch
                    {
                        // Ignore COM cleanup failures.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("枚举音频设备失败。", ex);
        }

        return options
            .OrderByDescending(option => option.Id is null)
            .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string? GetDefaultDeviceId(AudioSourceKind source)
    {
        var flow = source == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取默认音频设备 ID 失败：{ex.Message}");
            return null;
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
}
