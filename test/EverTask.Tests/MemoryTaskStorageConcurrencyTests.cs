using EverTask.Tests.TestHelpers;
using EverTask.Logger;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class MemoryTaskStorageConcurrencyTests
{
    private readonly MemoryTaskStorage _storage;
    private readonly Mock<IEverTaskLogger<MemoryTaskStorage>> _mockLogger;

    public MemoryTaskStorageConcurrencyTests()
    {
        _mockLogger = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        _storage = new MemoryTaskStorage(_mockLogger.Object);
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Persist_Operations()
    {
        // Arrange
        const int taskCount = 1000;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending
            })
            .ToArray();

        // Act - Persist tasks concurrently using Parallel.ForEach
        await Parallel.ForEachAsync(tasks, async (task, ct) =>
        {
            await _storage.Persist(task, ct);
        });

        // Assert - All tasks should be persisted without exceptions or data loss
        var allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(taskCount, "All tasks should be persisted");

        // Verify all original task IDs are present
        var persistedIds = allTasks.Select(t => t.Id).ToHashSet();
        foreach (var task in tasks)
        {
            persistedIds.ShouldContain(task.Id, $"Task {task.Id} should be in storage");
        }
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Read_Write_Operations()
    {
        // Arrange
        const int operationCount = 500;
        var initialTasks = Enumerable.Range(0, 100)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"InitialTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending
            })
            .ToArray();

        // Pre-populate storage
        foreach (var task in initialTasks)
        {
            await _storage.Persist(task);
        }

        // Act - Mix reads and writes concurrently
        var exception = await Record.ExceptionAsync(async () =>
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, operationCount), async (i, ct) =>
            {
                if (i % 2 == 0)
                {
                    // Read operation
                    var allTasks = await _storage.GetAll(ct);
                    allTasks.Length.ShouldBeGreaterThanOrEqualTo(initialTasks.Length);
                }
                else
                {
                    // Write operation
                    var newTask = new QueuedTask
                    {
                        Id = TestGuidGenerator.New(),
                        Type = $"ConcurrentTask{i}",
                        Request = "{}",
                        Handler = "TestHandler",
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        Status = QueuedTaskStatus.Pending
                    };
                    await _storage.Persist(newTask, ct);
                }
            });
        });

        // Assert - No exceptions should be thrown (especially InvalidOperationException)
        exception.ShouldBeNull("Concurrent read/write operations should not throw exceptions");

        // Verify final state
        var finalTasks = await _storage.GetAll();
        finalTasks.Length.ShouldBeGreaterThanOrEqualTo(initialTasks.Length, "At least initial tasks should remain");
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Status_Updates()
    {
        // Arrange
        const int taskCount = 200;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"StatusTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending
            })
            .ToArray();

        // Persist all tasks
        foreach (var task in tasks)
        {
            await _storage.Persist(task);
        }

        // Act - Update status concurrently
        await Parallel.ForEachAsync(tasks, async (task, ct) =>
        {
            await _storage.SetStatus(task.Id, QueuedTaskStatus.Queued, null, AuditLevel.Full, 100, ct);
            await _storage.SetStatus(task.Id, QueuedTaskStatus.InProgress, null, AuditLevel.Full, 100, ct);
            await _storage.SetStatus(task.Id, QueuedTaskStatus.Completed, null, AuditLevel.Full, 100, ct);
        });

        // Assert - All tasks should be in Completed status
        var allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(taskCount);

        foreach (var task in allTasks)
        {
            task.Status.ShouldBe(QueuedTaskStatus.Completed, $"Task {task.Id} should be completed");

            // Verify status audit trail
            task.StatusAudits.Count.ShouldBeGreaterThanOrEqualTo(3, "Should have at least 3 status audit entries");
        }
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Updates_And_Removals()
    {
        // Arrange
        const int taskCount = 300;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"RemovalTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending
            })
            .ToArray();

        // Persist all tasks
        foreach (var task in tasks)
        {
            await _storage.Persist(task);
        }

        // Act - Concurrently update some tasks and remove others
        await Parallel.ForEachAsync(tasks.Select((t, index) => (task: t, index)), async (item, ct) =>
        {
            if (item.index % 2 == 0)
            {
                // Update status
                await _storage.SetStatus(item.task.Id, QueuedTaskStatus.InProgress, null, AuditLevel.Full, 100, ct);
            }
            else
            {
                // Remove task
                await _storage.Remove(item.task.Id, ct);
            }
        });

        // Assert
        var remainingTasks = await _storage.GetAll();

        // Approximately half should remain (those that were updated, not removed)
        remainingTasks.Length.ShouldBe(taskCount / 2, "Half of tasks should remain after removal");

        // All remaining tasks should be in InProgress status
        foreach (var task in remainingTasks)
        {
            task.Status.ShouldBe(QueuedTaskStatus.InProgress);
        }
    }

    [Fact]
    public async Task Should_Handle_Concurrent_GetByTaskKey_Operations()
    {
        // Arrange
        const int taskCount = 100;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"KeyedTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending,
                TaskKey = $"key-{i}"
            })
            .ToArray();

        // Persist all tasks
        foreach (var task in tasks)
        {
            await _storage.Persist(task);
        }

        // Act - Concurrently query by task key
        var exception = await Record.ExceptionAsync(async () =>
        {
            await Parallel.ForEachAsync(tasks, async (task, ct) =>
            {
                var foundTask = await _storage.GetByTaskKey(task.TaskKey!, ct);
                foundTask.ShouldNotBeNull($"Task with key {task.TaskKey} should be found");
                foundTask.Id.ShouldBe(task.Id);
            });
        });

        // Assert
        exception.ShouldBeNull("Concurrent GetByTaskKey operations should not throw exceptions");
    }

    [Fact]
    public async Task Should_Handle_Concurrent_UpdateCurrentRun_Operations()
    {
        // Arrange
        const int taskCount = 100;
        const int runsPerTask = 10;

        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => new QueuedTask
            {
                Id = TestGuidGenerator.New(),
                Type = $"RecurringTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = QueuedTaskStatus.Pending,
                IsRecurring = true,
                CurrentRunCount = 0
            })
            .ToArray();

        // Persist all tasks
        foreach (var task in tasks)
        {
            await _storage.Persist(task);
        }

        // Act - Concurrently update run counters
        await Parallel.ForEachAsync(tasks, async (task, ct) =>
        {
            for (int run = 0; run < runsPerTask; run++)
            {
                var nextRun = DateTimeOffset.UtcNow.AddMinutes(run + 1);
                await _storage.UpdateCurrentRun(task.Id, 100.0, nextRun, AuditLevel.Full);
            }
        });

        // Assert
        var allTasks = await _storage.GetAll();

        foreach (var task in allTasks)
        {
            task.CurrentRunCount.ShouldBe(runsPerTask, $"Task {task.Id} should have {runsPerTask} runs");
            task.RunsAudits.Count.ShouldBe(runsPerTask, $"Task {task.Id} should have {runsPerTask} audit entries");
        }
    }
}
