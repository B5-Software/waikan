using Microsoft.Win32;

namespace Waikan.Services;

public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Waikan";

    private readonly AppPaths _paths;
    private readonly LogService _log;

    public AutoStartService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public bool IsDisabledByEnvironment => _paths.AutostartDisabled;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex)
        {
            _log.Error("读取开机自启状态失败。", ex);
            return false;
        }
    }

    public string BuildStartupCommand()
    {
        var command = $"\"{_paths.ExecutablePath}\" --background";
        return command;
    }

    public void SetEnabled(bool enabled)
    {
        if (IsDisabledByEnvironment)
        {
            _log.Info("开机自启被测试环境变量禁用。");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                throw new InvalidOperationException("无法打开当前用户的启动项注册表。");
            }

            if (enabled)
            {
                key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
                _log.Info($"已开启开机自启：{BuildStartupCommand()}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Info("已关闭开机自启。");
            }
        }
        catch (Exception ex)
        {
            _log.Error("设置开机自启失败。", ex);
            throw;
        }
    }

    public void EnsureCurrentPathIfEnabled()
    {
        if (IsDisabledByEnvironment)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var expected = BuildStartupCommand();
            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                SetEnabled(true);
            }
        }
        catch (Exception ex)
        {
            _log.Error("更新开机自启路径失败。", ex);
        }
    }
}
