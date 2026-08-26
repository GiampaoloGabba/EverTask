using System.Collections.Concurrent;
using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.Logger;
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
    private readonly RecordingLogger<SqlServerTaskStorage> _storageLog = new();
    private string _connectionString = "";
    private static readonly object _lock = new();

    /// <summary>EfCoreStorageLog.RereadAfterDeadlock — the storage saying it ran a read again.</summary>
    private const int RereadAfterDeadlockEventId = 2025;

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

        // Last registration wins: the storage keeps the EventIds it writes where a test can read them.
        services.AddSingleton<IEverTaskLogger<SqlServerTaskStorage>>(_storageLog);

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

    /// <summary>
    /// A read that SQL Server picks as the deadlock victim is re-run instead of thrown at the caller.
    /// </summary>
    /// <remarks>
    /// The collision is the real one, not a simulated one. <c>RetrievePending</c> walks
    /// IX_QueuedTasks_Recovery in (CreatedAtUtc, Id) order and looks each row up in the clustered index —
    /// nonclustered first, clustered second — while <c>usp_SetTaskStatus</c> writes the clustered row first
    /// and the two indexes carrying Status after it. Two lock orders, one cycle, and the engine kills
    /// whichever side wrote nothing: the read. Any other read queued on the same clustered row can be
    /// picked instead, which is how a poll of unrelated rows ends up as the victim of a recovery the caller
    /// never asked about.
    /// The storage's own rerun log is what proves the scenario really happened: a run in which nothing
    /// collided FAILS rather than passing without having tested anything.
    /// The write pressure runs INSIDE SQL Server (see <see cref="WritePressureBatchSql"/>): a
    /// solution-wide `dotnet test` has fifteen test hosts competing for the thread pool, and client-side
    /// loops then issue a fraction of the round trips they issue alone — which is how a run reached its
    /// budget without a cycle ever forming and reported itself as a storage that does not re-run its
    /// reads. A server-side loop keeps hammering at full speed whatever the client's threads are doing,
    /// so the reads under test only have to arrive; each one meets a table being rewritten continuously.
    /// </remarks>
    [Fact]
    public async Task Should_rerun_a_read_that_sql_server_picked_as_the_deadlock_victim()
    {
        var storage = GetStorage();
        var ids     = await SeedRecoverableRowsAsync(200);

        // Diagnosis, not an assertion: whether the ENGINE deadlocked at all during the run separates
        // "no cycle ever formed" from "cycles formed and no read of the storage was ever the victim".
        // Both mean the run proved nothing, and neither is a storage that stopped re-running its reads.
        var deadlocksBefore = await ReadEngineDeadlockCountAsync();
        var pressureClock   = Stopwatch.StartNew();

        // The whole point is to stop as soon as the engine HAS deadlocked, which alone on the machine takes
        // two or three seconds; the budget is the guarantee that a run on which nothing ever collides still
        // ends, so it is sized for the slowest run rather than the expected one and costs nothing on a
        // healthy one. Twenty seconds was not sized for it: a solution-wide `dotnet test` runs three target
        // frameworks at once, so three SQL Server containers and every other suite share these cores, the
        // pressure loops get a fraction of the round trips they get alone, and how long a cycle takes to
        // form scales with them - a run that simply had not collided YET then reported itself as a run in
        // which the storage does not re-run its reads.
        // It is a STOP SIGNAL for the loops, never a token handed to a command: an attention sent to a
        // query already on the wire surfaces out of SqlClient as a bare SqlException ("A severe error
        // occurred on the current command ... Operation cancelled by user") as readily as an
        // OperationCanceledException, and since cancelling is how this test ends, a read carrying the token
        // would eventually hand `readFailures` a failure the test itself caused - on the very run that had
        // just proved the reread works.
        using var pressure = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var readFailures  = new ConcurrentQueue<Exception>();
        var writeFailures = new ConcurrentQueue<Exception>();

        // The nonclustered-then-clustered side: the startup-recovery page, unchanged.
        async Task RecoveryReads()
        {
            while (!pressure.IsCancellationRequested)
            {
                try
                {
                    await storage.RetrievePending(DateTimeOffset.UtcNow, null, null, 200, CancellationToken.None);
                }
                catch (Exception e) { readFailures.Enqueue(e); }
            }
        }

        // The clustered-then-nonclustered side: the status write of a task being executed. Audits are off
        // so the pressure - and the cleanup after it - stays on the one table the cycle is about.
        // A write runs to completion for a second reason of its own: usp_SetTaskStatus owns a transaction,
        // and cancelling one mid-flight leaves it open on a pooled connection, holding the locks the cleanup
        // of this test then waits for.
        // Each loop starts at a different point of the table so two writers spend their time on different
        // rows: writers share one lock order and only ever block each other, while a writer and a reader
        // meeting on the same row are the cycle this test is about.
        async Task StatusWrites(int offset)
        {
            while (!pressure.IsCancellationRequested)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    if (pressure.IsCancellationRequested) break;

                    var id = ids[(i + offset) % ids.Length];

                    await storage.SetStatus(id, QueuedTaskStatus.InProgress, null, AuditLevel.None);
                    await storage.SetStatus(id, QueuedTaskStatus.Queued, null, AuditLevel.None);
                }
            }
        }

        // The same write, driven from inside the engine: one round trip buys a burst of thousands of
        // executions of usp_SetTaskStatus instead of one, so the write pressure no longer depends on how
        // often this process gets a thread. The burst is short and re-issued in a loop rather than running
        // for the whole budget, so the run still ends promptly once a collision has been proved - and it
        // ends BETWEEN bursts, never by cancelling a write that owns a transaction.
        async Task ServerSideWrites(int offset)
        {
            while (!pressure.IsCancellationRequested)
            {
                try
                {
                    await using var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync(CancellationToken.None);

                    await using var command = new SqlCommand(WritePressureBatchSql, connection)
                                              { CommandTimeout = 120 };
                    command.Parameters.AddWithValue("@BurstMs", WriteBurstMs);
                    command.Parameters.AddWithValue("@Offset", offset);

                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (SqlException e)
                {
                    // The pressure is allowed to lose: a batch picked as the deadlock victim is rolled
                    // back and reported here, and the next burst takes its place. Only the READS are the
                    // claim. Counted so a run that proved nothing can say what its writers were doing.
                    writeFailures.Enqueue(e);
                }
            }
        }

        // The bystander: a caller reading rows of its own, queued behind the same clustered row. This is
        // the one that failed in the multi-host suite - a poll that asked about nothing the cycle involved.
        async Task BystanderReads()
        {
            while (!pressure.IsCancellationRequested)
            {
                try
                {
                    await storage.Get(t => t.Status == QueuedTaskStatus.InProgress, CancellationToken.None);
                }
                catch (Exception e) { readFailures.Enqueue(e); }
            }
        }

        async Task StopOnceItHasDeadlocked()
        {
            while (!pressure.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(200, pressure.Token);
                }
                catch (OperationCanceledException) { return; }

                // One rerun is the whole claim, and it is exactly what the assertion below reads. Waiting
                // for a second one asked a second collision to fit inside the budget too, and the gap
                // between the first and the second is where most of the wait went: measured at two to
                // eight seconds on an idle machine, more under a solution-wide run.
                // A read that FAILED ends the run too: that is the regression this test exists for, and
                // it is already proved - there is nothing left for the remaining budget to add.
                if (_storageLog.Count(RereadAfterDeadlockEventId) > 0 || !readFailures.IsEmpty)
                    await pressure.CancelAsync();
            }
        }

        await Task.WhenAll(
            RecoveryReads(), RecoveryReads(), RecoveryReads(),
            StatusWrites(0), StatusWrites(ids.Length / 2),
            ServerSideWrites(0), ServerSideWrites(ids.Length / 3), ServerSideWrites(2 * ids.Length / 3),
            BystanderReads(), BystanderReads(),
            StopOnceItHasDeadlocked());

        pressureClock.Stop();

        readFailures.ShouldBeEmpty(
            "a read has nothing to undo and nothing to reconcile: being chosen as the deadlock victim is the " +
            "engine asking it to run again, not a failure to hand the caller" +
            (readFailures.TryPeek(out var firstFailure) ? $" - first failure: {firstFailure}" : ""));

        var deadlocksDuringRun = await ReadEngineDeadlockCountAsync() - deadlocksBefore;

        _storageLog.Count(RereadAfterDeadlockEventId).ShouldBeGreaterThan(0,
            "no collision could be provoked against a read of the storage in " +
            $"{pressureClock.Elapsed.TotalSeconds:F0}s of pressure (the engine resolved {deadlocksDuringRun} " +
            $"deadlock(s) in that window, and {writeFailures.Count} write burst(s) were aborted), so this run " +
            "proves nothing about what happens when one does - it is not evidence that the storage stopped " +
            "re-running its reads, which would have surfaced above as a failed read");
    }

    /// <summary>How long one server-side write burst hammers before the client gets its turn to stop it.</summary>
    private const int WriteBurstMs = 2000;

    /// <summary>
    /// Write pressure that runs inside the engine: rotate over the seeded rows from <c>@Offset</c> and
    /// run the real <c>usp_SetTaskStatus</c> on each until <c>@BurstMs</c> have passed. The procedure is
    /// the point — its UPDATE takes the clustered row and then the two indexes carrying <c>Status</c>,
    /// which is the lock order the read collides with — and driving it from a table variable keeps the
    /// loop itself off the indexes the cycle is about.
    /// </summary>
    private string WritePressureBatchSql =>
        $"""
         SET NOCOUNT ON;

         DECLARE @ids TABLE (Seq INT IDENTITY(1,1) PRIMARY KEY, Id UNIQUEIDENTIFIER);
         INSERT INTO @ids (Id) SELECT Id FROM [{_dbContext.Schema}].[QueuedTasks];

         DECLARE @count INT = (SELECT COUNT(*) FROM @ids);
         IF @count = 0 RETURN;

         DECLARE @deadline DATETIME2(3) = DATEADD(MILLISECOND, @BurstMs, SYSUTCDATETIME());
         DECLARE @seq INT = (@Offset % @count) + 1;
         DECLARE @id UNIQUEIDENTIFIER;

         WHILE SYSUTCDATETIME() < @deadline
         BEGIN
             SELECT @id = Id FROM @ids WHERE Seq = @seq;

             EXEC [{_dbContext.Schema}].[usp_SetTaskStatus] @TaskId = @id, @Status = N'InProgress',
                  @Exception = NULL, @AuditLevel = 3;
             EXEC [{_dbContext.Schema}].[usp_SetTaskStatus] @TaskId = @id, @Status = N'Queued',
                  @Exception = NULL, @AuditLevel = 3;

             SET @seq = CASE WHEN @seq >= @count THEN 1 ELSE @seq + 1 END;
         END
         """;

    /// <summary>
    /// Deadlocks the engine has resolved since it started, instance-wide. Purely diagnostic: it is what
    /// tells a run that provoked nothing apart from a run whose cycles never picked a read of the storage.
    /// Never throws — a diagnosis that fails the test it is explaining is worse than no diagnosis.
    /// </summary>
    private async Task<long> ReadEngineDeadlockCountAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(
                """
                SELECT cntr_value FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Number of Deadlocks/sec' AND instance_name = '_Total'
                """,
                connection);

            return await command.ExecuteScalarAsync() as long? ?? 0;
        }
        catch (SqlException)
        {
            return 0;
        }
    }

    /// <summary>Rows a recovery page returns, enough that it resolves them through the index.</summary>
    private async Task<Guid[]> SeedRecoverableRowsAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => TestGuidGenerator.NewForSqlServer()).ToArray();
        var now = DateTimeOffset.UtcNow;

        var values = string.Join(", ", ids.Select((_, i) =>
            $"(@Id{i}, @Created{i}, 0, 'deadlock-probe', '[]', 'deadlock-probe', 0, 0, 'default', 0, 'Queued', 0)"));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            $"""
             INSERT INTO [{_dbContext.Schema}].[QueuedTasks]
                 (Id, CreatedAtUtc, ExecutionTimeMs, Type, Request, Handler, IsRecurring, CurrentRunCount,
                  QueueName, AuditLevel, Status, ScheduleVersion)
             VALUES {values}
             """, connection);

        for (var i = 0; i < ids.Length; i++)
        {
            command.Parameters.AddWithValue($"@Id{i}", ids[i]);
            command.Parameters.AddWithValue($"@Created{i}", now.AddMinutes(i - count));
        }

        await command.ExecuteNonQueryAsync();

        return ids;
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
