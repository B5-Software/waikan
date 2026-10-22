using Waikan.Services;

namespace Waikan.Tests;

public sealed class AppPathsAndAutoStartTests
{
    [Fact]
    public void Keyword_File_Is_Created_With_Canonical_Content()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            Assert.True(File.Exists(paths.KeywordsPath));
            Assert.Equal("w ài k ān @外刊\n", File.ReadAllText(paths.KeywordsPath));
            Assert.Equal(Path.Combine(root, "waikan.db"), paths.DatabasePath);
            Assert.Equal(Path.Combine(root, "logs"), paths.LogDirectory);
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Startup_Command_Is_Quoted_And_Starts_In_Background()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var service = new AutoStartService(paths, TestInfrastructure.CreateLog(root));
            var command = service.BuildStartupCommand();

            Assert.StartsWith("\"", command);
            Assert.Contains("\" --background", command);
            Assert.Contains(".exe", command, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }
}
