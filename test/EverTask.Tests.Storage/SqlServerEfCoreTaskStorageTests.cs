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

    private async Task<Respawner> GetRespawner() =>
        _respawner ??= await Respawner.CreateAsync(_connectionString, new RespawnerOptions
                           { TablesToIgnore = ["__EFMigrationsHistory"] });

    protected override async Task CleanUpDatabase()
    {
        // Use Respawn for efficient, reliable cleanup
        var respawner = await GetRespawner();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
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
