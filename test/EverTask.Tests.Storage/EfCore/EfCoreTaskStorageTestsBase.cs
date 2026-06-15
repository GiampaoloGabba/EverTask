using EverTask.Abstractions;
using Xunit;
using EverTask.Storage.EfCore;
using EverTask.Storage;
using Shouldly;
using EverTask.Tests.TestHelpers;

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
            Id                    = GetGuidForProvider(),
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
            Id                    = GetGuidForProvider(),
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

    /// <summary>
    /// Generates a GUID optimized for the specific database provider being tested.
    /// Override in provider-specific tests to use database-optimized GUID generation.
    /// </summary>
    protected virtual Guid GetGuidForProvider() => TestGuidGenerator.New();

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
        Assert.Equal(QueuedTasks.Count, result.Count(x => queued.Any(y => y.Id == x.Id)));
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

        await _storage.RetrievePending(null, null, 100, CancellationToken.None);

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

        await _storage.SetQueued(result[0].Id, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        //using ToList because sqlite doesnt support order by DateTimeOffset
        _mockedDbContext.StatusAudit.ToList().OrderBy(x => x.UpdatedAtUtc)
                        .LastOrDefault(x => x.QueuedTaskId == result[0].Id)?.NewStatus
                        .ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_transition_only_recoverable_tasks()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = (await _storage.Get(x => x.Id == queued.Id))[0].Id;

        // Recoverable status: the conditional transition succeeds
        (await _storage.TrySetQueuedIfRecoverable(taskId, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(x => x.Id == taskId))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        // Terminal status (non-recurring Completed): the transition is REFUSED and the status
        // stays untouched — the startup recovery must never resurrect a finished task
        await _storage.SetCompleted(taskId, 10.0, AuditLevel.Full);
        (await _storage.TrySetQueuedIfRecoverable(taskId, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == taskId))[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_apply_RunUntil_and_MaxRuns_guards()
    {
        // The conditional transition must apply the SAME MaxRuns/RunUntil guards as RetrievePending
        // (canonical QueuedTask.IsRecoverable), on every provider: the atomic UPDATE on SQL Server,
        // the client-side override on SQLite, the client-side fallback on EF Core InMemory.
        var now = DateTimeOffset.UtcNow;

        // Recurring task between runs but past its RunUntil: must NOT be resurrected
        var pastRunUntil = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            CreatedAtUtc = now,
            Type         = "RecPastRunUntil", Request = "{}", Handler = "H",
            Status       = QueuedTaskStatus.Completed,
            IsRecurring  = true,
            NextRunUtc   = now.AddMinutes(5),
            RunUntil     = now.AddMinutes(-5)
        };
        await _storage.Persist(pastRunUntil);
        (await _storage.TrySetQueuedIfRecoverable(pastRunUntil.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == pastRunUntil.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Recurring task that exhausted MaxRuns: must NOT be resurrected
        var exhausted = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecMaxRuns", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            MaxRuns         = 3,
            CurrentRunCount = 4
        };
        await _storage.Persist(exhausted);
        (await _storage.TrySetQueuedIfRecoverable(exhausted.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == exhausted.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Recurring task within both bounds: recoverable
        var recoverable = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecOk", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            RunUntil        = now.AddMinutes(10),
            MaxRuns         = 10,
            CurrentRunCount = 1
        };
        await _storage.Persist(recoverable);
        (await _storage.TrySetQueuedIfRecoverable(recoverable.Id, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(x => x.Id == recoverable.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_SetTaskInProgress()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);
        result.ShouldNotBeEmpty();

        var taskId = result[0].Id;
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        await _storage.SetInProgress(taskId, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.InProgress);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == taskId)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.InProgress);
    }

    [Fact]
    public async Task Should_SetTaskCompleted()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetCompleted(result[0].Id, 100.0, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_PersistExecutionTimeMs_When_SetCompleted()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);
        result.Length.ShouldBeGreaterThan(0, "Task should exist after persist");
        const double expectedExecutionTime = 123.45;

        // Act
        await _storage.SetCompleted(result[0].Id, expectedExecutionTime, AuditLevel.Full);

        // Assert
        result = await _storage.Get(x => x.Id == queued.Id);
        result.Length.ShouldBeGreaterThan(0, "Task should still exist after SetCompleted");
        result[0].ExecutionTimeMs.ShouldBe(expectedExecutionTime, "ExecutionTimeMs should be persisted when setting task as completed");
    }

    [Fact]
    public async Task Should_PersistExecutionTimeMs_When_UpdateCurrentRun()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "RecurringTask",
            Request = "{}",
            Handler = "RecurringHandler",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsRecurring = true,
            CurrentRunCount = 0
        };
        await _storage.Persist(task);
        const double expectedExecutionTime = 234.56;
        var nextRun = DateTimeOffset.UtcNow.AddMinutes(5);

        // Act
        await _storage.UpdateCurrentRun(taskId, expectedExecutionTime, nextRun, AuditLevel.Full);

        // Assert
        var result = await _storage.Get(x => x.Id == taskId);
        result.Length.ShouldBeGreaterThan(0, "Task should exist after UpdateCurrentRun");
        result[0].ExecutionTimeMs.ShouldBe(expectedExecutionTime, "ExecutionTimeMs should be persisted when updating current run");

        // Verify RunsAudit also has ExecutionTimeMs
        var runsAudit = _mockedDbContext.RunsAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == taskId);
        runsAudit.ShouldNotBeNull("RunsAudit should be created");
        runsAudit.ExecutionTimeMs.ShouldBe(expectedExecutionTime, "RunsAudit should have ExecutionTimeMs");
    }

    [Fact]
    public async Task Should_SetTaskCancelledByUser()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);

        await _storage.SetCancelledByUser(result[0].Id, AuditLevel.Full);

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
        await _storage.SetCancelledByService(result[0].Id, exception, AuditLevel.Full);

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

        await _storage.UpdateCurrentRun(taskId, 100.0, null, AuditLevel.Full); // Aggiorna la corsa
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

        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_advance_counter_by_runsToAdvance_with_audit()
    {
        // F7/F8: occurrences skipped during downtime must count toward CurrentRunCount, so the
        // overload advances the counter by 1 + skipped (here 5) in ONE write (tracked path, audited).
        // On SQL Server this is the path the stored procedure delegates to the base implementation for.
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = DateTimeOffset.UtcNow.AddDays(1);

        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full, runsToAdvance: 5);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(5);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_advance_counter_by_runsToAdvance_without_audit()
    {
        // Same accounting on the AuditLevel.None server-side fast path (ExecuteUpdate, no SELECT).
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        await _storage.UpdateCurrentRun(taskId, 100.0, null, AuditLevel.None, runsToAdvance: 4);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(4);
    }

    [Fact]
    public async Task IncrementRecoveryFailure_should_count_and_ClearRecoveryFailure_should_reset()
    {
        // L18: persistent recovery re-dispatch failures are counted durably so the caller can poison
        // the task after a limit; a successful re-dispatch clears the counter.
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBeNull();

        (await _storage.IncrementRecoveryFailure(taskId)).ShouldBe(1);
        (await _storage.IncrementRecoveryFailure(taskId)).ShouldBe(2);
        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBe(2);

        await _storage.ClearRecoveryFailure(taskId);
        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteRecurringRun_Should_set_completed_and_advance_atomically()
    {
        // CU14/L29: a recurring occurrence's Completed status AND the run-counter / next-run advance must
        // be written in a single atomic operation (one transaction per provider).
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = DateTimeOffset.UtcNow.AddMinutes(30);

        await _storage.CompleteRecurringRun(taskId, 75.0, nextRun, runsToAdvance: 2, AuditLevel.Full);

        var task = (await _storage.Get(x => x.Id == taskId))[0];
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.CurrentRunCount.ShouldBe(2);
        task.NextRunUtc.ShouldBe(nextRun);
        task.ExecutionTimeMs.ShouldBe(75.0);
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
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Information",
                Message        = "Log 1",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Warning",
                Message        = "Log 2",
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
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Information",
                Message        = "First log",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(1),
                Level          = "Warning",
                Message        = "Second log",
                SequenceNumber = 1
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(2),
                Level          = "Error",
                Message        = "Third log",
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
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(i),
                Level          = "Information",
                Message        = $"Log {i}",
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
        var nonExistentTaskId = GetGuidForProvider();

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
                Id               = GetGuidForProvider(),
                TaskId           = queued.Id,
                TimestampUtc     = DateTimeOffset.UtcNow,
                Level            = "Error",
                Message          = "Error occurred",
                ExceptionDetails = exception.ToString(),
                SequenceNumber   = 0
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

    /// <summary>
    /// SMOKE TEST: Fast basic validation that pagination doesn't duplicate or lose tasks.
    /// Catches obvious bugs in keyset pagination logic.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Should_Page_Without_Duplicates()
    {
        // Arrange - Create 20 tasks with GUID v7 (time-ordered)
        var createdIds = new List<Guid>();
        var start      = DateTimeOffset.UtcNow;
        for (int i = 0; i < 20; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in two pages of 10
        var page1 = await _storage.RetrievePending(null, null, 10);
        var page2 = await _storage.RetrievePending(page1[^1].CreatedAtUtc, page1[^1].Id, 10);

        // Assert
        page1.Length.ShouldBe(10, "First page should have 10 tasks");
        page2.Length.ShouldBe(10, "Second page should have 10 tasks");

        // CRITICAL: Verify no overlap (duplicate IDs across pages)
        var page1Ids = page1.Select(t => t.Id).ToHashSet();
        var page2Ids = page2.Select(t => t.Id).ToHashSet();
        var overlap  = page1Ids.Intersect(page2Ids).ToList();
        overlap.ShouldBeEmpty($"Pages should not have overlapping IDs. Found duplicates: {string.Join(", ", overlap)}");

        // CRITICAL: Verify no missing tasks
        var allRetrieved = page1.Concat(page2).Select(t => t.Id).ToHashSet();
        allRetrieved.Count.ShouldBe(20, "Should retrieve all 20 unique tasks");

        var missingIds = createdIds.Except(allRetrieved).ToList();
        missingIds.ShouldBeEmpty(
            $"All created tasks should be retrieved. Missing IDs: {string.Join(", ", missingIds)}");
    }

    /// <summary>
    /// EDGE CASE TEST: Detects GUID v7 ordering inconsistencies between database and .NET.
    /// Catches bugs like SQL Server uniqueidentifier sorting differently than Guid.CompareTo().
    /// </summary>
    [Fact]
    public async Task RetrievePending_Database_Ordering_Should_Match_LINQ()
    {
        // Arrange - Create 10 tasks with GUID v7
        var taskIds = new List<Guid>();
        var start   = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            var taskId = GetGuidForProvider();
            taskIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve all in one page (to get database ordering)
        var dbOrdered = await _storage.RetrievePending(null, null, 100);

        // Assert - Database ordering should match LINQ OrderBy (only by CreatedAtUtc, GUID not used as tiebreaker)
        var linqOrdered = dbOrdered.OrderBy(t => t.CreatedAtUtc).ToArray();

        dbOrdered.Length.ShouldBe(linqOrdered.Length, "Should have same number of tasks");

        for (int i = 0; i < dbOrdered.Length; i++)
        {
            dbOrdered[i].Id.ShouldBe(linqOrdered[i].Id,
                $"DB ordering differs from LINQ at index {i}. " +
                $"DB ID: {dbOrdered[i].Id}, LINQ ID: {linqOrdered[i].Id}. " +
                $"This indicates GUID v7 CompareTo() behaves differently in database vs .NET!");
        }
    }

    /// <summary>
    /// STRESS TEST: Validates pagination performance and correctness with large dataset.
    /// Verifies no data loss or duplication across 10 pages.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Should_Handle_Large_Dataset()
    {
        // Arrange - Create 100 tasks
        var createdIds = new List<Guid>();
        var start      = DateTimeOffset.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in pages of 10
        var             allPages      = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid?           lastId        = null;

        for (int pageNum = 0; pageNum < 10; pageNum++)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            page.Length.ShouldBe(10, $"Page {pageNum + 1} should have 10 tasks");

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId        = page[^1].Id;
        }

        // Verify no more pages
        var emptyPage = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
        emptyPage.ShouldBeEmpty("Should have no tasks after last page");

        // Assert - All tasks retrieved, no duplicates
        allPages.Count.ShouldBe(100, "Should retrieve all 100 tasks");

        var retrievedIds = allPages.Select(t => t.Id).ToList();
        var uniqueIds    = retrievedIds.ToHashSet();
        uniqueIds.Count.ShouldBe(100,
            $"Should have 100 unique tasks. Found {retrievedIds.Count - uniqueIds.Count} duplicates");

        var missingIds = createdIds.Except(uniqueIds).ToList();
        missingIds.ShouldBeEmpty($"All created tasks should be retrieved. Missing: {missingIds.Count} tasks");
    }

    /// <summary>
    /// GOLD STANDARD TEST: Comprehensive keyset pagination correctness validation.
    /// This is the most thorough test, verifying ALL correctness properties:
    /// - Completeness (all tasks retrieved)
    /// - Uniqueness (no duplicates)
    /// - Monotonic ordering (CreatedAtUtc always increasing)
    /// - Chronological correctness (tasks in creation order)
    /// Uses unique timestamps to eliminate ordering ambiguity from secondary sort (Id).
    /// </summary>
    [Fact]
    public async Task RetrievePending_Keyset_Pagination_Should_Be_Perfect()
    {
        // Arrange - Create 50 tasks with UNIQUE CreatedAtUtc timestamps
        var createdTasks = new List<QueuedTask>();
        var start        = DateTimeOffset.UtcNow;

        for (int i = 0; i < 50; i++)
        {
            var task = new QueuedTask
            {
                Id      = GetGuidForProvider(),
                Type    = $"Task{i:D3}",
                Request = "{}",
                Handler = "Handler",
                // Wide spacing (100ms): ensures unique timestamps, avoids GUID tiebreaker
                // SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo(),
                // so tests must not rely on GUID ordering for correctness verification
                CreatedAtUtc = start.AddMilliseconds(i * 100),
                Status       = QueuedTaskStatus.Pending
            };
            createdTasks.Add(task);
            await _storage.Persist(task);
        }

        // Act - Retrieve all tasks via keyset pagination (pages of 10)
        var             allPages      = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid?           lastId        = null;

        while (true)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            if (page.Length == 0)
                break;

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId        = page[^1].Id;
        }

        // Assert 1: Completeness - all 50 tasks retrieved
        allPages.Count.ShouldBe(50, "Should retrieve all 50 tasks via pagination");

        // Assert 2: Uniqueness - no duplicate tasks
        var uniqueIds = allPages.Select(t => t.Id).ToHashSet();
        uniqueIds.Count.ShouldBe(50,
            $"Should have no duplicate tasks. Found {allPages.Count - uniqueIds.Count} duplicates");

        // Assert 3: Correctness - no missing tasks
        var missingIds = createdTasks.Select(t => t.Id).Except(uniqueIds).ToList();
        missingIds.ShouldBeEmpty($"All created tasks should be retrieved. Missing: {string.Join(", ", missingIds)}");

        // Assert 4: No extra tasks
        var extraIds = uniqueIds.Except(createdTasks.Select(t => t.Id)).ToList();
        extraIds.ShouldBeEmpty($"Should not retrieve non-existent tasks. Extra: {string.Join(", ", extraIds)}");

        // Assert 5: CRITICAL - Monotonic ordering by CreatedAtUtc
        for (int i = 1; i < allPages.Count; i++)
        {
            var prev = allPages[i - 1];
            var curr = allPages[i];

            (prev.CreatedAtUtc <= curr.CreatedAtUtc).ShouldBeTrue(
                $"CreatedAtUtc must be monotonically increasing. " +
                $"Task[{i - 1}].CreatedAtUtc={prev.CreatedAtUtc:O} (ID: {prev.Id}), " +
                $"Task[{i}].CreatedAtUtc={curr.CreatedAtUtc:O} (ID: {curr.Id}). " +
                $"Ordering violation detected!");
        }

        // Assert 6: CRITICAL - Chronological correctness (matches creation order)
        // Note: We only order by CreatedAtUtc since timestamps are unique (no GUID tiebreaker needed)
        var expectedOrder = createdTasks
                            .OrderBy(t => t.CreatedAtUtc)
                            .Select(t => t.Id)
                            .ToList();
        var actualOrder = allPages.Select(t => t.Id).ToList();

        for (int i = 0; i < expectedOrder.Count; i++)
        {
            actualOrder[i].ShouldBe(expectedOrder[i],
                $"Position {i}: expected {expectedOrder[i]} (created {createdTasks.First(t => t.Id == expectedOrder[i]).Type}), " +
                $"got {actualOrder[i]} (created {createdTasks.First(t => t.Id == actualOrder[i]).Type}). " +
                $"Pagination order does NOT match creation order!");
        }
    }

    #region AuditLevel Tests

    /// <summary>
    /// Tests for AuditLevel.Full - complete audit trail (default behavior)
    /// </summary>
    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_full_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.Queued
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull("LastExecutionUtc should be set for terminal status");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Full audit level should create StatusAudit record");

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_full_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test exception");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test exception");
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1);

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Failed);
        latestAudit.Exception.ShouldNotBeNull();
        latestAudit.Exception!.ShouldContain("Test exception");
    }

    /// <summary>
    /// Tests for AuditLevel.Minimal - only errors create StatusAudit, but always create RunsAudit for recurring
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_minimal_and_task_completes_successfully()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "Minimal audit level should NOT create StatusAudit for successful completion");
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_minimal_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test failure");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test failure");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Minimal audit level should create StatusAudit for failures");

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Failed);
        latestAudit.Exception.ShouldNotBeNull();
        latestAudit.Exception!.ShouldContain("Test failure");
    }

    [Fact]
    public async Task
        Should_not_create_status_audit_when_audit_level_is_minimal_and_task_is_service_stopped_without_exception()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - ServiceStopped without exception is a clean shutdown, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, null, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "Minimal audit level should NOT create StatusAudit for clean shutdown");
    }

    /// <summary>
    /// Tests for AuditLevel.ErrorsOnly - only errors create audit records
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_errors_only_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "ErrorsOnly audit level should NOT create StatusAudit for success");
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_errors_only_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Error test");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "ErrorsOnly audit level should create StatusAudit for failures");
    }

    /// <summary>
    /// Tests for ServiceStopped with OperationCanceledException (expected shutdown behavior)
    /// Should NOT create audit in ErrorsOnly/Minimal modes
    /// </summary>
    [Fact]
    public async Task
        Should_not_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_errors_only()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - OperationCanceledException during shutdown is expected, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "ErrorsOnly should NOT audit ServiceStopped with OperationCanceledException");
    }

    [Fact]
    public async Task
        Should_not_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_minimal()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - OperationCanceledException during shutdown is expected, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "Minimal should NOT audit ServiceStopped with OperationCanceledException");
    }

    /// <summary>
    /// Tests for ServiceStopped with other exceptions (real errors during shutdown)
    /// SHOULD create audit in ErrorsOnly/Minimal modes
    /// </summary>
    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_other_exception_and_errors_only()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Real error during shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Other exceptions during shutdown ARE errors
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "ErrorsOnly should audit ServiceStopped with real exceptions");
    }

    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_other_exception_and_minimal()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Real error during shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Other exceptions during shutdown ARE errors
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Minimal should audit ServiceStopped with real exceptions");
    }

    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_full()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Full audit level always creates audit
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Full audit level should always create StatusAudit");
    }

    /// <summary>
    /// Tests for AuditLevel.None - no audit trail at all
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_none_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "None audit level should NOT create StatusAudit");
    }

    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_none_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test failure");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test failure");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "None audit level should NOT create StatusAudit even for failures");
    }

    /// <summary>
    /// Tests for UpdateCurrentRun with different audit levels (recurring tasks)
    /// </summary>
    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_full_and_recurring_task_executes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1, "Full audit level should create RunsAudit record");
    }

    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_minimal_and_recurring_task_executes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1,
            "Minimal audit level should ALWAYS create RunsAudit to track last run");
    }

    [Fact]
    public async Task Should_not_create_runs_audit_when_audit_level_is_errors_only_and_recurring_task_succeeds()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount, "ErrorsOnly audit level should NOT create RunsAudit for successful runs");
    }

    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_errors_only_and_recurring_task_fails()
    {
        // Arrange
        var taskId    = GetGuidForProvider();
        var exception = new InvalidOperationException("Recurring task failure");

        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Failed,
            Exception       = exception.ToString(),
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1, "ErrorsOnly audit level should create RunsAudit for failed runs");
    }

    [Fact]
    public async Task Should_not_create_runs_audit_when_audit_level_is_none()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1, "CurrentRunCount should be incremented even with None audit level");

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount, "None audit level should NOT create RunsAudit");
    }

    /// <summary>
    /// Tests for terminal vs intermediate states with LastExecutionUtc
    /// </summary>
    [Fact]
    public async Task Should_update_last_execution_utc_for_terminal_states()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.InProgress,
            LastExecutionUtc = null
        });

        // Act - Completed is a terminal state
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].LastExecutionUtc.ShouldNotBeNull("Terminal states should set LastExecutionUtc");
    }

    [Fact]
    public async Task Should_not_update_last_execution_utc_for_intermediate_states()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Queued,
            LastExecutionUtc = null
        });

        // Act - InProgress is an intermediate state
        await _storage.SetStatus(taskId, QueuedTaskStatus.InProgress, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].LastExecutionUtc.ShouldBeNull("Intermediate states should NOT set LastExecutionUtc");
    }

    [Fact]
    public async Task Should_not_set_last_execution_utc_when_reverting_to_waiting_queue()
    {
        // Full-queue revert (TryQueue race): the task never ran, so LastExecutionUtc must stay null
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Queued,
            LastExecutionUtc = null
        });

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.WaitingQueue, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        task[0].LastExecutionUtc.ShouldBeNull("WaitingQueue revert must not stamp a fake execution time");
    }

    [Fact]
    public async Task Should_preserve_last_execution_utc_on_intermediate_transitions()
    {
        // Re-queueing a recurring task between runs must keep the timestamp of its last real run
        var taskId  = GetGuidForProvider();
        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Completed,
            IsRecurring      = true,
            LastExecutionUtc = lastRun
        });

        // Act - intermediate transition (next occurrence enqueued)
        await _storage.SetStatus(taskId, QueuedTaskStatus.Queued, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Queued);
        task[0].LastExecutionUtc.ShouldNotBeNull("intermediate transitions must PRESERVE the last run timestamp");
        (task[0].LastExecutionUtc!.Value - lastRun).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1),
            "the preserved value must be the previous run timestamp, not a new one");
    }

    #endregion

    #region RetrievePending recovery filter

    private async Task<QueuedTask> PersistTaskWithStatus(
        QueuedTaskStatus status,
        bool isRecurring = false,
        DateTimeOffset? nextRunUtc = null)
    {
        var task = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            Type         = "RecoveryFilterTask",
            Request      = "{}",
            Handler      = "RecoveryFilterHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status       = status,
            IsRecurring  = isRecurring,
            NextRunUtc   = nextRunUtc
        };
        await _storage.Persist(task);
        return task;
    }

    [Fact]
    public async Task RetrievePending_should_include_WaitingQueue_tasks()
    {
        // WaitingQueue = persisted but never delivered to a worker queue (parked in the
        // in-memory scheduler at shutdown, or dropped by a full queue with ThrowException).
        // Without this, delayed one-shot tasks are silently lost on restart.
        var task = await PersistTaskWithStatus(QueuedTaskStatus.WaitingQueue);

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task RetrievePending_should_include_recurring_tasks_between_runs()
    {
        // Between two runs a recurring task sits as Completed (or Failed after a bad run)
        // with a future NextRunUtc: it must be revived at startup.
        var completed = await PersistTaskWithStatus(QueuedTaskStatus.Completed,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));
        var failed = await PersistTaskWithStatus(QueuedTaskStatus.Failed,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldContain(t => t.Id == completed.Id);
        pending.ShouldContain(t => t.Id == failed.Id);
    }

    [Fact]
    public async Task RetrievePending_should_not_include_terminal_one_shot_tasks()
    {
        var completed = await PersistTaskWithStatus(QueuedTaskStatus.Completed);
        var failed    = await PersistTaskWithStatus(QueuedTaskStatus.Failed);
        var cancelled = await PersistTaskWithStatus(QueuedTaskStatus.Cancelled);

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldNotContain(t => t.Id == completed.Id);
        pending.ShouldNotContain(t => t.Id == failed.Id);
        pending.ShouldNotContain(t => t.Id == cancelled.Id);
    }

    [Fact]
    public async Task RetrievePending_should_not_include_recurring_tasks_without_next_run()
    {
        // Recurring task that exhausted its schedule: NextRunUtc is null, must not be revived.
        var exhausted = await PersistTaskWithStatus(QueuedTaskStatus.Completed,
            isRecurring: true, nextRunUtc: null);

        // A cancelled recurring task must never be revived, even with a future NextRunUtc.
        var cancelled = await PersistTaskWithStatus(QueuedTaskStatus.Cancelled,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldNotContain(t => t.Id == exhausted.Id);
        pending.ShouldNotContain(t => t.Id == cancelled.Id);
    }

    #endregion

    #region Audit consistency across providers (M8 batch A)

    private async Task<Guid> SeedSimpleTask(
        QueuedTaskStatus status = QueuedTaskStatus.InProgress, DateTimeOffset? lastExecutionUtc = null)
    {
        var task = new QueuedTask
        {
            Id               = GetGuidForProvider(),
            Type             = "AuditConsistencyTask",
            Request          = "{}",
            Handler          = "H",
            CreatedAtUtc     = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status           = status,
            LastExecutionUtc = lastExecutionUtc
        };
        await _storage.Persist(task);
        return task.Id;
    }

    [Fact]
    public async Task Should_audit_servicestopped_consistently_across_providers()
    {
        // F19/L29: a ServiceStopped carrying an OperationCanceledException (expected shutdown) or a null
        // exception must NOT be audited at Minimal/ErrorsOnly — same rule as MemoryTaskStorage.
        var cancelled = await SeedSimpleTask();
        await _storage.SetCancelledByService(cancelled, new OperationCanceledException(), AuditLevel.Minimal);

        var nullEx = await SeedSimpleTask();
        await _storage.SetStatus(nullEx, QueuedTaskStatus.ServiceStopped, null, AuditLevel.ErrorsOnly);

        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == cancelled && s.NewStatus == QueuedTaskStatus.ServiceStopped)
            .ShouldBe(0);
        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == nullEx && s.NewStatus == QueuedTaskStatus.ServiceStopped)
            .ShouldBe(0);
    }

    [Fact]
    public async Task Should_audit_recovery_queued_transition_consistently()
    {
        // L43: the recovery Queued transition is audited at Full on every provider.
        var id = await SeedSimpleTask(QueuedTaskStatus.WaitingQueue);

        (await _storage.TrySetQueuedIfRecoverable(id, AuditLevel.Full)).ShouldBeTrue();

        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == id && s.NewStatus == QueuedTaskStatus.Queued)
            .ShouldBe(1);
    }

    [Fact]
    public async Task Should_stamp_executedat_consistently()
    {
        // L28: RunsAudit.ExecutedAt is stamped at the moment of the update, not the older LastExecutionUtc.
        var id = await SeedSimpleTask(QueuedTaskStatus.Completed, DateTimeOffset.UtcNow.AddHours(-1));

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await _storage.UpdateCurrentRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(5), AuditLevel.Full);

        var executedAt = _mockedDbContext.RunsAudit
            .Where(r => r.QueuedTaskId == id)
            .ToList()
            .Select(r => r.ExecutedAt)
            .ShouldHaveSingleItem();
        executedAt.ShouldBeGreaterThan(before);
    }

    #endregion

    #region Audit cleanup retention (M6 G5/G6)

    private QueuedTask CompletedTask(DateTimeOffset finishedAt, params TaskExecutionLog[] logs)
    {
        var id = GetGuidForProvider();
        foreach (var log in logs)
            log.TaskId = id;

        return new QueuedTask
        {
            Id               = id,
            CreatedAtUtc     = finishedAt,
            LastExecutionUtc = finishedAt,
            Type             = "CleanupTask",
            Request          = "{}",
            Handler          = "CleanupHandler",
            Status           = QueuedTaskStatus.Completed,
            ExecutionLogs    = logs.ToList()
        };
    }

    [Fact]
    public async Task Should_not_delete_completed_task_within_retention_window()
    {
        // G5: a Completed non-recurring task with no audit rows must NOT be hard-deleted before it is
        // older than the retention window. Pre-fix the cleanup had no age gate and deleted it on the
        // very next cycle, losing the task record immediately.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 30,
            DeleteCompletedTasksAfterRetention = true
        };

        var recent = CompletedTask(now);                 // just finished → inside the 30-day window
        var aged   = CompletedTask(now.AddDays(-40));     // past the cutoff → legitimately deletable

        _mockedDbContext.QueuedTasks.AddRange(recent, aged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.DeleteCompletedTasksAsync(_mockedDbContext, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == recent.Id).ShouldBe(1, "recent task is within retention");
        _mockedDbContext.QueuedTasks.Count(x => x.Id == aged.Id).ShouldBe(0, "aged task past the cutoff is purged");
    }

    [Fact]
    public async Task Should_cascade_delete_execution_logs_when_purging_an_aged_task()
    {
        // Purging an aged-out completed task takes everything it owns with it, execution logs included —
        // that is what cleanup is for. The age gate (not a log guard) is what protects recent tasks.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 1,
            DeleteCompletedTasksAfterRetention = true
        };

        var logged = CompletedTask(now.AddDays(-10), new TaskExecutionLog
        {
            Id             = GetGuidForProvider(),
            TimestampUtc   = now.AddDays(-10),
            Level          = "Information",
            Message        = "execution log of an aged task",
            SequenceNumber = 0
        });

        _mockedDbContext.QueuedTasks.Add(logged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.DeleteCompletedTasksAsync(_mockedDbContext, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == logged.Id).ShouldBe(0, "aged task past the cutoff is purged");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == logged.Id).ShouldBe(0, "its logs cascade away with it");
    }

    [Fact]
    public async Task Should_not_delete_completed_tasks_when_no_retention_window_configured()
    {
        // G5 (core data-loss case): with the flag enabled but NO audit retention configured there is no
        // age cutoff, so an AuditLevel.None completed task (no audit rows) must be preserved — pre-fix it
        // was hard-deleted on the next cycle regardless of retention.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy { DeleteCompletedTasksAfterRetention = true };

        var task = CompletedTask(now.AddDays(-365)); // ancient, but no retention window means no cutoff

        _mockedDbContext.QueuedTasks.Add(task);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.DeleteCompletedTasksAsync(_mockedDbContext, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id).ShouldBe(1);
    }

    [Fact]
    public async Task Should_delete_completed_task_past_retention_window()
    {
        // Safety/regression: the legitimate cleanup must keep working — a Completed task with no audits
        // and no logs, older than the cutoff, is still purged after the fix.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 7,
            DeleteCompletedTasksAfterRetention = true
        };

        var aged = CompletedTask(now.AddDays(-30));

        _mockedDbContext.QueuedTasks.Add(aged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.DeleteCompletedTasksAsync(_mockedDbContext, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == aged.Id).ShouldBe(0);
    }

    [Fact]
    public async Task Should_use_longest_retention_window_as_age_cutoff()
    {
        // The cutoff is the MAX of the configured retention windows, so a task is preserved while ANY of
        // its audit categories could still exist. Here success=5d but errors=30d → a 10-day-old task is
        // still within the (30-day) cutoff and must survive.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 5,
            RunsAuditRetentionDays             = 5,
            ErrorAuditRetentionDays            = 30,
            DeleteCompletedTasksAfterRetention = true
        };

        var task = CompletedTask(now.AddDays(-10));

        _mockedDbContext.QueuedTasks.Add(task);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.DeleteCompletedTasksAsync(_mockedDbContext, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id).ShouldBe(1);
    }

    #endregion

    protected abstract Task CleanUpDatabase();
}
