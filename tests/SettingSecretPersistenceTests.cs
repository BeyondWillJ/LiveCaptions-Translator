using System.Text.Json.Nodes;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class SettingSecretPersistenceTests
{
    [Fact]
    public void Save_ProtectsSecretsAndEncryptedBackupRoundTrips()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        const string secret = "test-key-do-not-write-plain";
        try
        {
            var setting = CreateSetting(secret);
            setting.Save(path);

            string json = File.ReadAllText(path);
            string backup = File.ReadAllText(path + ".bak");
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, backup, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", json, StringComparison.Ordinal);
            Assert.Equal(secret, ReadOpenAiKey(Setting.Load(path)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_ProtectsAppSecretAndBackupRoundTrips()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        const string secret = "youdao-app-secret-do-not-write-plain";
        try
        {
            var setting = new Setting();
            ((YoudaoConfig)setting.Configs["Youdao"][0]).AppSecret = secret;
            setting.Save(path);

            string json = File.ReadAllText(path);
            string backup = File.ReadAllText(path + ".bak");
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, backup, StringComparison.Ordinal);
            Assert.StartsWith("dpapi:v1:", JsonNode.Parse(json)!["Configs"]!["Youdao"]![0]!["AppSecret"]!.GetValue<string>());
            Assert.Equal(secret, ((YoudaoConfig)Setting.Load(path).Configs["Youdao"][0]).AppSecret);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Load_LegacyPlaintextSecretCanBeResavedEncrypted()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        const string secret = "legacy-secret";
        try
        {
            CreateSetting("temporary").Save(path);
            ReplaceOpenAiKey(path, secret);

            SecretProtector.ResetLoadStatus();
            Setting loaded = Setting.Load(path);
            Assert.Equal(secret, ReadOpenAiKey(loaded));
            Assert.True(SecretProtector.NeedsMigration);

            loaded.Save(path);
            Assert.DoesNotContain(secret, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(path + ".bak"), StringComparison.Ordinal);
            Assert.Equal(secret, ReadOpenAiKey(Setting.Load(path)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LoadMigrating_EncryptsCurrentFileAndBackupBeforeRemovingLegacyFiles()
    {
        string directory = CreateDirectory();
        string legacyDirectory = Path.Combine(directory, "legacy");
        string currentDirectory = Path.Combine(directory, "current");
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(currentDirectory);
        string legacyPath = Path.Combine(legacyDirectory, Setting.FILENAME);
        string currentPath = Path.Combine(currentDirectory, Setting.FILENAME);
        const string secret = "legacy-migration-secret";
        try
        {
            CreateSetting("temporary").Save(legacyPath);
            ReplaceOpenAiKey(legacyPath, secret);
            ReplaceOpenAiKey(legacyPath + ".bak", secret);
            File.Copy(legacyPath, legacyPath + ".tmp");
            ReplaceOpenAiKey(legacyPath + ".tmp", secret);

            Setting migrated = Setting.LoadMigrating(currentPath, legacyPath);

            Assert.Equal(secret, ReadOpenAiKey(migrated));
            Assert.False(File.Exists(legacyPath));
            Assert.False(File.Exists(legacyPath + ".bak"));
            Assert.False(File.Exists(legacyPath + ".tmp"));

            string currentJson = File.ReadAllText(currentPath);
            string backupJson = File.ReadAllText(currentPath + ".bak");
            Assert.DoesNotContain(secret, currentJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, backupJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", currentJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", backupJson, StringComparison.Ordinal);
            Assert.Equal(secret, ReadOpenAiKey(Setting.Load(currentPath)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LoadMigrating_PreservesCorruptCurrentFileAndEncryptedBackup()
    {
        string directory = CreateDirectory();
        string currentDirectory = Path.Combine(directory, "current");
        string legacyDirectory = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(legacyDirectory);
        string currentPath = Path.Combine(currentDirectory, Setting.FILENAME);
        string legacyPath = Path.Combine(legacyDirectory, Setting.FILENAME);
        const string secret = "backup-secret-must-stay-encrypted";
        const string corruptJson = "{ definitely-not-json";
        try
        {
            CreateSetting(secret).Save(currentPath);
            byte[] encryptedBackup = File.ReadAllBytes(currentPath + ".bak");
            File.WriteAllText(currentPath, corruptJson);

            Setting loaded = Setting.LoadMigrating(currentPath, legacyPath);

            Assert.Equal("Google", loaded.ApiName);
            Assert.Equal(corruptJson, File.ReadAllText(currentPath));
            Assert.Equal(encryptedBackup, File.ReadAllBytes(currentPath + ".bak"));
            Assert.DoesNotContain(secret, File.ReadAllText(currentPath + ".bak"), StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", File.ReadAllText(currentPath + ".bak"), StringComparison.Ordinal);
            Assert.False(File.Exists(legacyPath));
            Assert.Empty(Directory.GetFiles(currentDirectory, "setting.json.corrupt-*"));
            Assert.False(File.Exists(currentPath + ".tmp"));
            Assert.False(File.Exists(currentPath + ".bak.tmp"));
            Assert.Equal(currentPath, SecretProtector.PersistenceWarning);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Load_InvalidCipherClearsOnlySecretAndReportsFailure()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        try
        {
            Setting setting = CreateSetting("temporary");
            ((OpenAIConfig)setting.Configs["OpenAI"][0]).ModelName = "keep-this-model";
            setting.Save(path);
            ReplaceOpenAiKey(path, "dpapi:v1:not-valid-ciphertext");

            SecretProtector.ResetLoadStatus();
            Setting loaded = Setting.Load(path);
            Assert.Empty(ReadOpenAiKey(loaded));
            Assert.Equal("keep-this-model", ((OpenAIConfig)loaded.Configs["OpenAI"][0]).ModelName);
            Assert.True(SecretProtector.DecryptionFailed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_WhenReplacingAnOpenSettingsFileFails_PreservesOriginalAndEncryptedBackup()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        const string originalSecret = "original-secret";
        const string replacementSecret = "replacement-secret";
        try
        {
            CreateSetting(originalSecret).Save(path);
            byte[] original = File.ReadAllBytes(path);
            var replacement = CreateSetting(replacementSecret);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<UnauthorizedAccessException>(() => replacement.Save(path));
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            string backup = File.ReadAllText(path + ".bak");
            Assert.DoesNotContain(originalSecret, backup, StringComparison.Ordinal);
            Assert.DoesNotContain(replacementSecret, backup, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", backup, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RemovedProviderSelection_FallsBackButPreservesLegacyConfigurationData()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "setting.json");
        try
        {
            CreateSetting("unused").Save(path);
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["ApiName"] = "Google2";
            root["Configs"]!["Google2"] = new JsonArray(new JsonObject
            {
                ["ApiKey"] = "legacy-provider-key",
                ["ApiUrl"] = "https://legacy.example/translate",
                ["CustomSetting"] = "preserve-this"
            });
            root["ConfigIndices"]!["Google2"] = 3;
            File.WriteAllText(path, root.ToJsonString());

            Setting loaded = Setting.Load(path);
            Assert.Equal("Google", loaded.ApiName);
            Assert.True(loaded.Configs.ContainsKey("Google2"));
            Assert.Equal("preserve-this", loaded.Configs["Google2"][0].AdditionalData["CustomSetting"].GetString());
            Assert.Equal(3, loaded.ConfigIndices["Google2"]);

            loaded.Save(path);
            JsonObject saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.StartsWith("dpapi:v1:", saved["Configs"]!["Google2"]![0]!["ApiKey"]!.GetValue<string>());
            Assert.Equal("preserve-this", saved["Configs"]!["Google2"]![0]!["CustomSetting"]!.GetValue<string>());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Setting CreateSetting(string key)
    {
        var setting = new Setting();
        ((OpenAIConfig)setting.Configs["OpenAI"][0]).ApiKey = key;
        return setting;
    }

    private static string ReadOpenAiKey(Setting setting) =>
        ((OpenAIConfig)setting.Configs["OpenAI"][0]).ApiKey;

    private static void ReplaceOpenAiKey(string path, string value)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["Configs"]!["OpenAI"]![0]!["ApiKey"] = value;
        File.WriteAllText(path, root.ToJsonString());
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"setting-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
