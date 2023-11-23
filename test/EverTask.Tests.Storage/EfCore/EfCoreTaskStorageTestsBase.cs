using Xunit;
using EverTask.EfCore;
using EverTask.Storage;
using Shouldly;

namespace EverTask.Tests.Storage.EfCore;

public abstract class EfCoreTaskStorageTestsBase
{
    private readonly List<QueuedTask> _queuedTasks;
    private readonly ITaskStorage _storage;
    private readonly ITaskStoreDbContext _mockedDbContext;

    protected abstract ITaskStoreDbContext CreateDbContext();
    protected abstract ITaskStorage GetStorage();

    public EfCoreTaskStorageTestsBase()
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
                /*StatusAudits = new List<StatusAudit>()
                {
                    new StatusAudit
                    {
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.Queued
                    },
                    new StatusAudit
                    {
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.InProgress
                    }
                }*/
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
                /*StatusAudits = new List<StatusAudit>()
                {
                    new StatusAudit
                    {
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = QueuedTaskStatus.Queued
                    }
                }*/
            },
        };

        Initialize();
        _storage         = GetStorage();
        _mockedDbContext = CreateDbContext();

        _storage.Persist(_queuedTasks[0]);
    }

    protected virtual void Initialize()
    {
    }

    [Fact]
    public async Task Get_ReturnsExpectedTasks()
    {
        await CleanUpDatabase();

        _mockedDbContext.QueuedTasks.AddRange(_queuedTasks);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        var result = await _storage.Get(x => x.Type == "Type1");

        result.Length.ShouldBe(1);
        result[0].Type.ShouldBe("Type1");
        result[0].Request.ShouldBe("Request1");
    }

    [Fact]
    public async Task GetAll_ReturnsAllTasks()
    {
        await CleanUpDatabase();
        _mockedDbContext.QueuedTasks.AddRange(_queuedTasks);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);
        var result = await _storage.GetAll();
        Assert.Equal(_queuedTasks.Count, result.Length);
    }

    [Fact]
    public async Task PersistTask_ShouldPersistTask()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[1]);
        _mockedDbContext.QueuedTasks.Count().ShouldBe(1);

        var result = await _storage.Get(x => x.Type == "Type2");

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type2");
    }

    [Fact]
    public async Task Should_RetrivePendingTasks()
    {
        await CleanUpDatabase();
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
        await CleanUpDatabase();
        await Task.Delay(1000);
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetQueued(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.OrderBy(x=>x.UpdatedAtUtc).LastOrDefault(x => x.QueuedTaskId == result[0].Id)?.NewStatus
                        .ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_SetTaskInProgress()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetInProgress(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.InProgress);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.InProgress);
    }

    [Fact]
    public async Task Should_SetTaskCompleted()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result         = await _storage.Get(x => x.Type == "Type1");
        var startingLength = _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetCompleted(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        _mockedDbContext.QueuedTaskStatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.QueuedTaskStatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_SetTaskCancelledByUser()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result = await _storage.Get(x => x.Type == "Type1");

        await _storage.SetCancelledByUser(result[0].Id);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_SetTaskCancelledByService()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var result = await _storage.Get(x => x.Type == "Type1");

        var exception = new Exception("Test Exception");
        await _storage.SetCancelledByService(result[0].Id, exception);

        result = await _storage.Get(x => x.Type == "Type1");
        result[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);
        result[0].Exception!.ShouldContain("Test Exception");
    }

    [Fact]
    public async Task GetCurrentRunCount_Should_ReturnCorrectCount()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var taskId = _queuedTasks[0].Id;

        var count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(0); // Inizialmente zero

        await _storage.UpdateCurrentRun(taskId, null); // Aggiorna la corsa
        count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(1); // Dovrebbe essere incrementato
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_UpdateRunCountAndNextRun()
    {
        await CleanUpDatabase();
        await _storage.Persist(_queuedTasks[0]);
        var taskId  = _queuedTasks[0].Id;
        var nextRun = DateTimeOffset.UtcNow.AddDays(1);

        await _storage.UpdateCurrentRun(taskId, nextRun);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    protected abstract Task CleanUpDatabase();
}
