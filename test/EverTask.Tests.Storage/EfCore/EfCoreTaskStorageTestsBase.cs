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

    [Fact]
    public async Task SaveExecutionLogsAsync_Should_PersistLogs()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "Log 1",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Warning",
                Message = "Log 2",
                SequenceNumber = 1
            }
        };

        // Act
        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Assert
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);
        savedLogs.Count.ShouldBe(2);
        savedLogs[0].Message.ShouldBe("Log 1");
        savedLogs[0].Level.ShouldBe("Information");
        savedLogs[1].Message.ShouldBe("Log 2");
        savedLogs[1].Level.ShouldBe("Warning");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_Should_ReturnLogsOrderedBySequenceNumber()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "First log",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                Level = "Warning",
                Message = "Second log",
                SequenceNumber = 1
            },
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(2),
                Level = "Error",
                Message = "Third log",
                SequenceNumber = 2
            }
        };

        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Act
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);

        // Assert
        savedLogs.Count.ShouldBe(3);
        savedLogs[0].SequenceNumber.ShouldBe(0);
        savedLogs[0].Message.ShouldBe("First log");
        savedLogs[1].SequenceNumber.ShouldBe(1);
        savedLogs[1].Message.ShouldBe("Second log");
        savedLogs[2].SequenceNumber.ShouldBe(2);
        savedLogs[2].Message.ShouldBe("Third log");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_WithPagination_Should_ReturnCorrectPage()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>();
        for (int i = 0; i < 10; i++)
        {
            logs.Add(new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(i),
                Level = "Information",
                Message = $"Log {i}",
                SequenceNumber = i
            });
        }

        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Act - get second page (skip 3, take 3)
        var page = await _storage.GetExecutionLogsAsync(queued.Id, skip: 3, take: 3, CancellationToken.None);

        // Assert
        page.Count.ShouldBe(3);
        page[0].SequenceNumber.ShouldBe(3);
        page[0].Message.ShouldBe("Log 3");
        page[1].SequenceNumber.ShouldBe(4);
        page[1].Message.ShouldBe("Log 4");
        page[2].SequenceNumber.ShouldBe(5);
        page[2].Message.ShouldBe("Log 5");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_ForNonExistentTask_Should_ReturnEmptyList()
    {
        // Arrange
        var nonExistentTaskId = Guid.NewGuid();

        // Act
        var logs = await _storage.GetExecutionLogsAsync(nonExistentTaskId, CancellationToken.None);

        // Assert
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveExecutionLogsAsync_WithExceptionDetails_Should_PersistExceptionInfo()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var exception = new InvalidOperationException("Test exception");
        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Error",
                Message = "Error occurred",
                ExceptionDetails = exception.ToString(),
                SequenceNumber = 0
            }
        };

        // Act
        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Assert
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);
        savedLogs.Count.ShouldBe(1);
        savedLogs[0].ExceptionDetails.ShouldNotBeNull();
        savedLogs[0].ExceptionDetails!.ShouldContain("Test exception");
        savedLogs[0].ExceptionDetails!.ShouldContain("InvalidOperationException");
    }

    protected abstract Task CleanUpDatabase();
}
