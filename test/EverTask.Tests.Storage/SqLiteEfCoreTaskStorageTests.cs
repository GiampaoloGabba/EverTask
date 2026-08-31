using System.Globalization;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Sqlite;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("DatabaseTests")]
public class SqliteEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private string _connectionString = "";

    protected override void Initialize()
    {
        _connectionString = "Data Source=EverTask.db";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqliteEfCoreTaskStorageTests).Assembly))
                .AddSqliteStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        Assert.NotNull(_dbContext);
        Assert.NotNull(_taskStorage);
        _dbContext.Schema.ShouldBe("");
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
    }

    /// <summary>
    /// The durable-occurrence schema, read from the CATALOG rather than inferred from behaviour: exercising
    /// the operations passes just as well on a table whose unique index is missing or whose foreign key
    /// cascades, right up to the day a real workload hits the difference.
    /// </summary>
    [Fact]
    public async Task Should_have_the_durable_occurrence_schema_on_queued_tasks()
    {
        var occurrenceIndex = await ScalarAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'UX_QueuedTasks_Occurrence'");
        occurrenceIndex.ShouldContain("UNIQUE");
        occurrenceIndex.ShouldContain("ParentTaskId");
        occurrenceIndex.ShouldContain("ScheduledExecutionUtc");

        var parentIndex = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_QueuedTasks_ParentTaskId'");
        parentIndex.ShouldBe(1);

        var drainIndex = await ScalarAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_QueuedTasks_ParentTaskId_Status'");
        drainIndex.ShouldContain("\"ParentTaskId\", \"Status\"");

        // SQLite keeps the table's CREATE statement verbatim, so the check constraint and the foreign key
        // are read straight out of it.
        var tableSql = await ScalarAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'QueuedTasks'");
        tableSql.ShouldContain("CK_QueuedTasks_OccurrenceSlot");
        tableSql.ShouldContain("FK_QueuedTasks_QueuedTasks_ParentTaskId");
        tableSql.Contains("ON DELETE CASCADE", StringComparison.Ordinal).ShouldBeFalse(
            "the self-reference must restrict: the storage deletes occurrences explicitly, in one transaction");

        // EF turns foreign keys on per connection; without the pragma the key would be inert on SQLite.
        var foreignKeysOn = await ScalarAsync<long>("PRAGMA foreign_keys");
        foreignKeysOn.ShouldBe(1);
    }

    /// <summary>
    /// R5/X1: <c>ScheduleVersion INTEGER NOT NULL DEFAULT 0</c> must survive the TABLE REBUILD the migration
    /// performs — SQLite cannot ALTER a foreign key or a check constraint in, so the column is recreated from
    /// the MODEL and an <c>AddColumn(defaultValue: 0)</c> alone is silently thrown away. Without the default,
    /// an insert that does not name the column — an operator script, a data repair, an older binary — fails
    /// on SQLite alone while succeeding on the other three providers.
    /// </summary>
    [Fact]
    public async Task Should_keep_the_ScheduleVersion_default_through_the_table_rebuild()
    {
        var defaultValue = await ScalarAsync<string>(
            "SELECT COALESCE(dflt_value, '') FROM pragma_table_info('QueuedTasks') WHERE name = 'ScheduleVersion'");
        defaultValue.ShouldBe("0");

        // And it really applies: an outside writer that omits the column gets 0, not a NOT NULL violation.
        // The row is written and read back through raw SQL only — an outside writer is exactly what this is
        // about — and removed the same way, since the suite's EF cleanup would not recognise its key format.
        const string probeType = "ExternalWriterOmittingScheduleVersion";
        try
        {
            await ExecuteAsync(
                $$"""
                  INSERT INTO "QueuedTasks" ("Id", "CreatedAtUtc", "ExecutionTimeMs", "Type", "Request", "Handler",
                                             "IsRecurring", "Status")
                  VALUES ('{{Guid.NewGuid()}}', '2026-08-22 10:00:00', 0, '{{probeType}}', '{}', 'H', 0, 'Queued')
                  """);

            var written = await ScalarAsync<long>(
                $"""SELECT "ScheduleVersion" FROM "QueuedTasks" WHERE "Type" = '{probeType}'""");
            written.ShouldBe(0);
        }
        finally
        {
            await ExecuteAsync($"""DELETE FROM "QueuedTasks" WHERE "Type" = '{probeType}'""");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

#if NET10_0
    // One TFM only: EF Core renders the same migration differently across its own majors, and this
    // repository builds against three of them. See MigrationSqlSnapshot.
    [Fact]
    public void Should_emit_the_expected_sql_for_the_durable_occurrences_migration() =>
        MigrationSqlSnapshot.Verify((DbContext)_dbContext,
            "20260615005926_AddRecoveryDispatchFailureCount", "20260822183444_AddDurableOccurrences",
            "Sqlite.AddDurableOccurrences");
#endif

    protected override async Task CleanUpDatabase()
    {
        _dbContext.TaskExecutionLogs.RemoveRange(_dbContext.TaskExecutionLogs.ToList());
        _dbContext.RunsAudit.RemoveRange(_dbContext.RunsAudit.ToList());
        _dbContext.StatusAudit.RemoveRange(_dbContext.StatusAudit.ToList());
        _dbContext.QueuedTasks.RemoveRange(_dbContext.QueuedTasks.ToList());
        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    protected override string InstallStatusAuditInsertFaultSql =>
        """
        CREATE TRIGGER trg_evertask_audit_fault BEFORE INSERT ON "StatusAudit"
        BEGIN SELECT RAISE(ABORT, 'injected audit fault'); END
        """;

    protected override string RemoveStatusAuditInsertFaultSql =>
        "DROP TRIGGER IF EXISTS trg_evertask_audit_fault";

    protected override string InstallScheduleAdvanceFaultSql =>
        """
        CREATE TRIGGER trg_evertask_advance_fault BEFORE UPDATE ON "QueuedTasks"
        BEGIN SELECT RAISE(ABORT, 'injected advance fault'); END
        """;

    protected override string RemoveScheduleAdvanceFaultSql =>
        "DROP TRIGGER IF EXISTS trg_evertask_advance_fault";

    protected override ITaskStoreDbContext CreateDbContext()
    {
        return _dbContext;
    }

    protected override ITaskStorage GetStorage()
    {
        return _taskStorage;
    }

    /// <summary>
    /// Override to use SQLite-optimized GUID v7 generation.
    /// SQLite uses byte-by-byte lexicographic sorting for BLOB/TEXT.
    /// </summary>
    protected override Guid GetGuidForProvider() => TestGuidGenerator.NewForSqlite();

    public void Dispose()
    {
        CleanUpDatabase().GetAwaiter().GetResult();
    }
}
