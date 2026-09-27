using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using LiveCaptionsTranslator.models;
using Microsoft.Data.Sqlite;

namespace LiveCaptionsTranslator.utils;

public static class SQLiteHistoryLogger
{
    private const int SchemaVersion = 1;
    public static string CONNECTION_STRING { get; }

    static SQLiteHistoryLogger()
    {
        AppPaths.MigrateLegacyFile("translation_history.db", AppPaths.HistoryDatabase);
        CONNECTION_STRING = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDatabase,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        InitializeDatabase();
    }

    private static SqliteConnection OpenConnection() => OpenConnection(CONNECTION_STRING);

    private static SqliteConnection OpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InitializeDatabase()
        => InitializeDatabase(CONNECTION_STRING);

    internal static void InitializeDatabase(string connectionString)
    {
        using var connection = OpenConnection(connectionString);
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );";
            create.ExecuteNonQuery();
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(TranslationHistory);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        AddColumn("SessionId", "TEXT NULL");
        AddColumn("Epoch", "INTEGER NULL");
        AddColumn("SegmentId", "INTEGER NULL");
        AddColumn("Revision", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Status", "TEXT NOT NULL DEFAULT 'Succeeded'");
        AddColumn("ErrorCode", "TEXT NOT NULL DEFAULT ''");
        AddColumn("ErrorMessage", "TEXT NOT NULL DEFAULT ''");

        using (var migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText = @"
                UPDATE TranslationHistory
                    SET Status='SourceOnly'
                    WHERE ApiUsed='LogOnly' AND Status='Succeeded';
                UPDATE TranslationHistory
                    SET Status='Failed', ErrorCode='LegacyFailure',
                        ErrorMessage='The previous translation request failed.'
                    WHERE Status='Succeeded'
                      AND (TranslatedText LIKE '[ERROR]%' OR TranslatedText LIKE '[WARNING]%');
                UPDATE TranslationHistory SET Status='Interrupted' WHERE Status='Pending';
                CREATE UNIQUE INDEX IF NOT EXISTS UX_TranslationHistory_SessionSegment
                    ON TranslationHistory(SessionId, SegmentId)
                    WHERE SessionId IS NOT NULL AND SegmentId IS NOT NULL;
                PRAGMA user_version=1;";
            migrate.ExecuteNonQuery();
        }

        transaction.Commit();

        void AddColumn(string name, string declaration)
        {
            if (columns.Contains(name))
                return;
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE TranslationHistory ADD COLUMN {name} {declaration};";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>Persists the source before translation. Revisions update the same history row.</summary>
    public static async Task<long> SaveSourceAsync(
        Guid sessionId,
        long epoch,
        long segmentId,
        long revision,
        string sourceText,
        string targetLanguage,
        string apiUsed,
        TranslationStatus initialStatus = TranslationStatus.Pending,
        CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(token);
        long? existingId = null;
        long existingRevision = -1;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = @"SELECT Id, Revision FROM TranslationHistory
                WHERE SessionId=@session AND SegmentId=@segment LIMIT 1;";
            find.Parameters.AddWithValue("@session", sessionId.ToString("D"));
            find.Parameters.AddWithValue("@segment", segmentId);
            await using var reader = await find.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                existingId = reader.GetInt64(0);
                existingRevision = reader.GetInt64(1);
            }
        }

        long historyId;
        if (existingId.HasValue)
        {
            historyId = existingId.Value;
            if (revision > existingRevision)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = @"UPDATE TranslationHistory SET
                    Timestamp=@timestamp, SourceText=@source, TranslatedText='', TargetLanguage=@language,
                    ApiUsed=@api, Epoch=@epoch, Revision=@revision, Status=@status,
                    ErrorCode='', ErrorMessage=''
                    WHERE Id=@id AND Revision < @revision;";
                AddSourceParameters(update, sessionId, epoch, revision, sourceText,
                    targetLanguage, apiUsed, initialStatus);
                update.Parameters.AddWithValue("@id", historyId);
                await update.ExecuteNonQueryAsync(token);
            }
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = @"INSERT INTO TranslationHistory
                (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                 SessionId, Epoch, SegmentId, Revision, Status, ErrorCode, ErrorMessage)
                VALUES (@timestamp, @source, '', @language, @api,
                 @session, @epoch, @segment, @revision, @status, '', '')
                RETURNING Id;";
            AddSourceParameters(insert, sessionId, epoch, revision, sourceText,
                targetLanguage, apiUsed, initialStatus);
            insert.Parameters.AddWithValue("@segment", segmentId);
            historyId = Convert.ToInt64(await insert.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(token);
        return historyId;
    }

    public static async Task<bool> CompleteTranslationAsync(
        long historyId,
        Guid sessionId,
        long segmentId,
        long revision,
        TranslationOutcome outcome,
        CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET
            TranslatedText=@translated, Status=@status, ErrorCode=@code, ErrorMessage=@message
            WHERE Id=@id AND SessionId=@session AND SegmentId=@segment AND Revision=@revision
              AND Status='Pending';";
        command.Parameters.AddWithValue("@translated", outcome.Status == TranslationStatus.Succeeded
            ? outcome.Text : string.Empty);
        command.Parameters.AddWithValue("@status", outcome.Status.ToString());
        command.Parameters.AddWithValue("@code", outcome.ErrorCode);
        command.Parameters.AddWithValue("@message", outcome.Diagnostic);
        command.Parameters.AddWithValue("@id", historyId);
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("@segment", segmentId);
        command.Parameters.AddWithValue("@revision", revision);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public static async Task<bool> MarkPendingAsync(
        long historyId,
        Guid sessionId,
        long segmentId,
        long revision,
        TranslationStatus status,
        string errorCode = "",
        string errorMessage = "",
        CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET Status=@status, ErrorCode=@code,
            ErrorMessage=@message, TranslatedText=''
            WHERE Id=@id AND SessionId=@session AND SegmentId=@segment AND Revision=@revision
              AND Status='Pending';";
        command.Parameters.AddWithValue("@status", status.ToString());
        command.Parameters.AddWithValue("@code", errorCode);
        command.Parameters.AddWithValue("@message", errorMessage);
        command.Parameters.AddWithValue("@id", historyId);
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("@segment", segmentId);
        command.Parameters.AddWithValue("@revision", revision);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public static async Task<bool> MarkSourceOnlyAsync(
        long historyId,
        Guid sessionId,
        long segmentId,
        long revision,
        string reason,
        CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET Status='SourceOnly',
            ErrorCode=@reason, ErrorMessage='', TranslatedText=''
            WHERE Id=@id AND SessionId=@session AND SegmentId=@segment AND Revision=@revision
              AND Status IN ('Pending','Succeeded','Failed');";
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@id", historyId);
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("@segment", segmentId);
        command.Parameters.AddWithValue("@revision", revision);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public static async Task<int> MarkSessionPendingSourceOnlyAsync(
        Guid sessionId, CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET Status='SourceOnly',
            TranslatedText='', ErrorCode='', ErrorMessage=''
            WHERE SessionId=@session AND Status='Pending';";
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        return await command.ExecuteNonQueryAsync(token);
    }

    public static async Task<int> MarkPendingInterruptedAsync(CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET Status='Interrupted',
            ErrorCode='Interrupted', ErrorMessage='Translation did not finish before the previous run ended.'
            WHERE Status='Pending';";
        return await command.ExecuteNonQueryAsync(token);
    }

    public static Task LogTranslation(string sourceText, string translatedText,
        string targetLanguage, string apiUsed, CancellationToken token = default) =>
        LogTranslation(sourceText, translatedText, targetLanguage, apiUsed, false, token);

    public static async Task LogTranslation(string sourceText, string translatedText,
        string targetLanguage, string apiUsed, bool overwrite, CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(token);
        if (overwrite)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM TranslationHistory WHERE Id=(SELECT MAX(Id) FROM TranslationHistory);";
            await delete.ExecuteNonQueryAsync(token);
        }

        (TranslationStatus status, string code, string message) = LegacyResult(translatedText, apiUsed);
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = @"INSERT INTO TranslationHistory
            (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed, Status, ErrorCode, ErrorMessage)
            VALUES (@timestamp, @source, @translated, @language, @api, @status, @code, @message);";
        insert.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue("@source", sourceText);
        insert.Parameters.AddWithValue("@translated", status == TranslationStatus.Succeeded ? translatedText : string.Empty);
        insert.Parameters.AddWithValue("@language", targetLanguage);
        insert.Parameters.AddWithValue("@api", apiUsed);
        insert.Parameters.AddWithValue("@status", status.ToString());
        insert.Parameters.AddWithValue("@code", code);
        insert.Parameters.AddWithValue("@message", message);
        await insert.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
        int page, int maxRow, string searchText, CancellationToken token = default)
    {
        HistoryPageResult result = await LoadHistoryPageAsync(page, maxRow, searchText, null, token);
        return (result.Rows, result.MaxPage);
    }

    public static async Task<HistoryPageResult> LoadHistoryPageAsync(
        int page, int maxRow, string searchText, string? statusFilter = null,
        CancellationToken token = default)
    {
        maxRow = Math.Clamp(maxRow, 1, 1000);
        await using var connection = OpenConnection();
        string search = $"%{searchText}%";
        int totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = @"SELECT COUNT(*) FROM TranslationHistory
                WHERE (SourceText LIKE @search OR TranslatedText LIKE @search OR
                       ErrorCode LIKE @search OR ErrorMessage LIKE @search)
                  AND (@status='' OR Status=@status);";
            count.Parameters.AddWithValue("@search", search);
            count.Parameters.AddWithValue("@status", NormalizeFilter(statusFilter));
            totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
        int actualPage = Math.Clamp(page, 1, maxPage);
        int offset = (actualPage - 1) * maxRow;
        var rows = new List<TranslationHistoryEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                    SessionId, Epoch, SegmentId, Revision, Status, ErrorCode, ErrorMessage
            FROM TranslationHistory
            WHERE (SourceText LIKE @search OR TranslatedText LIKE @search OR
                   ErrorCode LIKE @search OR ErrorMessage LIKE @search)
              AND (@status='' OR Status=@status)
            ORDER BY Id DESC LIMIT @limit OFFSET @offset;";
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@status", NormalizeFilter(statusFilter));
        command.Parameters.AddWithValue("@limit", maxRow);
        command.Parameters.AddWithValue("@offset", offset);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(ReadEntry(reader));
        return new HistoryPageResult(rows, maxPage, actualPage, totalCount);
    }

    public static async Task ClearHistory(CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory';";
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public static async Task<string> LoadLastSourceText(CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceText FROM TranslationHistory ORDER BY Id DESC LIMIT 1;";
        object? result = await command.ExecuteScalarAsync(token);
        return result as string ?? string.Empty;
    }

    public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                SessionId, Epoch, SegmentId, Revision, Status, ErrorCode, ErrorMessage
            FROM TranslationHistory ORDER BY Id DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadEntry(reader) : null;
    }

    public static async Task DeleteLastTranslation(CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TranslationHistory WHERE Id=(SELECT MAX(Id) FROM TranslationHistory);";
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task ExportToCSV(string filePath, CancellationToken token = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                Status, ErrorCode, ErrorMessage
            FROM TranslationHistory ORDER BY Id DESC;";
        await using var reader = await command.ExecuteReaderAsync(token);
        await using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteField("Timestamp");
        csv.WriteField("SourceText");
        csv.WriteField("TranslatedText");
        csv.WriteField("TargetLanguage");
        csv.WriteField("ApiUsed");
        csv.WriteField("Status");
        csv.WriteField("ErrorCode");
        csv.WriteField("ErrorMessage");
        await csv.NextRecordAsync();

        while (await reader.ReadAsync(token))
        {
            DateTime timestamp = ParseTimestamp(reader.GetString(reader.GetOrdinal("Timestamp")));
            csv.WriteField(timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            csv.WriteField(ReadString(reader, "SourceText"));
            csv.WriteField(ReadString(reader, "TranslatedText"));
            csv.WriteField(ReadString(reader, "TargetLanguage"));
            csv.WriteField(ReadString(reader, "ApiUsed"));
            csv.WriteField(ReadString(reader, "Status"));
            csv.WriteField(ReadString(reader, "ErrorCode"));
            csv.WriteField(ReadString(reader, "ErrorMessage"));
            await csv.NextRecordAsync();
            token.ThrowIfCancellationRequested();
        }
        await writer.FlushAsync(token);
    }

    private static void AddSourceParameters(SqliteCommand command, Guid sessionId, long epoch,
        long revision, string sourceText, string targetLanguage, string apiUsed, TranslationStatus status)
    {
        command.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@source", sourceText);
        command.Parameters.AddWithValue("@language", targetLanguage);
        command.Parameters.AddWithValue("@api", apiUsed);
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("@epoch", epoch);
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.AddWithValue("@status", status.ToString());
    }

    private static (TranslationStatus Status, string Code, string Message) LegacyResult(string translated, string api)
    {
        if (api == "LogOnly")
            return (TranslationStatus.SourceOnly, string.Empty, string.Empty);
        if (translated.StartsWith("[ERROR]", StringComparison.Ordinal) ||
            translated.StartsWith("[WARNING]", StringComparison.Ordinal))
            return (TranslationStatus.Failed, "LegacyFailure", "The previous translation request failed.");
        return (TranslationStatus.Succeeded, string.Empty, string.Empty);
    }

    private static string NormalizeFilter(string? status) =>
        string.IsNullOrWhiteSpace(status) || status.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : status;

    private static TranslationHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        DateTime localTime = ParseTimestamp(ReadString(reader, "Timestamp"));
        string session = ReadString(reader, "SessionId");
        return new TranslationHistoryEntry
        {
            HistoryId = reader.GetInt64(reader.GetOrdinal("Id")),
            SessionId = Guid.TryParse(session, out Guid guid) ? guid : null,
            Epoch = NullableInt64(reader, "Epoch"),
            SegmentId = NullableInt64(reader, "SegmentId") ?? 0,
            Revision = NullableInt64(reader, "Revision") ?? 0,
            Timestamp = localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            SourceText = ReadString(reader, "SourceText"),
            TranslatedText = ReadString(reader, "TranslatedText"),
            TargetLanguage = ReadString(reader, "TargetLanguage"),
            ApiUsed = ReadString(reader, "ApiUsed"),
            Status = ReadString(reader, "Status"),
            ErrorCode = ReadString(reader, "ErrorCode"),
            ErrorMessage = ReadString(reader, "ErrorMessage")
        };
    }

    private static long? NullableInt64(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static DateTime ParseTimestamp(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixTime))
            return DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime legacy))
            return legacy;
        return DateTime.UnixEpoch.ToLocalTime();
    }

    public sealed record HistoryPageResult(
        List<TranslationHistoryEntry> Rows, int MaxPage, int ActualPage, int TotalCount);
}
