using Xunit;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using EverTask.EfCore;
using EverTask.Logger;
using EverTask.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EverTask.Tests.Storage.EfCore;

public class EfCoreTaskStorageTests
{
    private readonly List<QueuedTask> _queuedTasks;
    private readonly ITaskStorage _storage;
    private readonly TestDbContext _mockedDbContext;

    public EfCoreTaskStorageTests()
    {
        _queuedTasks = new List<QueuedTask>
        {
            new QueuedTask
            {
                Id                    = Guid.NewGuid(),
                CreatedAtUtc          = DateTimeOffset.UtcNow,
                LastExecutionUtc      = DateTimeOffset.UtcNow,
                ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddDays(1),
                Type                  = "Type1",
                Request               = "Request1",
                Handler               = "Handler1",
                Status                = QueuedTaskStatus.InProgress,
                StatusAudits = new List<StatusAudit>()
                {
                    new StatusAudit
                    {
                        Id           = 1,
                        QueuedTaskId = Guid.NewGuid(),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.Queued
                    },
                    new StatusAudit
                    {
                        Id           = 2,
                        QueuedTaskId = Guid.NewGuid(),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.InProgress
                    }
                }
            },
            new QueuedTask
            {
                Id                    = Guid.NewGuid(),
                CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(1),
                LastExecutionUtc      = DateTimeOffset.UtcNow.AddMinutes(1),
                ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1),
                Type                  = "Type2",
                Request               = "Request2",
                Handler               = "Handler2",
                Status                = QueuedTaskStatus.Queued,
                StatusAudits = new List<StatusAudit>()
                {
                    new StatusAudit
                    {
                        Id           = 10,
                        QueuedTaskId = Guid.NewGuid(),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.Queued
                    }
                }
            },
        };


        var services = new ServiceCollection();

        services.AddDbContext<TestDbContext>(options =>
            options.UseInMemoryDatabase("TestDatabase"));

        services.AddLogging();
        services.AddScoped<ITaskStoreDbContext>(provider => provider.GetRequiredService<TestDbContext>());
        services.AddSingleton<ITaskStorage, EfCoreTaskStorage>();

        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(EfCoreTaskStorageTests).Assembly));

        _storage         = services.BuildServiceProvider().GetRequiredService<ITaskStorage>();
        _mockedDbContext = services.BuildServiceProvider().GetRequiredService<TestDbContext>();

        _storage.Persist(_queuedTasks[0]);
    }

    [Fact]
    public async Task Get_ReturnsExpectedTasks()
    {
        CleanUpDatabase();
        _mockedDbContext.QueuedTasks.AddRange(_queuedTasks);
        await _mockedDbContext.SaveChangesAsync();

        var result = await _storage.Get(x => x.Type == "Type1");

        result.Length.ShouldBe(1);
        result[0].Type.ShouldBe("Type1");
        result[0].Request.ShouldBe("Request1");
    }

    [Fact]
    public async Task GetAll_ReturnsAllTasks()
    {
        CleanUpDatabase();
        _mockedDbContext.QueuedTasks.AddRange(_queuedTasks);
        await _mockedDbContext.SaveChangesAsync();
        var result = await _storage.GetAll();
        Assert.Equal(_queuedTasks.Count, result.Length);
    }

    [Fact]
    public async Task PersistTask_ShouldPersistTask()
    {
        CleanUpDatabase();
        await _storage.Persist(_queuedTasks[1]);
        _mockedDbContext.QueuedTasks.Count().ShouldBe(1);

        var result = await _storage.Get(x => x.Type == "Type2");

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type2");
    }

    [Fact]
    public async Task Should_RetrivePendingTasks()
    {
        CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        await _storage.RetrievePending();
        _mockedDbContext.QueuedTasks.Count().ShouldBe(1);

        var result = await _storage.Get(x => x.Type == "Type1");

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type1");
    }

    [Fact]
    public async Task Should_SetTaskQueued()
    {
        CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id);

        await _storage.SetQueued(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.LastOrDefault(x=>x.QueuedTaskId==result[0].Id)?.NewStatus.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_SetTaskInProgress()
    {
        CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id);

        await _storage.SetInProgress(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.InProgress);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.OrderBy(x=>x.Id).LastOrDefault(x=>x.QueuedTaskId==result[0].Id)?.NewStatus.ShouldBe(QueuedTaskStatus.InProgress);
    }

    [Fact]
    public async Task Should_SetTaskCompleted()
    {
        CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id);

        await _storage.SetCompleted(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x=>x.QueuedTaskId==result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.OrderBy(x=>x.Id).LastOrDefault(x=>x.QueuedTaskId==result[0].Id)?.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    private void CleanUpDatabase()
    {
        _mockedDbContext.QueuedTasks.RemoveRange(_mockedDbContext.QueuedTasks);
        _mockedDbContext.QueuedTaskStatusAudit.RemoveRange(_mockedDbContext.QueuedTaskStatusAudit);
        _mockedDbContext.SaveChanges();
    }
}
