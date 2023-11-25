using Xunit;
using EverTask.Storage.EfCore;
using EverTask.Storage;
using Shouldly;

namespace EverTask.Tests.Storage.EfCore;

public abstract class EfCoreTaskStorageTestsBase
{
    private readonly ITaskStorage _storage;
    private readonly ITaskStoreDbContext _mockedDbContext;

    protected abstract ITaskStoreDbContext CreateDbContext();
    protected abstract ITaskStorage GetStorage();

    public EfCoreTaskStorageTestsBase()
    {
        Initialize();
        _storage         = GetStorage();
        _mockedDbContext = CreateDbContext();
    }

    public List<QueuedTask> QueuedTasks =>
    [
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
        }
    ];


    protected virtual void Initialize()
    {
    }

    [Fact]
    public async Task Get_ReturnsExpectedTasks()
    {
        var queued = QueuedTasks;
        _mockedDbContext.QueuedTasks.AddRange(queued);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        var result = await _storage.Get(x => x.Id == queued.FirstOrDefault()!.Id);

        result.Length.ShouldBe(1);
        result[0].Type.ShouldBe("Type1");
        result[0].Request.ShouldBe("Request1");
    }

    [Fact]
    public async Task GetAll_ReturnsAllTasks()
    {
        var queued = QueuedTasks;
        _mockedDbContext.QueuedTasks.AddRange(queued);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);
        var result = await _storage.GetAll();
        Assert.Equal(QueuedTasks.Count, result.Count(x=>queued.Any(y=>y.Id == x.Id)));
    }

    [Fact]
    public async Task PersistTask_ShouldPersistTask()
    {
        var queued = QueuedTasks[1];
        await _storage.Persist(queued);
        _mockedDbContext.QueuedTasks.Count(x => x.Id == queued.Id).ShouldBe(1);

        var result = await _storage.Get(x => x.Id == queued.Id);

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type2");
    }

    [Fact]
    public async Task Should_RetrivePendingTasks()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        await _storage.RetrievePending();
        _mockedDbContext.QueuedTasks.Count(x => x.Id == queued.Id).ShouldBe(1);

        var result = await _storage.Get(x => x.Id == queued.Id);

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type1");
    }

    [Fact]
    public async Task Should_SetTaskQueued()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetQueued(result[0].Id);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        //using ToList because sqlite doesnt support order by DateTimeOffset
        _mockedDbContext.StatusAudit.ToList().OrderBy(x=>x.UpdatedAtUtc).LastOrDefault(x => x.QueuedTaskId == result[0].Id)?.NewStatus
                        .ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_SetTaskInProgress()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetInProgress(result[0].Id);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.InProgress);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.InProgress);
    }

    [Fact]
    public async Task Should_SetTaskCompleted()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetCompleted(result[0].Id);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_SetTaskCancelledByUser()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);

        await _storage.SetCancelledByUser(result[0].Id);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_SetTaskCancelledByService()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);

        var exception = new Exception("Test Exception");
        await _storage.SetCancelledByService(result[0].Id, exception);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);
        result[0].Exception!.ShouldContain("Test Exception");
    }

    [Fact]
    public async Task GetCurrentRunCount_Should_ReturnCorrectCount()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        var count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(0); // Inizialmente zero

        await _storage.UpdateCurrentRun(taskId, null); // Aggiorna la corsa
        count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(1); // Dovrebbe essere incrementato
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_UpdateRunCountAndNextRun()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = DateTimeOffset.UtcNow.AddDays(1);

        await _storage.UpdateCurrentRun(taskId, nextRun);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    protected abstract Task CleanUpDatabase();
}
