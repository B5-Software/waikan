using Waikan.Models;
using Waikan.Services;
using Microsoft.Data.Sqlite;

namespace Waikan.Tests;

public sealed class DatabaseServiceTests
{
    [Fact]
    public async Task Insert_Query_And_DailyCount_Work()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var localOffset = TimeSpan.FromHours(8);
            var firstLocal = new DateTimeOffset(2026, 10, 22, 8, 0, 0, localOffset);
            var secondLocal = new DateTimeOffset(2026, 10, 22, 8, 0, 30, localOffset);
            var boundaryLocal = new DateTimeOffset(2026, 10, 22, 8, 1, 0, localOffset);

            await database.InsertOccurrenceAsync(
                firstLocal.ToUniversalTime(), firstLocal, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);
            await database.InsertOccurrenceAsync(
                secondLocal.ToUniversalTime(), secondLocal, OccurrenceRecord.AudioCaptureSourceSystem, AppPaths.Keyword);
            await database.InsertOccurrenceAsync(
                boundaryLocal.ToUniversalTime(), boundaryLocal, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);

            Assert.Equal(3, await database.GetCountForDayAsync("2026-10-22"));

            var records = await database.GetOccurrencesAsync(
                new DateTimeOffset(2026, 10, 22, 8, 0, 0, localOffset),
                new DateTimeOffset(2026, 10, 22, 8, 0, 59, localOffset));

            Assert.Equal(2, records.Count);
            Assert.Equal("microphone", records[0].Source);
            Assert.Equal(OccurrenceRecord.AudioCaptureSourceMicrophone, records[0].Source);
            Assert.Equal(new DateTimeOffset(2026, 10, 22, 8, 0, 0, localOffset), records[0].LocalTime);
            Assert.Equal(TimeSpan.FromHours(8), records[0].LocalTime.Offset);

            var otherDay = new DateTimeOffset(2026, 10, 21, 23, 59, 59, localOffset);
            await database.InsertOccurrenceAsync(
                otherDay.ToUniversalTime(), otherDay, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);

            var counts = await database.GetDailyCountsAsync("2026-10-21", "2026-10-22");
            Assert.Equal(new[]
            {
                new DailyCount("2026-10-21", 1),
                new DailyCount("2026-10-22", 3),
            }, counts);

            Assert.Equal(4, await database.GetTotalCountAsync());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Delete_By_Id_And_Range_Works()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var offset = TimeSpan.FromHours(8);
            var firstLocal = new DateTimeOffset(2026, 10, 22, 9, 0, 0, offset);
            var first = await database.InsertOccurrenceAsync(
                firstLocal.ToUniversalTime(), firstLocal, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);

            var secondLocal = firstLocal.AddHours(1);
            await database.InsertOccurrenceAsync(
                secondLocal.ToUniversalTime(), secondLocal, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);

            Assert.True(await database.DeleteOccurrenceAsync(first.Id));
            Assert.Equal(1, await database.GetCountForDayAsync("2026-10-22"));
            Assert.False(await database.DeleteOccurrenceAsync(first.Id));

            var deleted = await database.DeleteRangeAsync(
                new DateTimeOffset(2026, 10, 22, 0, 0, 0, offset),
                new DateTimeOffset(2026, 10, 22, 23, 59, 59, offset));
            Assert.Equal(1, deleted);
            Assert.Equal(0, await database.GetTotalCountAsync());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Concurrent_Inserts_Are_Serialized_By_Sqlite()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var offset = TimeSpan.FromHours(8);
            var tasks = Enumerable.Range(0, 16).Select(index =>
            {
                var local = new DateTimeOffset(2026, 10, 22, 10, 0, index, offset);
                return database.InsertOccurrenceAsync(
                    local.ToUniversalTime(), local, OccurrenceRecord.AudioCaptureSourceMicrophone, AppPaths.Keyword);
            });

            await Task.WhenAll(tasks);
            Assert.Equal(16, await database.GetCountForDayAsync("2026-10-22"));
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Version1_Database_Is_Migrated_And_Queued_For_Sync()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, "waikan.db");
            var utcMs = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE occurrences (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        occurred_at_utc_ms INTEGER NOT NULL,
                        local_day TEXT NOT NULL,
                        local_offset_minutes INTEGER NOT NULL,
                        source TEXT NOT NULL,
                        keyword TEXT NOT NULL,
                        created_at_utc_ms INTEGER NOT NULL
                    );
                    INSERT INTO occurrences
                        (occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword, created_at_utc_ms)
                    VALUES
                        ($utcMs, '2026-10-20', 480, 'microphone', '外刊', $utcMs);
                    PRAGMA user_version=1;
                    """;
                command.Parameters.AddWithValue("$utcMs", utcMs);
                await command.ExecuteNonQueryAsync();
            }

            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(databasePath, log);
            await database.InitializeAsync();

            Assert.Equal(1, await database.GetPendingCountAsync());
            var pending = await database.GetPendingOccurrencesAsync();
            var record = Assert.Single(pending);
            Assert.False(string.IsNullOrWhiteSpace(record.SyncId));
            Assert.Equal("2026-10-20", record.LocalDay);
            Assert.Equal("外刊", record.Keyword);

            var updated = await database.MarkSyncedAsync(new[] { record.SyncId });
            Assert.Equal(1, updated);
            Assert.Equal(0, await database.GetPendingCountAsync());
            Assert.NotNull(await database.GetLastSyncedAtAsync());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CanOpen_Reports_Healthy_Database()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();
            Assert.True(await database.CanOpenAsync());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }
}
