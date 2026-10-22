using System.Text.Json;
using System.Text.Json.Serialization;
using Waikan.Models;

namespace Waikan.Services;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SettingsService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_paths.SettingsPath))
            {
                return CreateDefault();
            }

            var json = File.ReadAllText(_paths.SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            if (settings is null)
            {
                _log.Warn("settings.json 为空或无法解析，已使用默认设置。");
                return CreateDefault();
            }

            settings.Normalize();
            _log.Info("已加载设置。");
            return settings;
        }
        catch (Exception ex)
        {
            _log.Error("读取设置失败，已备份损坏文件并使用默认设置。", ex);
            TryBackupCorruptSettings();
            return CreateDefault();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings.Normalize();
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            var tempPath = _paths.SettingsPath + ".tmp";
            var backupPath = _paths.SettingsPath + ".bak";

            await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(_paths.SettingsPath))
            {
                File.Replace(tempPath, _paths.SettingsPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _paths.SettingsPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("保存设置失败。", ex);
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            _saveGate.Wait();
            try
            {
                settings.Normalize();
                var json = JsonSerializer.Serialize(settings, _jsonOptions);
                var tempPath = _paths.SettingsPath + ".tmp";
                File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(_paths.SettingsPath))
                {
                    File.Replace(tempPath, _paths.SettingsPath, _paths.SettingsPath + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _paths.SettingsPath);
                }
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Error("同步保存设置失败。", ex);
        }
    }

    private AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    private void TryBackupCorruptSettings()
    {
        try
        {
            if (File.Exists(_paths.SettingsPath))
            {
                var backup = _paths.SettingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(_paths.SettingsPath, backup, overwrite: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
