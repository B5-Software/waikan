using System.Reflection;

namespace Waikan.Services;

public sealed class AppPaths
{
    public const string AppDisplayName = "这个词在外刊中";
    public const string DataDirectoryEnvironmentVariable = "WAIKAN_DATA_DIR";
    public const string DisableAutostartEnvironmentVariable = "WAIKAN_DISABLE_AUTOSTART";
    public const string Keyword = "外刊";

    public AppPaths(string? rootOverride = null)
    {
        var overrideValue = rootOverride ?? Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        RootDirectory = string.IsNullOrWhiteSpace(overrideValue)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waikan")
            : Path.GetFullPath(overrideValue);
        LogDirectory = Path.Combine(RootDirectory, "logs");
        DatabasePath = Path.Combine(RootDirectory, "waikan.db");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        KeywordsPath = Path.Combine(RootDirectory, "keywords.txt");
        ModelDirectory = Path.Combine(AppContext.BaseDirectory, "models", "kws");
        ExecutablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Waikan.exe");

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        EnsureKeywordFile();
    }

    public string RootDirectory { get; }

    public string LogDirectory { get; }

    public string DatabasePath { get; }

    public string SettingsPath { get; }

    public string KeywordsPath { get; }

    public string ModelDirectory { get; }

    public string ExecutablePath { get; }

    public bool AutostartDisabled =>
        string.Equals(Environment.GetEnvironmentVariable(DisableAutostartEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase);

    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            }

            var plusIndex = version.IndexOf('+');
            return plusIndex >= 0 ? version[..plusIndex] : version;
        }
    }

    public void EnsureKeywordFile()
    {
        // The keyword file is deliberately data, not a compiled-in word list.
        // The KWS model consumes initial/final pinyin tokens for 外刊.
        const string content = "w ài k ān @外刊\n";
        try
        {
            if (!File.Exists(KeywordsPath) || File.ReadAllText(KeywordsPath) != content)
            {
                File.WriteAllText(KeywordsPath, content, new System.Text.UTF8Encoding(false));
            }
        }
        catch
        {
            // The caller will surface a useful engine error if the file remains unusable.
        }
    }

    public bool HasModelFiles()
    {
        return File.Exists(Path.Combine(ModelDirectory, "encoder.int8.onnx"))
               && File.Exists(Path.Combine(ModelDirectory, "decoder.onnx"))
               && File.Exists(Path.Combine(ModelDirectory, "joiner.int8.onnx"))
               && File.Exists(Path.Combine(ModelDirectory, "tokens.txt"));
    }
}
