using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Sqlite;
using EverTask.Tests.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

[Collection("StorageTests")]
public class SqliteEfCoreTaskStorageTests : EfCoreTaskStorageTestsBase, IDisposable
{
    private ITaskStoreDbContext _dbContext = null!;
    private ITaskStorage _taskStorage = null!;
    private string _connectionString = "";

    protected override void Initialize()
    {
        _connectionString = "Data Source=EverTask.db";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqliteEfCoreTaskStorageTests).Assembly))
                .AddSqliteStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        _dbContext   = serviceProvider.GetService<ITaskStoreDbContext>()!;
        _taskStorage = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        Assert.NotNull(_dbContext);
        Assert.NotNull(_taskStorage);
        _dbContext.Schema.ShouldBe("");
    }

    protected override async Task CleanUpDatabase()
    {
        _dbContext.TaskExecutionLogs.RemoveRange(_dbContext.TaskExecutionLogs.ToList());
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

    /// <summary>
    /// Override to use SQLite-optimized GUID v7 generation.
    /// SQLite uses byte-by-byte lexicographic sorting for BLOB/TEXT.
    /// </summary>
    protected override Guid GetGuidForProvider() => TestGuidGenerator.NewForSqlite();

    public void Dispose()
    {
        CleanUpDatabase().GetAwaiter().GetResult();
    }
}
