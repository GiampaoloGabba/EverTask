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
        var result = await _storage.Get(x => x.Id == queued.Id);
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
                Id = GetGuidForProvider(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "Log 1",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id = GetGuidForProvider(),
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
                Id = GetGuidForProvider(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "First log",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id = GetGuidForProvider(),
                TaskId = queued.Id,
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                Level = "Warning",
                Message = "Second log",
                SequenceNumber = 1
            },
            new TaskExecutionLog
            {
                Id = GetGuidForProvider(),
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
                Id = GetGuidForProvider(),
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
                Id = GetGuidForProvider(),
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

    /// <summary>
    /// SMOKE TEST: Fast basic validation that pagination doesn't duplicate or lose tasks.
    /// Catches obvious bugs in keyset pagination logic.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Should_Page_Without_Duplicates()
    {
        // Arrange - Create 20 tasks with GUID v7 (time-ordered)
        var createdIds = new List<Guid>();
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 20; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id = taskId,
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
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
        var overlap = page1Ids.Intersect(page2Ids).ToList();
        overlap.ShouldBeEmpty($"Pages should not have overlapping IDs. Found duplicates: {string.Join(", ", overlap)}");

        // CRITICAL: Verify no missing tasks
        var allRetrieved = page1.Concat(page2).Select(t => t.Id).ToHashSet();
        allRetrieved.Count.ShouldBe(20, "Should retrieve all 20 unique tasks");

        var missingIds = createdIds.Except(allRetrieved).ToList();
        missingIds.ShouldBeEmpty($"All created tasks should be retrieved. Missing IDs: {string.Join(", ", missingIds)}");
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
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            var taskId = GetGuidForProvider();
            taskIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id = taskId,
                Type = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
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
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id = taskId,
                Type = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in pages of 10
        var allPages = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;

        for (int pageNum = 0; pageNum < 10; pageNum++)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            page.Length.ShouldBe(10, $"Page {pageNum + 1} should have 10 tasks");

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId = page[^1].Id;
        }

        // Verify no more pages
        var emptyPage = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
        emptyPage.ShouldBeEmpty("Should have no tasks after last page");

        // Assert - All tasks retrieved, no duplicates
        allPages.Count.ShouldBe(100, "Should retrieve all 100 tasks");

        var retrievedIds = allPages.Select(t => t.Id).ToList();
        var uniqueIds = retrievedIds.ToHashSet();
        uniqueIds.Count.ShouldBe(100, $"Should have 100 unique tasks. Found {retrievedIds.Count - uniqueIds.Count} duplicates");

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
        var start = DateTimeOffset.UtcNow;

        for (int i = 0; i < 50; i++)
        {
            var task = new QueuedTask
            {
                Id = GetGuidForProvider(),
                Type = $"Task{i:D3}",
                Request = "{}",
                Handler = "Handler",
                // Wide spacing (100ms): ensures unique timestamps, avoids GUID tiebreaker
                // SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo(),
                // so tests must not rely on GUID ordering for correctness verification
                CreatedAtUtc = start.AddMilliseconds(i * 100),
                Status = QueuedTaskStatus.Pending
            };
            createdTasks.Add(task);
            await _storage.Persist(task);
        }

        // Act - Retrieve all tasks via keyset pagination (pages of 10)
        var allPages = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;

        while (true)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            if (page.Length == 0)
                break;

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId = page[^1].Id;
        }

        // Assert 1: Completeness - all 50 tasks retrieved
        allPages.Count.ShouldBe(50, "Should retrieve all 50 tasks via pagination");

        // Assert 2: Uniqueness - no duplicate tasks
        var uniqueIds = allPages.Select(t => t.Id).ToHashSet();
        uniqueIds.Count.ShouldBe(50, $"Should have no duplicate tasks. Found {allPages.Count - uniqueIds.Count} duplicates");

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

    protected abstract Task CleanUpDatabase();
}
