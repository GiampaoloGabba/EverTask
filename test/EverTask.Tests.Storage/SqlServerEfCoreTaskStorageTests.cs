using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.SqlServer;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Respawn;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("StorageTests")]
public class SqlServerEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";
    private static bool _dbInitialized = false;
    private static readonly object _lock = new object();

    protected override void Initialize()
    {
        _connectionString = "Server=(localdb)\\mssqllocaldb;Database=EverTaskTestDb;Trusted_Connection=True;MultipleActiveResultSets=true";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt=>opt.RegisterTasksFromAssembly(typeof(SqlServerEfCoreTaskStorageTests).Assembly))
                .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = false);

        // Create database only once for all tests in this collection
        lock (_lock)
        {
            if (!_dbInitialized)
            {
                using var scope = services.BuildServiceProvider().CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<SqlServerTaskStoreContext>();
                // Don't delete - just ensure migrations are applied
                context.Database.Migrate();
                _dbInitialized = true;
            }
        }

        var serviceProvider = services.BuildServiceProvider();

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
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
        { TablesToIgnore = new Respawn.Graph.Table[] { "__EFMigrationsHistory" } });

    protected override async Task CleanUpDatabase()
    {
        // Use Respawn for efficient, reliable cleanup
        var respawner = await GetRespawner();
        using var connection = new SqlConnection(_connectionString);
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
