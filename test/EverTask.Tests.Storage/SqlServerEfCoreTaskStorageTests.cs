using System.Data.Common;
using EverTask.Abstractions;
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
using Testcontainers.MsSql;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("DatabaseTests")]
public class SqlServerEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IAsyncLifetime, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";
    private static MsSqlContainer? _sqlContainer;
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
        // Start container once for all tests in this collection
        lock (_lock)
        {
            if (!_containerInitialized)
            {
                _sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                _sqlContainer.StartAsync().GetAwaiter().GetResult();
                _containerInitialized = true;
            }
        }

        _connectionString = _sqlContainer!.GetConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqlServerEfCoreTaskStorageTests).Assembly))
                .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        // Apply migrations once
        lock (_lock)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SqlServerTaskStoreContext>();
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

    [Fact]
    public async Task CompleteRecurringRun_override_propagates_sql_failure_instead_of_swallowing()
    {
        // Residual D: the SQL Server override must PROPAGATE a failed EXEC (like UpdateCurrentRun), NOT
        // swallow it (like SetStatus) — a failed completion must not let the scheduler advance on
        // unpersisted state. Deterministic, self-contained failure injection: CurrentRunCount = int.MaxValue
        // makes the proc's `ISNULL(CurrentRunCount,0) + 1` overflow inside the EXEC (no schema mutation, so
        // subsequent tests in the collection still see the proc).
        var id = GetGuidForProvider();
        await _taskStorage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = "T",
            Request         = "{}",
            Handler         = "H",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.InProgress,
            IsRecurring     = true,
            CurrentRunCount = int.MaxValue
        });

        // Assert a DbException specifically: this proves the failure propagated from the DB layer (the EXEC),
        // not from some unrelated pre-EXEC C# fault, so the test genuinely exercises the propagate-on-persist-
        // failure contract.
        await Should.ThrowAsync<DbException>(
            () => _taskStorage.CompleteRecurringRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full));

        // The failed completion rolled back: the row stays recoverable (not advanced to Completed).
        var row = (await _taskStorage.Get(x => x.Id == id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.InProgress, "a rolled-back completion must not advance the status");
        row.CurrentRunCount.ShouldBe(int.MaxValue, "the counter must not advance on a failed persist");
    }

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
