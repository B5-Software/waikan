using System.Globalization;
using Microsoft.Data.Sqlite;
using Waikan.Models;

namespace Waikan.Services;

public sealed record DailyCount(string LocalDay, int Count);

public sealed class DatabaseService
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly LogService _log;
    private readonly string _connectionString;

    public DatabaseService(string databasePath, LogService log)
    {
        _databasePath = databasePath;
        _log = log;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5,
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                PRAGMA foreign_keys=ON;
                """;
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS occurrences (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_utc_ms INTEGER NOT NULL,
                    local_day TEXT NOT NULL,
                    local_offset_minutes INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    keyword TEXT NOT NULL,
                    created_at_utc_ms INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_occurrences_utc_ms ON occurrences(occurred_at_utc_ms);
                CREATE INDEX IF NOT EXISTS ix_occurrences_local_day ON occurrences(local_day);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var value = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (value > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"数据库版本 {value} 高于当前应用支持的版本 {SchemaVersion}。请升级应用或备份数据后重试。");
            }

            if (value < SchemaVersion)
            {
                await using var setVersion = connection.CreateCommand();
                setVersion.CommandText = $"PRAGMA user_version={SchemaVersion};";
                await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _log.Info($"数据库已就绪：{_databasePath}");
    }

    public async Task<OccurrenceRecord> InsertOccurrenceAsync(
        DateTimeOffset occurredAtUtc,
        DateTimeOffset localTime,
        string source,
        string keyword,
        CancellationToken cancellationToken = default)
    {
        var utcMs = occurredAtUtc.ToUnixTimeMilliseconds();
        var createdAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var localDay = localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var offsetMinutes = (int)localTime.Offset.TotalMinutes;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO occurrences
                (occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword, created_at_utc_ms)
            VALUES
                ($utcMs, $localDay, $offsetMinutes, $source, $keyword, $createdAtMs)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$utcMs", utcMs);
        command.Parameters.AddWithValue("$localDay", localDay);
        command.Parameters.AddWithValue("$offsetMinutes", offsetMinutes);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$keyword", keyword);
        command.Parameters.AddWithValue("$createdAtMs", createdAtMs);

        var idValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(idValue, System.Globalization.CultureInfo.InvariantCulture);

        return new OccurrenceRecord(id, occurredAtUtc.ToUniversalTime(), localTime, localDay, source, keyword);
    }

    public async Task<int> GetCountForDayAsync(string localDay, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM occurrences WHERE local_day = $day;";
        command.Parameters.AddWithValue("$day", localDay);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<OccurrenceRecord>> GetOccurrencesAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken = default)
    {
        var fromMs = fromInclusive.ToUnixTimeMilliseconds();
        var toMs = toInclusive.ToUnixTimeMilliseconds();
        var results = new List<OccurrenceRecord>();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword
            FROM occurrences
            WHERE occurred_at_utc_ms >= $fromMs AND occurred_at_utc_ms <= $toMs
            ORDER BY occurred_at_utc_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$fromMs", fromMs);
        command.Parameters.AddWithValue("$toMs", toMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var utcMs = reader.GetInt64(1);
            var localDay = reader.GetString(2);
            var offsetMinutes = reader.GetInt32(3);
            var source = reader.GetString(4);
            var keyword = reader.GetString(5);
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMs);
            var local = utc.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
            results.Add(new OccurrenceRecord(id, utc, local, localDay, source, keyword));
        }

        return results;
    }

    public async Task<IReadOnlyList<DailyCount>> GetDailyCountsAsync(
        string fromDay,
        string toDay,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DailyCount>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_day, COUNT(*)
            FROM occurrences
            WHERE local_day >= $fromDay AND local_day <= $toDay
            GROUP BY local_day
            ORDER BY local_day ASC;
            """;
        command.Parameters.AddWithValue("$fromDay", fromDay);
        command.Parameters.AddWithValue("$toDay", toDay);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DailyCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return results;
    }

    public async Task<bool> DeleteOccurrenceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM occurrences WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<int> DeleteRangeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM occurrences
            WHERE occurred_at_utc_ms >= $fromMs AND occurred_at_utc_ms <= $toMs;
            """;
        command.Parameters.AddWithValue("$fromMs", fromInclusive.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", toInclusive.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetTotalCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM occurrences;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> CanOpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception ex)
        {
            _log.Error("数据库健康检查失败。", ex);
            return false;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
