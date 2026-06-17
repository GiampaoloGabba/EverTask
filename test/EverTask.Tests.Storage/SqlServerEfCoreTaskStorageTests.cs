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
