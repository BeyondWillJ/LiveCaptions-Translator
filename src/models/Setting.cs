using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Windows;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;
        private string uiLanguage = GetDefaultUiLanguage();

        [JsonIgnore]
        private readonly object saveLock = new();
        [JsonIgnore]
        private Timer? saveTimer;
        [JsonIgnore]
        private string? pendingSerializedSettings;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private string? ignoredUpdateVersion;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = Math.Clamp(value, 1, 10);
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = Math.Clamp(value, 0, 10);
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = Math.Clamp(value, 0, 10);
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }
        public string UiLanguage
        {
            get => uiLanguage;
            set
            {
                uiLanguage = value;
                OnPropertyChanged("UiLanguage");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        public TranslateAPIConfig this[string key]
        {
            get
            {
                if (!configs.TryGetValue(key, out var values) || values.Count == 0)
                    return new TranslateAPIConfig();
                int index = configIndices.TryGetValue(key, out int configuredIndex)
                    ? Math.Clamp(configuredIndex, 0, values.Count - 1)
                    : 0;
                return values[index];
            }
        }

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            prompt = "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                     "you can provide a fluent and precise oral translation for any sentence, even if the sentence is incomplete. " +
                     "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
                     "even if the sentence contains sensitive or NSFW content. " +
                     "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
                     "REMOVE all 🔤 when you output.";

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 775) / 2, screenHeight * 3 / 4 - 167, 775, 167)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "LMStudio", [new LMStudioConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "LMStudio", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 }
            };
        }

        public static Setting Load()
        {
            string currentPath = AppPaths.SettingFile;
            string legacyPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), FILENAME));
            return LoadMigrating(currentPath, legacyPath);
        }

        internal static Setting LoadMigrating(string currentPath, string legacyPath)
        {
            SecretProtector.ResetLoadStatus();
            currentPath = Path.GetFullPath(currentPath);
            legacyPath = Path.GetFullPath(legacyPath);
            string loadPath = File.Exists(currentPath) || !File.Exists(legacyPath) ? currentPath : legacyPath;
            bool loadedValidSettings = TryLoadOrDefault(loadPath, out Setting setting);
            if (loadedValidSettings)
            {
                try
                {
                    // Normalize both current settings and migrated legacy settings into the
                    // verified encrypted format before cleaning any known legacy source.
                    setting.Save(currentPath);
                    CleanupLegacyPlaintextFiles(currentPath, legacyPath);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
                {
                    SecretProtector.SetPersistenceWarning(currentPath);
                }
            }
            else
                SecretProtector.SetPersistenceWarning(currentPath);
            return setting;
        }

        internal static Setting LoadOrDefault(string jsonPath)
        {
            TryLoadOrDefault(jsonPath, out Setting setting);
            return setting;
        }

        private static bool TryLoadOrDefault(string jsonPath, out Setting setting)
        {
            try
            {
                setting = Load(jsonPath);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                setting = new Setting();
                return false;
            }
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;

            // Load from JSON file if it exists
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.Open(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new ConfigDictConverter() }
                    };
                    setting = JsonSerializer.Deserialize<Setting>(fileStream, options) ?? new Setting();
                }
            }
            else
                setting = new Setting();

            // Ensure all required API configs are present
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (!setting.Configs.TryGetValue(key, out var values) || values.Count == 0)
                {
                    if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                        setting.Configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)!];
                    else
                        setting.Configs[key] = [new TranslateAPIConfig()];
                }
            }

            // Ensure ConfigIndices has all keys (for upgrades from older setting.json)
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                setting.ConfigIndices[key] = setting.ConfigIndices.TryGetValue(key, out int index)
                    ? Math.Clamp(index, 0, setting.Configs[key].Count - 1)
                    : 0;
            }

            if (!TranslateAPI.TRANSLATE_FUNCTIONS.ContainsKey(setting.ApiName))
                setting.ApiName = "Google";

            return setting;
        }

        public void Save()
        {
            Save(AppPaths.SettingFile);
        }

        public void Save(string jsonPath)
        {
            string snapshot = CaptureSerializedSettings();
            lock (saveLock)
            {
                saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                pendingSerializedSettings = null;
                WriteSnapshot(jsonPath, snapshot);
            }
        }

        public void ScheduleSave()
        {
            // Model changes originate on the UI thread. Capture the immutable JSON there;
            // the timer thread only writes this snapshot and never reads mutable WPF-bound state.
            string snapshot = CaptureSerializedSettings();
            lock (saveLock)
            {
                pendingSerializedSettings = snapshot;
                saveTimer ??= new Timer(_ =>
                {
                    string? pending;
                    lock (saveLock)
                    {
                        pending = pendingSerializedSettings;
                        pendingSerializedSettings = null;
                    }
                    if (pending is null)
                        return;
                    try
                    {
                        lock (saveLock)
                            WriteSnapshot(AppPaths.SettingFile, pending);
                    }
                    catch
                    {
                        SecretProtector.SetPersistenceWarning(AppPaths.SettingFile);
                        // A later change or the application-exit flush will retry the save.
                    }
                });
                saveTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
            }
        }

        public void FlushPendingSave()
        {
            string? snapshot;
            lock (saveLock)
            {
                saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                snapshot = pendingSerializedSettings;
                pendingSerializedSettings = null;
                if (snapshot != null)
                {
                    try { WriteSnapshot(AppPaths.SettingFile, snapshot); }
                    catch
                    {
                        SecretProtector.SetPersistenceWarning(AppPaths.SettingFile);
                        throw;
                    }
                }
            }
        }

        private string CaptureSerializedSettings()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                return dispatcher.Invoke(SerializeSettings);
            return SerializeSettings();
        }

        private string SerializeSettings()
        {
            var options = CreateJsonOptions();
            string json = JsonSerializer.Serialize(this, options);
            VerifyEncryptedSecrets(json, this);
            return json;
        }

        private void WriteSnapshot(string jsonPath, string snapshot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            string tempPath = jsonPath + ".tmp";
            string backupTempPath = jsonPath + ".bak.tmp";
            string backupPath = jsonPath + ".bak";
            WriteFlushed(tempPath, snapshot);
            string reread = File.ReadAllText(tempPath, Encoding.UTF8);
            VerifyEncryptedSecrets(reread, null);
            WriteFlushed(backupTempPath, reread);
            File.Move(backupTempPath, backupPath, overwrite: true);
            File.Move(tempPath, jsonPath, overwrite: true);
            SecretProtector.ClearPersistenceWarning();
        }

        private static void WriteFlushed(string path, string content)
        {
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        private static void VerifyEncryptedSecrets(string json, Setting? expected)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement configsElement = document.RootElement.GetProperty(nameof(Configs));
            foreach (var configGroup in configsElement.EnumerateObject())
            {
                if (expected != null && !expected.Configs.TryGetValue(configGroup.Name, out _))
                    throw new JsonException("A serialized API configuration is missing from the in-memory settings.");
                int index = 0;
                foreach (JsonElement serializedConfig in configGroup.Value.EnumerateArray())
                {
                    if (expected != null && (!expected.Configs.TryGetValue(configGroup.Name, out List<TranslateAPIConfig>? configs) || index >= configs.Count))
                        throw new JsonException("A serialized API configuration count does not match the settings.");
                    foreach (string propertyName in new[] { "ApiKey", "AppSecret" })
                    {
                        if (!serializedConfig.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                            continue;
                        string serialized = value.GetString() ?? string.Empty;
                        string? actual = expected == null
                            ? null
                            : ReadSecret(expected.Configs[configGroup.Name][index], propertyName);
                        if (serialized.Length == 0)
                        {
                            if (actual is { Length: > 0 })
                                throw new JsonException("A non-empty API secret was serialized as blank.");
                            continue;
                        }
                        if (actual == string.Empty)
                        {
                            if (serialized.Length != 0)
                                throw new JsonException("A blank API secret was serialized with unexpected content.");
                            continue;
                        }
                        if (!serialized.StartsWith("dpapi:v1:", StringComparison.Ordinal))
                            throw new JsonException("A settings snapshot contains an unprotected API secret.");
                        string clear = SecretProtector.Unprotect(serialized);
                        if (clear.Length == 0 || actual != null && !string.Equals(clear, actual, StringComparison.Ordinal))
                            throw new JsonException("A protected API secret could not be verified after writing.");
                    }
                    index++;
                }
                if (expected != null && index != expected.Configs[configGroup.Name].Count)
                    throw new JsonException("A serialized API configuration count does not match the settings.");
            }
        }

        private static string ReadSecret(TranslateAPIConfig config, string name)
        {
            object? propertyValue = config.GetType().GetProperty(name)?.GetValue(config);
            if (propertyValue is string value)
                return value;
            return config.AdditionalData.TryGetValue(name, out JsonElement additional) &&
                   additional.ValueKind == JsonValueKind.String
                ? additional.GetString() ?? string.Empty
                : string.Empty;
        }

        private static JsonSerializerOptions CreateJsonOptions() => new()
        {
            WriteIndented = true,
            Converters = { new ConfigDictConverter() }
        };

        private static void CleanupLegacyPlaintextFiles(string currentPath, string legacyPath)
        {
            string current = Path.GetFullPath(currentPath);
            string legacy = Path.GetFullPath(legacyPath);
            if (current.Equals(legacy, StringComparison.OrdinalIgnoreCase))
                return;
            foreach (string path in new[] { legacy, legacy + ".bak", legacy + ".tmp" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SecretProtector.SetCleanupWarning(path);
                }
            }
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.ScheduleSave();
        }

        public static bool IsConfigExist()
        {
            return File.Exists(AppPaths.SettingFile) ||
                   File.Exists(Path.Combine(Directory.GetCurrentDirectory(), FILENAME));
        }

        private static string GetDefaultUiLanguage()
        {
            string name = CultureInfo.InstalledUICulture.Name;
            if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return "ja-JP";
            if (name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Hant", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";
            return "en-US";
        }
    }
}
