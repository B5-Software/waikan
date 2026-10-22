using Waikan.Models;
using Waikan.Services;

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
