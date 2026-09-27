using System.IO;

namespace LiveCaptionsTranslator.utils
{
    public static class AppPaths
    {
        private const string DataDirectoryEnvironmentVariable = "LIVECAPTIONS_TRANSLATOR_DATA_DIR";
        private static readonly Lazy<string> ResolvedDataDirectory = new(ResolveDataDirectory);

        public static string DataDirectory
        {
            get => ResolvedDataDirectory.Value;
        }

        public static string SettingFile => Path.Combine(DataDirectory, "setting.json");
        public static string HistoryDatabase => Path.Combine(DataDirectory, "translation_history.db");

        public static void MigrateLegacyFile(string fileName, string destination)
        {
            if (File.Exists(destination))
                return;

            string legacy = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (Path.GetFullPath(legacy).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(legacy))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(legacy, destination, overwrite: false);
        }

        private static string ResolveDataDirectory()
        {
            string? overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                string resolvedOverride = Path.GetFullPath(overrideDirectory);
                Directory.CreateDirectory(resolvedOverride);
                return resolvedOverride;
            }

            string preferred = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveCaptionsTranslator");
            try
            {
                string root = Path.GetPathRoot(preferred)!;
                if (new DriveInfo(root).AvailableFreeSpace >= 16 * 1024 * 1024)
                {
                    Directory.CreateDirectory(preferred);
                    return preferred;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            string fallback = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
