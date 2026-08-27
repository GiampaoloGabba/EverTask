using EverTask.Abstractions;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Postgres;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("DatabaseTests")]
public class PostgresEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IAsyncLifetime, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";
    private static PostgreSqlContainer? _postgresContainer;
    private static bool _containerInitialized = false;
    private static readonly object _lock = new();

    public async Task InitializeAsync()
    {
        // Clean database before each test
        await CleanUpDatabase();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected override void Initialize()
    {
        // Start container once for all tests in this collection. The image is PINNED (postgres:16-alpine):
        // the Testcontainers default tag can drift between package versions and stall the serial DatabaseTests
        // queue on a slow first pull. alpine is small/fast (vs the ~1.67 GB mssql image).
        lock (_lock)
        {
            if (!_containerInitialized)
            {
                _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
                _postgresContainer.StartAsync().GetAwaiter().GetResult();
                _containerInitialized = true;
            }
        }

        _connectionString = _postgresContainer!.GetConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(PostgresEfCoreTaskStorageTests).Assembly))
                .AddPostgresStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        // Apply migrations once
        lock (_lock)
        {
            using var scope = serviceProvider.CreateScope();
            // The pooled factory does not register the concrete context for direct resolution; resolve the
            // scoped ITaskStoreDbContext (a real PostgresTaskStoreContext created via the factory) instead.
            var context = (DbContext)scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();
            context.Database.Migrate();
        }

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = serviceProvider.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        Assert.NotNull(_dbContext);
        Assert.NotNull(_taskStorage);
        // Postgres is fully relational -> non-empty schema, lowercase (folding-safe).
        _dbContext.Schema.ShouldBe("evertask");
    }

    [Fact]
    public async Task Should_have_recovery_index_on_queued_tasks()
    {
        // IX_QueuedTasks_Recovery is what keeps RetrievePending off a sequential scan + sort on large tables
        // (partial + covering index, see the Initial migration). FORM B: partial WHERE on recoverable states.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM pg_indexes
            WHERE indexname = 'IX_QueuedTasks_Recovery'
              AND schemaname = 'evertask'
            """,
            connection);

        var count = (long)(await command.ExecuteScalarAsync())!;
        count.ShouldBe(1, "IX_QueuedTasks_Recovery should exist on the evertask.QueuedTasks table");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The durable-occurrence schema, read from the CATALOG rather than inferred from behaviour: exercising
    /// the operations passes just as well on a table whose unique index is missing or whose foreign key
    /// cascades, right up to the day a real workload hits the difference.
    /// </summary>
    [Fact]
    public async Task Should_have_the_durable_occurrence_schema_on_queued_tasks()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // No filter here, unlike SQL Server: PostgreSQL already treats NULLs as distinct in a unique index.
        var occurrenceIndex = await ScalarAsync<string>(connection,
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'evertask' AND indexname = 'UX_QueuedTasks_Occurrence'
            """);
        occurrenceIndex.ShouldContain("UNIQUE");
        occurrenceIndex.ShouldContain("ParentTaskId");
        occurrenceIndex.ShouldContain("ScheduledExecutionUtc");
        occurrenceIndex.Contains("WHERE", StringComparison.Ordinal).ShouldBeFalse(
            "a filter would be redundant on PostgreSQL and would silently narrow the guarantee");

        var parentIndex = await ScalarAsync<long>(connection,
            """
            SELECT COUNT(*) FROM pg_indexes
            WHERE schemaname = 'evertask' AND indexname = 'IX_QueuedTasks_ParentTaskId'
            """);
        parentIndex.ShouldBe(1);

        var drainIndex = await ScalarAsync<string>(connection,
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'evertask' AND indexname = 'IX_QueuedTasks_ParentTaskId_Status'
            """);
        drainIndex.ShouldContain("\"ParentTaskId\", \"Status\"");

        var checkConstraint = await ScalarAsync<string>(connection,
            """
            SELECT pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'evertask' AND t.relname = 'QueuedTasks'
              AND c.conname = 'CK_QueuedTasks_OccurrenceSlot'
            """);
        checkConstraint.Contains("ParentTaskId", StringComparison.Ordinal).ShouldBeTrue(
            "the check must be spelled with QUOTED column names or PostgreSQL folds them to lowercase");

        // 'r' = RESTRICT ('a' would be NO ACTION, 'c' CASCADE). Npgsql renders DeleteBehavior.Restrict as a
        // genuine RESTRICT; what matters is that it is neither of the cascading rules — the key exists to stop
        // a concurrent Remove of the schedule from orphaning its occurrences, and the storage deletes them
        // explicitly in the same transaction.
        var deleteRule = await ScalarAsync<char>(connection,
            """
            SELECT c.confdeltype
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'evertask' AND t.relname = 'QueuedTasks'
              AND c.conname = 'FK_QueuedTasks_QueuedTasks_ParentTaskId'
            """);
        deleteRule.ShouldBe('r');
    }

#if NET10_0
    // One TFM only: EF Core renders the same migration differently across its own majors, and this
    // repository builds against three of them. See MigrationSqlSnapshot.
    [Fact]
    public void Should_emit_the_expected_sql_for_the_durable_occurrences_migration() =>
        MigrationSqlSnapshot.Verify((DbContext)_dbContext,
            "20260616162141_Initial", "20260822182810_AddDurableOccurrences",
            "Postgres.AddDurableOccurrences");
#endif

    /// <summary>
    /// M15 under PostgreSQL's snapshot rules: a cancel that runs while a materializer is inserting an
    /// occurrence must also cancel that occurrence.
    /// </summary>
    /// <remarks>
    /// The materializer is reproduced here as a second real session doing exactly what
    /// <c>MaterializeOccurrence</c> does — lock the schedule row <c>FOR UPDATE</c>, insert the child, commit —
    /// because that lock discipline is the whole point. Under READ COMMITTED a single statement runs on a
    /// snapshot taken BEFORE it starts waiting on a row lock, so a cancel expressed as one UPDATE never sees
    /// the child that appears while it waits: the schedule would end up Cancelled with a brand-new
    /// <c>WaitingQueue</c> occurrence free to run after it.
    /// </remarks>
    [Fact]
    public async Task CancelSchedule_must_also_cancel_an_occurrence_a_materializer_commits_while_it_waits()
    {
        var cursor   = DateTimeOffset.UtcNow.AddMinutes(-1);
        var schedule = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            Type         = "DurableSchedule",
            Request      = "{}",
            Handler      = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status       = QueuedTaskStatus.Queued,
            IsRecurring  = true,
            NextRunUtc   = cursor
        };
        await _taskStorage.Persist(schedule);

        var childId = GetGuidForProvider();

        await using var materializer = new NpgsqlConnection(_connectionString);
        await materializer.OpenAsync();

        await using var transaction = await materializer.BeginTransactionAsync();

        await using (var takeLock = new NpgsqlCommand(
                         """SELECT "Id" FROM "evertask"."QueuedTasks" WHERE "Id" = @parent FOR UPDATE""",
                         materializer, transaction))
        {
            takeLock.Parameters.AddWithValue("parent", schedule.Id);
            await takeLock.ExecuteScalarAsync();
        }

        // The cancel starts now and blocks on the schedule row the materializer holds.
        var cancel = Task.Run(() => _taskStorage.CancelSchedule(schedule.Id, AuditLevel.Full));

        await WaitForBlockedBackendAsync();

        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO "evertask"."QueuedTasks"
                             ("Id", "CreatedAtUtc", "ExecutionTimeMs", "ScheduledExecutionUtc", "Type", "Request",
                              "Handler", "IsRecurring", "CurrentRunCount", "Status", "ParentTaskId", "ScheduleVersion")
                         VALUES (@id, now(), 0, @slot, 'DurableSchedule', '{}', 'H', false, 0, 'WaitingQueue',
                                 @parent, 0)
                         """,
                         materializer, transaction))
        {
            insert.Parameters.AddWithValue("id", childId);
            insert.Parameters.AddWithValue("slot", cursor);
            insert.Parameters.AddWithValue("parent", schedule.Id);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        await cancel;

        (await _taskStorage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await _taskStorage.Get(t => t.Id == childId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "an occurrence committed while the cancel was waiting must not survive the cancel and execute");
    }

    /// <summary>Waits until some backend is blocked on a lock, so the ordering under test is real.</summary>
    private async Task WaitForBlockedBackendAsync()
    {
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await ScalarAsync<long>(observer,
                "SELECT COUNT(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND state = 'active'");
            if (blocked > 0)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException("The cancel never reached the schedule row's lock: the race was not set up.");
    }

    [Fact]
    public async Task TaskKey_unique_index_allows_multiple_null_keys()
    {
        // R18: the shared model declares .HasIndex(TaskKey).IsUnique() WITHOUT HasFilter. Npgsql emits no
        // IS NOT NULL filter (a standard Postgres unique index already treats multiple NULLs as distinct,
        // NULLS DISTINCT default), so two TaskKey = NULL rows must coexist. This is NOT an anomaly.
        var a = new QueuedTask
        {
            Id = GetGuidForProvider(), Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.Pending, TaskKey = null
        };
        var b = new QueuedTask
        {
            Id = GetGuidForProvider(), Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.Pending, TaskKey = null
        };

        await _taskStorage.Persist(a);
        await Should.NotThrowAsync(() => _taskStorage.Persist(b));

        (await _taskStorage.Get(x => x.Id == a.Id)).Length.ShouldBe(1);
        (await _taskStorage.Get(x => x.Id == b.Id)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task CleanUpDatabase_resets_all_rows_to_zero()
    {
        // R12: prove Respawn (DbAdapter.Postgres + SchemasToInclude=[public, evertask]) actually empties the
        // EverTask tables that live in the 'evertask' schema. Without SchemasToInclude the Postgres adapter
        // only touches 'public' and would clean NOTHING.
        await _taskStorage.Persist(new QueuedTask
        {
            Id = GetGuidForProvider(), Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.Pending
        });
        (await _taskStorage.GetAll()).Count().ShouldBeGreaterThan(0);

        await CleanUpDatabase();

        (await _taskStorage.GetAll()).Count().ShouldBe(0, "Respawn must empty the evertask-schema tables");
    }

    // ----------------------------------------------------------------------------------------------------
    // PHASE 2 — writable-CTE override behavior. These assert the CTE matches AuditPolicy EXACTLY.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    // SetStatus audit gate = AuditPolicy.ShouldCreateStatusAudit(level, status, exception).
    // Terminal Completed (no exception): only Full audits.
    [InlineData(AuditLevel.Full, QueuedTaskStatus.Completed, false, 1)]
    [InlineData(AuditLevel.Minimal, QueuedTaskStatus.Completed, false, 0)]
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Completed, false, 0)]
    [InlineData(AuditLevel.None, QueuedTaskStatus.Completed, false, 0)]
    // Failed (real error): Full/Minimal/ErrorsOnly audit, None never.
    [InlineData(AuditLevel.Full, QueuedTaskStatus.Failed, true, 1)]
    [InlineData(AuditLevel.Minimal, QueuedTaskStatus.Failed, true, 1)]
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Failed, true, 1)]
    [InlineData(AuditLevel.None, QueuedTaskStatus.Failed, true, 0)]
    public async Task SetStatus_CTE_creates_status_audit_iff_AuditPolicy_allows(
        AuditLevel level, QueuedTaskStatus status, bool withException, int expectedAudits)
    {
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress
        });
        var startAudits = _dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        var exception   = withException ? new InvalidOperationException("boom") : null;

        await _taskStorage.SetStatus(taskId, status, exception, level);

        var row = (await _taskStorage.Get(x => x.Id == taskId))[0];
        row.Status.ShouldBe(status);

        (_dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId) - startAudits)
            .ShouldBe(expectedAudits, "StatusAudit creation must match AuditPolicy.ShouldCreateStatusAudit exactly");
    }

    [Fact]
    public async Task SetStatus_CTE_does_not_audit_service_stopped_with_OperationCanceled_below_full()
    {
        // AuditPolicy.IsRealError: ServiceStopped carrying an OperationCanceledException is a clean shutdown,
        // NOT an error -> Minimal/ErrorsOnly must NOT audit it; Full always does.
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress
        });
        var start = _dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        await _taskStorage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped,
            new OperationCanceledException(), AuditLevel.Minimal);

        (_dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId) - start)
            .ShouldBe(0, "a clean ServiceStopped/OperationCanceled shutdown is not a real error below Full");
    }

    [Theory]
    // LastExecutionUtc is stamped only on terminal transitions; intermediate ones preserve the prior value.
    [InlineData(QueuedTaskStatus.WaitingQueue, false)]
    [InlineData(QueuedTaskStatus.Queued, false)]
    [InlineData(QueuedTaskStatus.InProgress, false)]
    [InlineData(QueuedTaskStatus.Cancelled, false)]
    [InlineData(QueuedTaskStatus.Pending, false)]
    [InlineData(QueuedTaskStatus.Completed, true)]
    [InlineData(QueuedTaskStatus.Failed, true)]
    [InlineData(QueuedTaskStatus.ServiceStopped, true)]
    public async Task SetStatus_CTE_stamps_LastExecutionUtc_only_on_terminal_transitions(
        QueuedTaskStatus status, bool shouldStamp)
    {
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            LastExecutionUtc = null
        });

        await _taskStorage.SetStatus(taskId, status, null, AuditLevel.Full);

        var row = (await _taskStorage.Get(x => x.Id == taskId))[0];
        if (shouldStamp)
            row.LastExecutionUtc.ShouldNotBeNull($"{status} is terminal: LastExecutionUtc must be stamped");
        else
            row.LastExecutionUtc.ShouldBeNull($"{status} is intermediate: LastExecutionUtc must be preserved");
    }

    [Theory]
    // UpdateCurrentRun RunsAudit gate = AuditPolicy.ShouldCreateRunsAudit(level, ROW.Status, ROW.Exception),
    // decided server-side in the CTE from the row's own Status/Exception (NOT a constant).
    // Row Status = Completed, no exception -> ErrorsOnly must NOT audit.
    [InlineData(AuditLevel.Full, QueuedTaskStatus.Completed, false, 1)]
    [InlineData(AuditLevel.Minimal, QueuedTaskStatus.Completed, false, 1)]
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Completed, false, 0)]
    [InlineData(AuditLevel.None, QueuedTaskStatus.Completed, false, 0)]
    // Row Status = Failed -> ErrorsOnly DOES audit (real error).
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Failed, false, 1)]
    // Row carries an exception -> ErrorsOnly DOES audit even if status not Failed.
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.InProgress, true, 1)]
    public async Task UpdateCurrentRun_CTE_creates_runs_audit_iff_AuditPolicy_allows(
        AuditLevel level, QueuedTaskStatus rowStatus, bool rowHasException, int expectedRuns)
    {
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status = rowStatus,
            Exception = rowHasException ? "some failure detail" : null,
            IsRecurring = true, CurrentRunCount = 0
        });
        var startRuns = _dbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(5));

        await _taskStorage.UpdateCurrentRun(taskId, 42.0, nextRun, level);

        var row = (await _taskStorage.Get(x => x.Id == taskId))[0];
        row.CurrentRunCount.ShouldBe(1, "the counter advances +1 regardless of the audit level");
        row.NextRunUtc.ShouldBe(nextRun, "NextRunUtc must be set by the CTE");

        (_dbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId) - startRuns)
            .ShouldBe(expectedRuns, "RunsAudit creation must match AuditPolicy.ShouldCreateRunsAudit (row-based)");
    }

    [Theory]
    // CompleteRecurringRun audits the CONSTANTS (Completed, null exception): StatusAudit at Full only,
    // RunsAudit at Full+Minimal. The counter always advances +1.
    [InlineData(AuditLevel.Full, 1, 1)]
    [InlineData(AuditLevel.Minimal, 0, 1)]
    [InlineData(AuditLevel.ErrorsOnly, 0, 0)]
    [InlineData(AuditLevel.None, 0, 0)]
    public async Task CompleteRecurringRun_CTE_creates_correct_audit_rows_per_level(
        AuditLevel level, int expectedStatusAudits, int expectedRunsAudits)
    {
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, CurrentRunCount = 0
        });
        var startStatus = _dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        var startRuns   = _dbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddHours(1));

        await _taskStorage.CompleteRecurringRun(taskId, 50.0, nextRun, level);

        var row = (await _taskStorage.Get(x => x.Id == taskId))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.CurrentRunCount.ShouldBe(1, "the counter always advances +1, never gated on the audit level");
        row.NextRunUtc.ShouldBe(nextRun, "NextRunUtc is assigned unconditionally");

        (_dbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId) - startStatus)
            .ShouldBe(expectedStatusAudits, "StatusAudit threshold (Full only) must match AuditPolicy");
        (_dbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId) - startRuns)
            .ShouldBe(expectedRunsAudits, "RunsAudit threshold (Full+Minimal) must match AuditPolicy");
    }

    [Fact]
    public async Task CompleteRecurringRun_CTE_with_null_nextRun_clears_NextRunUtc()
    {
        // The last occurrence of a bounded series passes nextRun = null: the CTE assigns it UNCONDITIONALLY
        // (never COALESCE), so a stale value cannot survive and resurrect a finished series at recovery.
        var taskId = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id = taskId, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, CurrentRunCount = 0,
            NextRunUtc = DateTimeOffset.UtcNow.AddMinutes(5),   // stale value that must NOT be preserved
            RunUntil   = DateTimeOffset.UtcNow.AddMinutes(30)
        });

        await _taskStorage.CompleteRecurringRun(taskId, 10.0, nextRun: null, AuditLevel.Full);

        var row = (await _taskStorage.Get(x => x.Id == taskId))[0];
        row.NextRunUtc.ShouldBeNull("NextRunUtc must be cleared, never preserved with COALESCE");

        var pending = await _taskStorage.RetrievePending(null, null, 100);
        pending.ShouldNotContain(t => t.Id == taskId, "a terminated series must never be revived by recovery");
    }

    protected override async Task CleanUpDatabase()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Respawn 7+ requires a DbConnection. CRITICAL: SchemasToInclude must list 'evertask' — the Postgres
        // adapter defaults to 'public' only and would otherwise clean NOTHING (state would leak across tests).
        // TablesToIgnore preserves __EFMigrationsHistory (it lives in 'evertask' via MigrationsHistoryTable),
        // otherwise the next Migrate() would re-run every migration on existing tables.
        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter        = DbAdapter.Postgres,
            SchemasToInclude = ["public", "evertask"],
            TablesToIgnore   = ["__EFMigrationsHistory"]
        });

        await _respawner.ResetAsync(connection);
    }

    protected override string InstallStatusAuditInsertFaultSql =>
        // RAISE aborts the whole statement, and the materialization IS one statement here: the occurrence
        // insert and the audit insert are two branches of the same writable CTE.
        $"""
         CREATE OR REPLACE FUNCTION evertask_audit_fault() RETURNS trigger LANGUAGE plpgsql AS $fault$
         BEGIN RAISE EXCEPTION 'injected audit fault'; END $fault$;
         CREATE TRIGGER trg_evertask_audit_fault BEFORE INSERT ON "{_dbContext.Schema}"."StatusAudit"
         FOR EACH ROW EXECUTE FUNCTION evertask_audit_fault();
         """;

    protected override string RemoveStatusAuditInsertFaultSql =>
        $"""
         DROP TRIGGER IF EXISTS trg_evertask_audit_fault ON "{_dbContext.Schema}"."StatusAudit";
         DROP FUNCTION IF EXISTS evertask_audit_fault();
         """;

    protected override string InstallScheduleAdvanceFaultSql =>
        // The writable CTE inserts the occurrence and updates the schedule in the same statement, so failing
        // the update aborts the pair — the window the plan names, expressed the only way this shape allows.
        $"""
         CREATE OR REPLACE FUNCTION evertask_advance_fault() RETURNS trigger LANGUAGE plpgsql AS $fault$
         BEGIN RAISE EXCEPTION 'injected advance fault'; END $fault$;
         CREATE TRIGGER trg_evertask_advance_fault BEFORE UPDATE ON "{_dbContext.Schema}"."QueuedTasks"
         FOR EACH ROW EXECUTE FUNCTION evertask_advance_fault();
         """;

    protected override string RemoveScheduleAdvanceFaultSql =>
        $"""
         DROP TRIGGER IF EXISTS trg_evertask_advance_fault ON "{_dbContext.Schema}"."QueuedTasks";
         DROP FUNCTION IF EXISTS evertask_advance_fault();
         """;

    protected override ITaskStoreDbContext CreateDbContext()
    {
        return _dbContext;
    }

    protected override ITaskStorage GetStorage()
    {
        return _taskStorage;
    }

    /// <summary>
    /// Override to use PostgreSQL-optimized GUID v7 generation (same v7 family as SQLite, byte-wise uuid
    /// sorting that matches .NET Guid.CompareTo — NOT the v8 SQL Server variant).
    /// </summary>
    protected override Guid GetGuidForProvider() => TestGuidGenerator.NewForPostgres();

    public void Dispose()
    {
        CleanUpDatabase().GetAwaiter().GetResult();
    }
}
