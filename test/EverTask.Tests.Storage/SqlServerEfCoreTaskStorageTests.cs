﻿using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.SqlServer;
using EverTask.Tests.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

public class SqlServerEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private Respawner? _respawner;
    private string _connectionString = "";

    protected override void Initialize()
    {
        _connectionString = "Server=(localdb)\\mssqllocaldb;Database=EverTaskTestDb;Trusted_Connection=True;MultipleActiveResultSets=true";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt=>opt.RegisterTasksFromAssembly(typeof(SqlServerEfCoreTaskStorageTests).Assembly))
                .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
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
        _dbContext.RunsAudit.RemoveRange(_dbContext.RunsAudit.ToList());
        _dbContext.StatusAudit.RemoveRange(_dbContext.StatusAudit.ToList());
        _dbContext.QueuedTasks.RemoveRange(_dbContext.QueuedTasks.ToList());
        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    protected override ITaskStoreDbContext CreateDbContext()
    {
        return _dbContext;
    }

    protected override ITaskStorage GetStorage()
    {
        return _taskStorage;
    }
}
