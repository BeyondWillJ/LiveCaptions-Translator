using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class AppPathsTests
{
    [Fact]
    public void MigrateLegacyFile_CopiesExistingFileWithoutOverwritingDestination()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        string legacyDirectory = Path.Combine(root, "legacy");
        string destination = Path.Combine(root, "current", "setting.json");
        Directory.CreateDirectory(legacyDirectory);
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            File.WriteAllText(Path.Combine(legacyDirectory, "setting.json"), "legacy");
            Directory.SetCurrentDirectory(legacyDirectory);

            AppPaths.MigrateLegacyFile("setting.json", destination);
            Assert.Equal("legacy", File.ReadAllText(destination));

            File.WriteAllText(destination, "current");
            AppPaths.MigrateLegacyFile("setting.json", destination);
            Assert.Equal("current", File.ReadAllText(destination));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Directory.Delete(root, recursive: true);
        }
    }
}
