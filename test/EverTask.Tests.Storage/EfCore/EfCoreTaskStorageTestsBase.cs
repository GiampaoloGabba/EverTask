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

    #endregion

    protected abstract Task CleanUpDatabase();
}
