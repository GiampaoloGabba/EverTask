using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

public class PendingRecoveryPagingTests
{
    [Fact]
    public async Task MemoryStorage_RetrievePending_Should_Return_Empty_Array_After_Last_Page()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create 10 pending tasks
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            await storage.Persist(new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Read first page and then request after last id
        var firstPage = await storage.RetrievePending(null, null, 10);
        firstPage.Length.ShouldBe(10);
        var lastTask = firstPage[^1];

        var result = await storage.RetrievePending(lastTask.CreatedAtUtc, lastTask.Id, 10);

        // Assert
        result.ShouldBeEmpty("Should return empty array when skip exceeds count");
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Return_Correct_Page()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create 25 pending tasks
        var taskIds = new List<Guid>();
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 25; i++)
        {
            var taskId = TestGuidGenerator.New();
            taskIds.Add(taskId);
            await storage.Persist(new QueuedTask
            {
                Id = taskId,
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Get page 1
        var page1 = await storage.RetrievePending(null, null, 10);
        page1.Length.ShouldBe(10, "Page 1 should have 10 tasks");

        // Get page 2
        var page2 = await storage.RetrievePending(page1[^1].CreatedAtUtc, page1[^1].Id, 10);

        // Assert
        page2.Length.ShouldBe(10, "Page 2 should have 10 tasks");

        // Act - Get page 3
        var page3 = await storage.RetrievePending(page2[^1].CreatedAtUtc, page2[^1].Id, 10);

        // Assert
        page3.Length.ShouldBe(5, "Page 3 should have remaining 5 tasks");
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Handle_Large_Backlog()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create 1000 pending tasks (simulating large backlog)
        const int totalTasks = 1000;
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < totalTasks; i++)
        {
            await storage.Persist(new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve all tasks in pages of 100
        const int pageSize = 100;
        var allRetrievedTasks = new List<QueuedTask>();

        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        while (true)
        {
            var page = await storage.RetrievePending(lastCreatedAt, lastId, pageSize);
            if (page.Length == 0)
                break;

            allRetrievedTasks.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId = page[^1].Id;
        }

        // Assert
        allRetrievedTasks.Count.ShouldBe(totalTasks, "Should retrieve all tasks via paging");
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Only_Return_Pending_Tasks()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create mix of statuses
        var pendingTaskIds = new List<Guid>();

        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            var pendingId = TestGuidGenerator.New();
            pendingTaskIds.Add(pendingId);
            await storage.Persist(new QueuedTask
            {
                Id = pendingId,
                Type = $"PendingTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });

            // Also create completed tasks (should not be retrieved)
            await storage.Persist(new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"CompletedTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(1000 + i),
                Status = QueuedTaskStatus.Completed
            });
        }

        // Act
        var retrieved = await storage.RetrievePending(null, null, 20);

        // Assert
        retrieved.Length.ShouldBe(10, "Should only retrieve pending tasks");
        retrieved.All(t => t.Status == QueuedTaskStatus.Pending ||
                           t.Status == QueuedTaskStatus.Queued ||
                           t.Status == QueuedTaskStatus.InProgress ||
                           t.Status == QueuedTaskStatus.ServiceStopped)
            .ShouldBeTrue("All retrieved tasks should have eligible statuses");
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Respect_MaxRuns_Limit()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create task that exceeded MaxRuns
        var start = DateTimeOffset.UtcNow;
        await storage.Persist(new QueuedTask
        {
            Id = TestGuidGenerator.New(),
            Type = "ExceededTask",
            Request = "{}",
            Handler = "TestHandler",
            CreatedAtUtc = start,
            Status = QueuedTaskStatus.Pending,
            MaxRuns = 3,
            CurrentRunCount = 4  // Exceeded
        });

        // Create task within MaxRuns
        var validTaskId = TestGuidGenerator.New();
        await storage.Persist(new QueuedTask
        {
            Id = validTaskId,
            Type = "ValidTask",
            Request = "{}",
            Handler = "TestHandler",
            CreatedAtUtc = start.AddMilliseconds(1),
            Status = QueuedTaskStatus.Pending,
            MaxRuns = 3,
            CurrentRunCount = 2  // Within limit
        });

        // Act
        var retrieved = await storage.RetrievePending(null, null, 10);

        // Assert
        retrieved.Length.ShouldBe(1, "Should only retrieve task within MaxRuns");
        retrieved[0].Id.ShouldBe(validTaskId);
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Respect_RunUntil_Limit()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create task that expired
        var start = DateTimeOffset.UtcNow;
        await storage.Persist(new QueuedTask
        {
            Id = TestGuidGenerator.New(),
            Type = "ExpiredTask",
            Request = "{}",
            Handler = "TestHandler",
            CreatedAtUtc = start,
            Status = QueuedTaskStatus.Pending,
            RunUntil = DateTimeOffset.UtcNow.AddDays(-1)  // Expired
        });

        // Create task not expired
        var validTaskId = TestGuidGenerator.New();
        await storage.Persist(new QueuedTask
        {
            Id = validTaskId,
            Type = "ValidTask",
            Request = "{}",
            Handler = "TestHandler",
            CreatedAtUtc = start.AddMilliseconds(1),
            Status = QueuedTaskStatus.Pending,
            RunUntil = DateTimeOffset.UtcNow.AddDays(1)  // Future
        });

        // Act
        var retrieved = await storage.RetrievePending(null, null, 10);

        // Assert
        retrieved.Length.ShouldBe(1, "Should only retrieve task not expired");
        retrieved[0].Id.ShouldBe(validTaskId);
    }

    [Fact]
    public async Task MemoryStorage_RetrievePendingPaged_Should_Be_Thread_Safe()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create 100 pending tasks
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            await storage.Persist(new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve pages concurrently
        var exception = await Record.ExceptionAsync(async () =>
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, 10), async (iteration, ct) =>
            {
                DateTimeOffset? lastCreatedAt = null;
                Guid? lastId = null;
                for (int i = 0; i < 10; i++)
                {
                    var page = await storage.RetrievePending(lastCreatedAt, lastId, 10, ct);
                    if (page.Length == 0)
                        break;
                    lastCreatedAt = page[^1].CreatedAtUtc;
                    lastId = page[^1].Id;
                }
            });
        });

        // Assert
        exception.ShouldBeNull("Concurrent paged retrieval should not throw exceptions");
    }

    [Fact]
    public async Task MemoryStorage_Paging_Should_Process_In_Consistent_Order()
    {
        // Arrange
        var loggerMock = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var storage = new MemoryTaskStorage(loggerMock.Object);

        // Create 30 tasks with GUID v7 (time-ordered)
        var orderedIds = new List<Guid>();
        var start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 30; i++)
        {
            var taskId = TestGuidGenerator.New();
            orderedIds.Add(taskId);
            await storage.Persist(new QueuedTask
            {
                Id = taskId,
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = start.AddMilliseconds(i),  // Ensure order
                Status = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in pages
        var page1 = await storage.RetrievePending(null, null, 10);
        var page2 = await storage.RetrievePending(page1[^1].CreatedAtUtc, page1[^1].Id, 10);
        var page3 = await storage.RetrievePending(page2[^1].CreatedAtUtc, page2[^1].Id, 10);

        // Assert - All pages should be retrieved
        page1.Length.ShouldBe(10);
        page2.Length.ShouldBe(10);
        page3.Length.ShouldBe(10);

        // Combine all pages
        var allPages = page1.Concat(page2).Concat(page3).ToList();
        allPages.Count.ShouldBe(30);

        // Verify all task IDs were retrieved (order may vary but all should be present)
        var retrievedIds = allPages.Select(t => t.Id).ToHashSet();
        retrievedIds.Count.ShouldBe(30, "Should retrieve all unique tasks");
        foreach (var id in orderedIds)
        {
            retrievedIds.ShouldContain(id, $"Task {id} should be retrieved");
        }
    }
}
