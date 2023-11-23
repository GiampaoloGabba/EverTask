﻿using Microsoft.Extensions.DependencyInjection;
using EverTask.Storage.EfCore;
using EverTask.Storage;
using Microsoft.EntityFrameworkCore;

namespace EverTask.Tests.Storage.EfCore;

public class InMemoryEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;

    protected override void Initialize()
    {
        var services = new ServiceCollection();

        services.AddDbContext<TestDbContext>(options =>
            options.UseInMemoryDatabase("TestDatabase"));

        services.AddLogging();

        services.AddScoped<ITaskStoreDbContext>(provider => provider.GetRequiredService<TestDbContext>());
        services.AddSingleton<ITaskStorage, EfCoreTaskStorage>();

        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(InMemoryEfCoreTaskStorageTests).Assembly));

        _dbContext = services.BuildServiceProvider().GetRequiredService<TestDbContext>();
        _taskStorage = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
    }

    protected override async Task CleanUpDatabase()
    {
        _dbContext.QueuedTasks.RemoveRange(_dbContext.QueuedTasks.ToList());
        _dbContext.QueuedTaskStatusAudit.RemoveRange(_dbContext.QueuedTaskStatusAudit.ToList());
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
