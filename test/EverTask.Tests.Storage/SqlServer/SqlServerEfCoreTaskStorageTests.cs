using EverTask.EfCore;
using EverTask.Storage;
using EverTask.Tests.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Respawn;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.SqlServer;

public class SqlServerEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner _respawner = null!;
    private string _connectionString = "";

    protected override void Initialize()
    {
        _connectionString = "Server=(localdb)\\mssqllocaldb;Database=EverTaskTestDb;Trusted_Connection=True;MultipleActiveResultSets=true";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt=>opt.RegisterTasksFromAssembly(typeof(ServiceRegistrationTests).Assembly))
                .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
    }

    protected override async Task CleanUpDatabase()
    {
        _respawner = await Respawner.CreateAsync(_connectionString, new RespawnerOptions
        {
            TablesToIgnore = new Respawn.Graph.Table[] { "__EFMigrationsHistory" }
        });

        await _respawner.ResetAsync(_connectionString);
        await Task.Delay(700);
    }

    protected override ITaskStoreDbContext CreateDbContext()
    {
        return _dbContext;
    }

    protected override ITaskStorage GetStorage()
    {
        return _taskStorage;
    }

    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        _dbContext.Schema.ShouldBe("EverTask");
    }
}
