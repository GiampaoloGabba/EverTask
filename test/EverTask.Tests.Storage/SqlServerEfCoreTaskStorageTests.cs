using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.SqlServer;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("DatabaseTests")]
public class SqlServerEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IAsyncLifetime, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";
    private static readonly object _lock = new();

    public async Task InitializeAsync()
    {
        // Clean database before each test
        await CleanUpDatabase();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected override void Initialize()
    {
        // The assembly shares one SQL Server container; this is the only caller that cannot await it.
        _connectionString = SqlServerTestContainer.GetConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqlServerEfCoreTaskStorageTests).Assembly))
                .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        // Apply migrations once
        lock (_lock)
        {
            using var scope = serviceProvider.CreateScope();
            // The pooled factory does not register the concrete context for direct resolution; resolve the
            // scoped ITaskStoreDbContext (a real SqlServerTaskStoreContext created via the factory) instead.
            var context = (DbContext)scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();
            context.Database.Migrate();
        }

        _dbContext = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = serviceProvider.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        Assert.NotNull(_dbContext);
        Assert.NotNull(_taskStorage);
        _dbContext.Schema.ShouldBe("EverTask");
    }

    [Fact]
    public async Task Should_have_recovery_index_on_queued_tasks()
    {
        // IX_QueuedTasks_Recovery is what keeps RetrievePending off a clustered scan + sort
        // on large tables (see AddRecoveryIndexAndUpdateRunProcedure migration)
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = 'IX_QueuedTasks_Recovery'
              AND object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
            """,
            connection);

        var count = (int)(await command.ExecuteScalarAsync())!;
        count.ShouldBe(1, "IX_QueuedTasks_Recovery should exist on QueuedTasks");
    }

    [Fact]
    public async Task Should_have_status_and_run_update_stored_procedures()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.objects
            WHERE type = 'P'
              AND SCHEMA_NAME(schema_id) = 'EverTask'
              AND name IN ('usp_SetTaskStatus', 'usp_UpdateCurrentRun', 'usp_CompleteRecurringRun')
            """,
            connection);

        var count = (int)(await command.ExecuteScalarAsync())!;
        count.ShouldBe(3, "usp_SetTaskStatus, usp_UpdateCurrentRun and usp_CompleteRecurringRun should exist");
    }

    [Fact]
    public async Task Should_have_complete_recurring_run_stored_procedure()
    {
        // Single-roundtrip counterpart of EfCoreTaskStorage.CompleteRecurringRun on SQL Server:
        // the override execs this proc, so it MUST exist after migrations are applied.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.objects
            WHERE type = 'P'
              AND SCHEMA_NAME(schema_id) = 'EverTask'
              AND name = 'usp_CompleteRecurringRun'
            """,
            connection);

        var count = (int)(await command.ExecuteScalarAsync())!;
        count.ShouldBe(1, "usp_CompleteRecurringRun should exist");
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The durable-occurrence schema, read from the CATALOG rather than inferred from behaviour: a test that
    /// only exercises the operations passes just as well on a table whose unique index is missing its filter
    /// or whose foreign key cascades, right up to the day a real workload hits the difference.
    /// </summary>
    [Fact]
    public async Task Should_have_the_durable_occurrence_schema_on_queued_tasks()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // The unique index MUST be filtered here and nowhere else: SQL Server treats NULLs as equal in a
        // unique index, and every ordinary row has a null ParentTaskId, so an unfiltered one would reject
        // the second non-occurrence task ever persisted.
        var occurrenceIndex = await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = 'UX_QueuedTasks_Occurrence'
              AND object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
              AND is_unique = 1
              AND has_filter = 1
            """);
        occurrenceIndex.ShouldBe(1, "UX_QueuedTasks_Occurrence must exist, be unique AND be filtered");

        var indexedColumns = await ScalarAsync<string>(connection,
            """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'UX_QueuedTasks_Occurrence'
              AND i.object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
            """);
        indexedColumns.ShouldBe("ParentTaskId,ScheduledExecutionUtc");

        var parentIndex = await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = 'IX_QueuedTasks_ParentTaskId' AND object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
            """);
        parentIndex.ShouldBe(1, "the occurrence lookups need their own index");

        var checkConstraint = await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE name = 'CK_QueuedTasks_OccurrenceSlot' AND parent_object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
            """);
        checkConstraint.ShouldBe(1, "an occurrence without its nominal slot must be impossible");

        // NO_ACTION, never CASCADE: SQL Server refuses a cascading self-reference outright, and the whole
        // point of the key is to stop a concurrent Remove of the schedule from orphaning its occurrences.
        var foreignKey = await ScalarAsync<string>(connection,
            """
            SELECT fk.delete_referential_action_desc
            FROM sys.foreign_keys fk
            WHERE fk.name = 'FK_QueuedTasks_QueuedTasks_ParentTaskId'
              AND fk.parent_object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
              AND fk.referenced_object_id = OBJECT_ID('[EverTask].[QueuedTasks]')
            """);
        foreignKey.ShouldBe("NO_ACTION");
    }

    [Fact]
    public async Task Should_have_the_durable_occurrence_stored_procedures()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var count = await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM sys.objects
            WHERE type = 'P'
              AND SCHEMA_NAME(schema_id) = 'EverTask'
              AND name IN ('usp_MaterializeOccurrence', 'usp_CancelSchedule',
                           'usp_UpdateCurrentRunCas', 'usp_CompleteRecurringRunCas')
            """);
        count.ShouldBe(4, "the four durable-occurrence procedures must exist after migrations");

        // Without XACT_ABORT a failed INSERT inside usp_MaterializeOccurrence aborts only its own statement:
        // the procedure falls through to the advance and commits a schedule that moved on without its
        // occurrence. Pinned here as well as behaviourally, because the behavioural proof needs a fault.
        var definition = await ScalarAsync<string>(connection,
            "SELECT OBJECT_DEFINITION(OBJECT_ID('[EverTask].[usp_MaterializeOccurrence]'))");
        definition.ShouldContain("SET XACT_ABORT ON");
    }

#if NET10_0
    // One TFM only: EF Core renders the same migration differently across its own majors, and this
    // repository builds against three of them. See MigrationSqlSnapshot.
    [Fact]
    public void Should_emit_the_expected_sql_for_the_durable_occurrences_migration() =>
        MigrationSqlSnapshot.Verify((DbContext)_dbContext,
            "20260616181803_SaturateRunCounter", "20260822182413_AddDurableOccurrences",
            "SqlServer.AddDurableOccurrences");
#endif

    protected override async Task CleanUpDatabase()
    {
        // Use Respawn for efficient, reliable cleanup
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Respawn 7+ requires a DbConnection (string overloads were removed)
        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
                           { TablesToIgnore = ["__EFMigrationsHistory"] });

        await _respawner.ResetAsync(connection);
    }

    protected override string InstallStatusAuditInsertFaultSql =>
        // THROW inside a trigger dooms the transaction, which is the point: the procedure runs with
        // XACT_ABORT ON and must never reach its COMMIT.
        $"""
         CREATE TRIGGER [{_dbContext.Schema}].[trg_evertask_audit_fault]
         ON [{_dbContext.Schema}].[StatusAudit] AFTER INSERT
         AS BEGIN THROW 51000, 'injected audit fault', 1; END
         """;

    protected override string RemoveStatusAuditInsertFaultSql =>
        $"DROP TRIGGER IF EXISTS [{_dbContext.Schema}].[trg_evertask_audit_fault]";

    protected override string InstallScheduleAdvanceFaultSql =>
        // usp_MaterializeOccurrence inserts the occurrence and THEN advances the schedule, so this trigger
        // fires in the window the plan names.
        $"""
         CREATE TRIGGER [{_dbContext.Schema}].[trg_evertask_advance_fault]
         ON [{_dbContext.Schema}].[QueuedTasks] AFTER UPDATE
         AS BEGIN THROW 51001, 'injected advance fault', 1; END
         """;

    protected override string RemoveScheduleAdvanceFaultSql =>
        $"DROP TRIGGER IF EXISTS [{_dbContext.Schema}].[trg_evertask_advance_fault]";

    protected override ITaskStoreDbContext CreateDbContext()
    {
        return _dbContext;
    }

    protected override ITaskStorage GetStorage()
    {
        return _taskStorage;
    }

    /// <summary>
    /// Override to use SQL Server-optimized GUID v7 generation.
    /// SQL Server uniqueidentifier has specific sorting behavior.
    /// </summary>
    protected override Guid GetGuidForProvider() => TestGuidGenerator.NewForSqlServer();

    public void Dispose()
    {
        CleanUpDatabase().GetAwaiter().GetResult();
    }
}
