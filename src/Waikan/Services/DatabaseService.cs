using System.Globalization;
using Microsoft.Data.Sqlite;
using Waikan.Models;

namespace Waikan.Services;

public sealed record DailyCount(string LocalDay, int Count);

public sealed class DatabaseService
{
    private const int SchemaVersion = 2;
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
                    created_at_utc_ms INTEGER NOT NULL,
                    sync_id TEXT NULL,
                    synced INTEGER NOT NULL DEFAULT 0,
                    synced_at_utc_ms INTEGER NULL
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
                if (value < 2)
                {
                    await MigrateToVersion2Async(connection, cancellationToken).ConfigureAwait(false);
                }

                await using var setVersion = connection.CreateCommand();
                setVersion.CommandText = $"PRAGMA user_version={SchemaVersion};";
                await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var syncIndexes = connection.CreateCommand())
        {
            syncIndexes.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ix_occurrences_sync_id ON occurrences(sync_id);
                CREATE INDEX IF NOT EXISTS ix_occurrences_pending ON occurrences(synced, occurred_at_utc_ms);
                """;
            await syncIndexes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.Info($"数据库已就绪：{_databasePath}");
    }

    /// <summary>
    /// Version 2 adds stable cloud-sync IDs and a pending flag. Existing rows are
    /// assigned IDs in-place so upgrading does not lose history; they are then
    /// uploaded on the next sync.
    /// </summary>
    private static async Task MigrateToVersion2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(occurrences);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        await using (var alter = connection.CreateCommand())
        {
            var statements = new List<string>();
            if (!columns.Contains("sync_id"))
            {
                statements.Add("ALTER TABLE occurrences ADD COLUMN sync_id TEXT NULL;");
            }

            if (!columns.Contains("synced"))
            {
                statements.Add("ALTER TABLE occurrences ADD COLUMN synced INTEGER NOT NULL DEFAULT 0;");
            }

            if (!columns.Contains("synced_at_utc_ms"))
            {
                statements.Add("ALTER TABLE occurrences ADD COLUMN synced_at_utc_ms INTEGER NULL;");
            }

            if (statements.Count > 0)
            {
                alter.CommandText = string.Join("\n", statements);
                await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var missingIds = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id
                FROM occurrences
                WHERE sync_id IS NULL OR sync_id = '';
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                missingIds.Add(reader.GetInt64(0));
            }
        }

        await using (var transaction = connection.BeginTransaction())
        {
            foreach (var id in missingIds)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE occurrences SET sync_id = $syncId WHERE id = $id;";
                update.Parameters.AddWithValue("$syncId", Guid.NewGuid().ToString("N"));
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_occurrences_sync_id ON occurrences(sync_id);
            CREATE INDEX IF NOT EXISTS ix_occurrences_pending ON occurrences(synced, occurred_at_utc_ms);
            """;
        await indexes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        var syncId = Guid.NewGuid().ToString("N");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO occurrences
                (occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword, created_at_utc_ms, sync_id, synced)
            VALUES
                ($utcMs, $localDay, $offsetMinutes, $source, $keyword, $createdAtMs, $syncId, 0)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$utcMs", utcMs);
        command.Parameters.AddWithValue("$localDay", localDay);
        command.Parameters.AddWithValue("$offsetMinutes", offsetMinutes);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$keyword", keyword);
        command.Parameters.AddWithValue("$createdAtMs", createdAtMs);
        command.Parameters.AddWithValue("$syncId", syncId);

        var idValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(idValue, System.Globalization.CultureInfo.InvariantCulture);

        return new OccurrenceRecord(
            id,
            occurredAtUtc.ToUniversalTime(),
            localTime,
            localDay,
            source,
            keyword,
            syncId,
            false);
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
            SELECT id, occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword, sync_id, synced
            FROM occurrences
            WHERE occurred_at_utc_ms >= $fromMs AND occurred_at_utc_ms <= $toMs
            ORDER BY occurred_at_utc_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$fromMs", fromMs);
        command.Parameters.AddWithValue("$toMs", toMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadOccurrence(reader));
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

    public async Task<IReadOnlyList<OccurrenceRecord>> GetPendingOccurrencesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var results = new List<OccurrenceRecord>();
        var safeLimit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at_utc_ms, local_day, local_offset_minutes, source, keyword, sync_id, synced
            FROM occurrences
            WHERE synced = 0
            ORDER BY occurred_at_utc_ms ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadOccurrence(reader));
        }

        return results;
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM occurrences WHERE synced = 0;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(synced_at_utc_ms) FROM occurrences WHERE synced = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(
            Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Marks rows as uploaded. The server records event IDs idempotently, so a
    /// retry after a lost response cannot double-count the same occurrences.
    /// </summary>
    public async Task<int> MarkSyncedAsync(
        IReadOnlyCollection<string> syncIds,
        CancellationToken cancellationToken = default)
    {
        if (syncIds.Count == 0)
        {
            return 0;
        }

        var ids = syncIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var idParameters = new string[ids.Length];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        for (var index = 0; index < ids.Length; index++)
        {
            idParameters[index] = $"$syncId{index}";
            command.Parameters.AddWithValue(idParameters[index], ids[index]);
        }

        command.Parameters.AddWithValue("$nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.CommandText = $"""
            UPDATE occurrences
            SET synced = 1, synced_at_utc_ms = $nowMs
            WHERE sync_id IN ({string.Join(", ", idParameters)});
            """;

        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
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

    private static OccurrenceRecord ReadOccurrence(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var utcMs = reader.GetInt64(1);
        var localDay = reader.GetString(2);
        var offsetMinutes = reader.GetInt32(3);
        var source = reader.GetString(4);
        var keyword = reader.GetString(5);
        var syncId = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        var synced = !reader.IsDBNull(7) && reader.GetInt64(7) != 0;
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMs);
        var local = utc.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        return new OccurrenceRecord(id, utc, local, localDay, source, keyword, syncId, synced);
    }
}
