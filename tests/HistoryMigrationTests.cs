using Microsoft.Data.Sqlite;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class HistoryMigrationTests
{
    [Fact]
    public async Task LegacyDatabase_MigratesStatusesWithoutRewritingExistingContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"history-migration-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE TranslationHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp TEXT, SourceText TEXT, TranslatedText TEXT,
                        TargetLanguage TEXT, ApiUsed TEXT);
                    INSERT INTO TranslationHistory VALUES (1, 't1', 'normal source', 'normal translation', 'zh-CN', 'OpenAI');
                    INSERT INTO TranslationHistory VALUES (2, 't2', 'old source', '[ERROR] legacy detail', 'zh-CN', 'Google');
                    INSERT INTO TranslationHistory VALUES (3, 't3', 'paused source', '', 'zh-CN', 'LogOnly');";
                await create.ExecuteNonQueryAsync();
            }

            SQLiteHistoryLogger.InitializeDatabase(connectionString);
            SQLiteHistoryLogger.InitializeDatabase(connectionString);

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"SELECT Id, SourceText, TranslatedText, ApiUsed, Status, ErrorCode
                    FROM TranslationHistory ORDER BY Id;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("normal source", reader.GetString(1));
                Assert.Equal("normal translation", reader.GetString(2));
                Assert.Equal("Succeeded", reader.GetString(4));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("old source", reader.GetString(1));
                Assert.Equal("[ERROR] legacy detail", reader.GetString(2));
                Assert.Equal("Failed", reader.GetString(4));
                Assert.Equal("LegacyFailure", reader.GetString(5));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("paused source", reader.GetString(1));
                Assert.Equal("LogOnly", reader.GetString(3));
                Assert.Equal("SourceOnly", reader.GetString(4));
                Assert.False(await reader.ReadAsync());

                await using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(1L, Convert.ToInt64(await version.ExecuteScalarAsync()));

                await using var insert = connection.CreateCommand();
                insert.CommandText = @"INSERT INTO TranslationHistory
                    (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed, SessionId, SegmentId)
                    VALUES ('now', 'one', '', 'zh-CN', 'OpenAI', 'session', 7);";
                await insert.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        }
    }
}
