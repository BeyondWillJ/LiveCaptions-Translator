using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.models;
using Microsoft.Data.Sqlite;

namespace LiveCaptionsTranslator.Tests;

public class HistoryLoggerTests
{
    [Fact]
    public async Task ConcurrentWritesAndReads_DoNotShareAnActiveConnection()
    {
        await SQLiteHistoryLogger.ClearHistory();

        Task[] writes = Enumerable.Range(0, 40)
            .Select(index => SQLiteHistoryLogger.LogTranslation(
                $"source-{index}", $"translated-{index}", "zh-CN", "Test"))
            .ToArray();
        await Task.WhenAll(writes);

        var (rows, maxPage) = await SQLiteHistoryLogger.LoadHistoryAsync(1, 100, string.Empty);
        Assert.Equal(40, rows.Count);
        Assert.Equal(1, maxPage);
    }

    [Fact]
    public async Task Overwrite_IsAtomicAndKeepsOneRow()
    {
        await SQLiteHistoryLogger.ClearHistory();
        await SQLiteHistoryLogger.LogTranslation("draft", "old", "zh-CN", "Test");
        await SQLiteHistoryLogger.LogTranslation("final", "new", "zh-CN", "Test", overwrite: true);

        var (rows, _) = await SQLiteHistoryLogger.LoadHistoryAsync(1, 10, string.Empty);
        Assert.Single(rows);
        Assert.Equal("final", rows[0].SourceText);
    }

    [Fact]
    public async Task SourceFirst_RevisionsAndLateResponsesStayOnOneCurrentRow()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        long id = await SQLiteHistoryLogger.SaveSourceAsync(session, 2, 41, 0,
            "first draft", "zh-CN", "OpenAI");

        Assert.True(await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 41, 0,
            new TranslationOutcome(TranslationStatus.Succeeded, "old translation", "", "", 20)));

        long revisedId = await SQLiteHistoryLogger.SaveSourceAsync(session, 2, 41, 1,
            "corrected final", "zh-CN", "OpenAI");
        Assert.Equal(id, revisedId);
        Assert.False(await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 41, 0,
            new TranslationOutcome(TranslationStatus.Succeeded, "late old translation", "", "", 20)));
        Assert.True(await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 41, 1,
            new TranslationOutcome(TranslationStatus.Succeeded, "current translation", "", "", 20)));

        var (rows, _) = await SQLiteHistoryLogger.LoadHistoryAsync(1, 10, string.Empty);
        Assert.Single(rows);
        Assert.Equal("corrected final", rows[0].SourceText);
        Assert.Equal("current translation", rows[0].TranslatedText);
        Assert.Equal("Succeeded", rows[0].Status);
        Assert.Equal(1, rows[0].Revision);
    }

    [Fact]
    public async Task PauseMarksPendingSourceOnlyAndRejectsLateTranslation()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        long id = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, 17, 0,
            "source kept when paused", "zh-CN", "OpenAI");

        Assert.Equal(1, await SQLiteHistoryLogger.MarkSessionPendingSourceOnlyAsync(session));
        Assert.False(await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 17, 0,
            new TranslationOutcome(TranslationStatus.Succeeded, "late translation", "", "", 10)));

        var result = await SQLiteHistoryLogger.LoadHistoryPageAsync(1, 10, string.Empty, "SourceOnly");
        Assert.Single(result.Rows);
        Assert.Equal("source kept when paused", result.Rows[0].SourceText);
        Assert.Empty(result.Rows[0].TranslatedText);
        Assert.Equal("SourceOnly", result.Rows[0].Status);
    }

    [Fact]
    public async Task PauseThatWinsCompletionRace_ReclassifiesSameRevisionAsSourceOnly()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        long id = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, 18, 2,
            "source completed as pause arrived", "zh-CN", "OpenAI");
        Assert.True(await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 18, 2,
            new TranslationOutcome(TranslationStatus.Succeeded, "translation", "", "", 20)));

        Assert.True(await SQLiteHistoryLogger.MarkSourceOnlyAsync(id, session, 18, 2, "Paused"));

        var page = await SQLiteHistoryLogger.LoadHistoryPageAsync(1, 10, string.Empty, "SourceOnly");
        var row = Assert.Single(page.Rows);
        Assert.Equal("source completed as pause arrived", row.SourceText);
        Assert.Empty(row.TranslatedText);
        Assert.Equal("SourceOnly", row.Status);
    }

    [Fact]
    public async Task SkippedAndSourceOnlyRowsKeepOriginalWithoutTranslation()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        long skipped = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, 1, 0,
            "kept source", "zh-CN", "OpenAI");
        Assert.True(await SQLiteHistoryLogger.MarkPendingAsync(skipped, session, 1, 0,
            TranslationStatus.Skipped, "QueueFull", "The pending queue is full."));
        long sourceOnly = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, 2, 0,
            "paused source", "zh-CN", "OpenAI", TranslationStatus.SourceOnly);

        var result = await SQLiteHistoryLogger.LoadHistoryPageAsync(1, 10, string.Empty, "Skipped");
        Assert.Single(result.Rows);
        Assert.Equal("kept source", result.Rows[0].SourceText);
        Assert.Empty(result.Rows[0].TranslatedText);
        Assert.Equal("QueueFull", result.Rows[0].ErrorCode);
        Assert.Equal("Skipped", result.Rows[0].Status);

        var (_, allPages) = await SQLiteHistoryLogger.LoadHistoryAsync(1, 10, string.Empty);
        Assert.Equal(1, allPages);
        Assert.True(sourceOnly > skipped);
    }

    [Fact]
    public async Task StatusFilter_ClampsRequestedPageToFilteredMaximum()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        for (int index = 1; index <= 5; index++)
        {
            long id = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, index, 0,
                $"source-{index}", "zh-CN", "OpenAI");
            TranslationOutcome outcome = index <= 3
                ? new TranslationOutcome(TranslationStatus.Succeeded, $"translation-{index}", "", "", 1)
                : new TranslationOutcome(TranslationStatus.Failed, "", "Timeout", "Timed out.", 1);
            Assert.True(await SQLiteHistoryLogger.CompleteTranslationAsync(
                id, session, index, 0, outcome));
        }

        var page = await SQLiteHistoryLogger.LoadHistoryPageAsync(
            page: 99, maxRow: 2, searchText: string.Empty, statusFilter: "Succeeded");

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.MaxPage);
        Assert.Equal(2, page.ActualPage);
        Assert.Single(page.Rows);
        Assert.Equal("Succeeded", page.Rows[0].Status);
    }

    [Fact]
    public async Task CsvExport_PreservesLegacyColumnsAndAppendsStatusFields()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid session = Guid.NewGuid();
        long id = await SQLiteHistoryLogger.SaveSourceAsync(session, 0, 1, 0,
            "source", "zh-CN", "OpenAI");
        await SQLiteHistoryLogger.CompleteTranslationAsync(id, session, 1, 0,
            new TranslationOutcome(TranslationStatus.Failed, "", "Timeout", "Request timed out.", 8000));
        string path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.csv");
        try
        {
            await SQLiteHistoryLogger.ExportToCSV(path);
            string header = File.ReadLines(path).First().TrimStart('\uFEFF');
            Assert.Equal("Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed,Status,ErrorCode,ErrorMessage", header);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CsvExport_WritesAllRowsFromLargeHistory()
    {
        await SQLiteHistoryLogger.ClearHistory();
        await using (var connection = new SqliteConnection(SQLiteHistoryLogger.CONNECTION_STRING))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                WITH RECURSIVE numbers(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 2000
                )
                INSERT INTO TranslationHistory
                    (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                     Status, ErrorCode, ErrorMessage)
                SELECT '2026-01-01 00:00:00', 'source-' || value, 'translation-' || value,
                       'zh-CN', 'Test', 'Succeeded', '', ''
                FROM numbers;";
            await command.ExecuteNonQueryAsync();
        }

        string path = Path.Combine(Path.GetTempPath(), $"history-large-{Guid.NewGuid():N}.csv");
        try
        {
            await SQLiteHistoryLogger.ExportToCSV(path);
            Assert.Equal(2001, File.ReadLines(path).Count());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
