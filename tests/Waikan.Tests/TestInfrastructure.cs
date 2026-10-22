using Waikan.Services;

namespace Waikan.Tests;

internal static class TestInfrastructure
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WaikanTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort; test temp directories may still be locked briefly.
        }
    }

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "models", "kws", "encoder.int8.onnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository model directory.");
    }

    public static LogService CreateLog(string rootDirectory)
    {
        var log = new LogService(Path.Combine(rootDirectory, "logs"));
        return log;
    }
}
