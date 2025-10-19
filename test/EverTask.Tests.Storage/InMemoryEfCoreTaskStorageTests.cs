using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Tests.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("StorageTests")]
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

        // Register factory using ServiceScopeDbContextFactory for backward compatibility
        services.AddSingleton<ITaskStoreDbContextFactory, ServiceScopeDbContextFactory>();
        services.AddSingleton<ITaskStorage, EfCoreTaskStorage>();

        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(InMemoryEfCoreTaskStorageTests).Assembly));

        var provider = services.BuildServiceProvider();
        _dbContext = provider.GetRequiredService<TestDbContext>();
        _taskStorage = provider.GetRequiredService<ITaskStorage>();
    }

    protected override async Task CleanUpDatabase()
    {
        _dbContext.QueuedTasks.RemoveRange(_dbContext.QueuedTasks.ToList());
        _dbContext.StatusAudit.RemoveRange(_dbContext.StatusAudit.ToList());
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
