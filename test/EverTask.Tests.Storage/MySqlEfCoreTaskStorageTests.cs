#if !NET8_0
using EverTask.Abstractions;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.MySql;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Respawn;
using Shouldly;
using Testcontainers.MariaDb;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("DatabaseTests")]
public class MySqlEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IAsyncLifetime, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";
    private static MariaDbContainer? _mariaDbContainer;
    private static bool _containerInitialized = false;
    private static readonly object _lock = new();

    // The provider stores tables in the connection's database (MySQL "schema" == database). The container is
    // created with this database, so it is also the Respawn SchemasToInclude target and the index-check schema.
    private const string Database = "evertask";

    public async Task InitializeAsync()
    {
        // Clean database before each test
        await CleanUpDatabase();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected override void Initialize()
    {
        // Start container once for all tests in this collection. The image is PINNED (mariadb:10.11, the LTS the
        // provider targets): the Testcontainers default tag can drift between package versions and stall the
        // serial DatabaseTests queue on a slow first pull.
        lock (_lock)
        {
            if (!_containerInitialized)
            {
                _mariaDbContainer = new MariaDbBuilder("mariadb:10.11")
                    .WithDatabase(Database)
                    .Build();
                _mariaDbContainer.StartAsync().GetAwaiter().GetResult();
                _containerInitialized = true;
            }
        }

        _connectionString = _mariaDbContainer!.GetConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(MySqlEfCoreTaskStorageTests).Assembly))
                .AddMySqlStorage(_connectionString, opt =>
                {
                    opt.AutoApplyMigrations = true;
                    // Pin the version to skip auto-detect (the container is a known MariaDB 10.11).
                    opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11));
                });

        var serviceProvider = services.BuildServiceProvider();

        // Apply migrations once
        lock (_lock)
        {
            using var scope = serviceProvider.CreateScope();
            // The pooled factory does not register the concrete context for direct resolution; resolve the
            // scoped ITaskStoreDbContext (a real MySqlTaskStoreContext created via the factory) instead.
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
        // MySQL/MariaDB have no sub-database schema concept -> empty schema (tables live in the connection's db).
        _dbContext.Schema.ShouldBe("");
    }

    [Fact]
    public async Task Should_have_recovery_index_on_queued_tasks()
    {
        // IX_QueuedTasks_Recovery (CreatedAtUtc, Id) is what keeps RetrievePending off a filesort on large
        // tables. MySQL/MariaDB have no INCLUDE/partial index, so it is a plain composite (see the Initial
        // migration). information_schema.statistics has one row per index column, so COUNT(*) >= 1 means present.
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema = @db
              AND table_name = 'QueuedTasks'
              AND index_name = 'IX_QueuedTasks_Recovery'
            """;
        command.Parameters.AddWithValue("@db", Database);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        count.ShouldBeGreaterThan(0, "IX_QueuedTasks_Recovery should exist on the QueuedTasks table");
    }

    [Fact]
    public async Task TaskKey_unique_index_allows_multiple_null_keys()
    {
        // The shared model declares .HasIndex(TaskKey).IsUnique() WITHOUT HasFilter. MySQL/MariaDB unique
        // indexes already treat multiple NULLs as distinct, so two TaskKey = NULL rows must coexist.
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
        // Prove Respawn (DbAdapter.MySql + SchemasToInclude=[evertask]) actually empties the EverTask tables.
        await _taskStorage.Persist(new QueuedTask
        {
            Id = GetGuidForProvider(), Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.Pending
        });
        (await _taskStorage.GetAll()).Count().ShouldBeGreaterThan(0);

        await CleanUpDatabase();

        (await _taskStorage.GetAll()).Count().ShouldBe(0, "Respawn must empty the EverTask tables");
    }

    // ----------------------------------------------------------------------------------------------------
    // PHASE 2 — stored-procedure override behavior. These assert the procs match AuditPolicy EXACTLY.
    // (The whole inherited EfCoreTaskStorageTestsBase already routes SetStatus/UpdateCurrentRun/
    // CompleteRecurringRun through the procs; these add the audit-gate parity assertions.)
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
    public async Task SetStatus_proc_creates_status_audit_iff_AuditPolicy_allows(
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
    public async Task SetStatus_proc_does_not_audit_service_stopped_with_OperationCanceled_below_full()
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
    public async Task SetStatus_proc_stamps_LastExecutionUtc_only_on_terminal_transitions(
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
    // decided server-side in the proc from the row's own Status/Exception (NOT a constant).
    // Row Status = Completed, no exception -> ErrorsOnly must NOT audit.
    [InlineData(AuditLevel.Full, QueuedTaskStatus.Completed, false, 1)]
    [InlineData(AuditLevel.Minimal, QueuedTaskStatus.Completed, false, 1)]
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Completed, false, 0)]
    [InlineData(AuditLevel.None, QueuedTaskStatus.Completed, false, 0)]
    // Row Status = Failed -> ErrorsOnly DOES audit (real error).
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.Failed, false, 1)]
    // Row carries an exception -> ErrorsOnly DOES audit even if status not Failed.
    [InlineData(AuditLevel.ErrorsOnly, QueuedTaskStatus.InProgress, true, 1)]
    public async Task UpdateCurrentRun_proc_creates_runs_audit_iff_AuditPolicy_allows(
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
        row.NextRunUtc.ShouldBe(nextRun, "NextRunUtc must be set by the proc");

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
    public async Task CompleteRecurringRun_proc_creates_correct_audit_rows_per_level(
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
    public async Task CompleteRecurringRun_proc_with_null_nextRun_clears_NextRunUtc()
    {
        // The last occurrence of a bounded series passes nextRun = null: the proc assigns it UNCONDITIONALLY
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
        row.NextRunUtc.ShouldBeNull("NextRunUtc must be cleared, never preserved");

        var pending = await _taskStorage.RetrievePending(null, null, 100);
        pending.ShouldNotContain(t => t.Id == taskId, "a terminated series must never be revived by recovery");
    }

    protected override async Task CleanUpDatabase()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        // Respawn 7+ requires a DbConnection. For MySQL "schema" == database, so SchemasToInclude lists the
        // connection's database. TablesToIgnore preserves __EFMigrationsHistory, otherwise the next Migrate()
        // would re-run every migration on existing tables.
        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter        = DbAdapter.MySql,
            SchemasToInclude = [Database],
            TablesToIgnore   = ["__EFMigrationsHistory"]
        });

        await _respawner.ResetAsync(connection);
    }

    protected override ITaskStoreDbContext CreateDbContext() => _dbContext;

    protected override ITaskStorage GetStorage() => _taskStorage;

    /// <summary>
    /// MySQL/MariaDB store Guid as char(36): a UUIDv7 canonical string sorts temporally, matching the
    /// (CreatedAtUtc, Id) keyset — same v7 family as Postgres/SQLite, NOT the v8 SQL Server variant.
    /// </summary>
    protected override Guid GetGuidForProvider() => TestGuidGenerator.NewForMySql();

    public void Dispose() => CleanUpDatabase().GetAwaiter().GetResult();
}
#endif
