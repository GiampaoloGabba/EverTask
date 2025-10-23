using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EverTask.Tests.TestHelpers;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.IntegrationTests;

public class LogCaptureIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_CaptureAndStoreLogs_WhenEnabled()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskWithLogs("test data"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldNotBeEmpty();
        logs.Count.ShouldBe(2);
        logs[0].Message.ShouldBe("Processing task with data: test data");
        logs[0].Level.ShouldBe("Information");
        logs[1].Message.ShouldBe("Task processing completed");
        logs[1].Level.ShouldBe("Information");

        // Verify sequence numbers
        logs[0].SequenceNumber.ShouldBe(0);
        logs[1].SequenceNumber.ShouldBe(1);

        // Verify timestamps are UTC
        logs[0].TimestampUtc.Offset.ShouldBe(TimeSpan.Zero);
        logs[1].TimestampUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Should_NotStoreLogs_WhenDisabled()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log.Disable()); // DISABLED
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskWithLogs("test data"));

        // Debug: Check what's in storage immediately
        await Task.Delay(100);
        var allTasks = await Storage.GetAll();
        if (allTasks.Length > 0)
        {
            var task = allTasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                // Task exists, check its status
                System.Console.WriteLine($"Task Status: {task.Status}");
            }
            else
            {
                System.Console.WriteLine("Task not found in storage!");
            }
        }
        else
        {
            System.Console.WriteLine("Storage is empty!");
        }

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // Assert
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_StoreLogs_EvenWhenTaskFails()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
                // Default retry policy: 3 retries (1 initial + 3 retries = 4 attempts total)
                // 3 logs per attempt × 4 attempts = 12 logs total
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskThatFailsWithLogs("test data"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Assert - logs should be captured for ALL retry attempts (including failures)
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldNotBeEmpty();
        logs.Count.ShouldBe(12); // 3 logs × 4 attempts (1 initial + 3 retries)

        // Verify first attempt logs
        logs[0].Message.ShouldBe("Starting task with data: test data");
        logs[0].Level.ShouldBe("Information");
        logs[1].Message.ShouldBe("About to throw exception");
        logs[1].Level.ShouldBe("Warning");
        logs[2].Message.ShouldBe("Throwing test exception");
        logs[2].Level.ShouldBe("Error");

        // Verify second attempt logs (first retry)
        logs[3].Message.ShouldBe("Starting task with data: test data");
        logs[3].Level.ShouldBe("Information");
        logs[4].Message.ShouldBe("About to throw exception");
        logs[4].Level.ShouldBe("Warning");
        logs[5].Message.ShouldBe("Throwing test exception");
        logs[5].Level.ShouldBe("Error");

        // Verify all attempts have the same log pattern
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 3;
            logs[offset].Message.ShouldBe("Starting task with data: test data");
            logs[offset].Level.ShouldBe("Information");
            logs[offset + 1].Message.ShouldBe("About to throw exception");
            logs[offset + 1].Level.ShouldBe("Warning");
            logs[offset + 2].Message.ShouldBe("Throwing test exception");
            logs[offset + 2].Level.ShouldBe("Error");
        }
    }

    [Fact]
    public async Task Should_FilterLogsByLevel()
    {
        // Arrange - only Warning and above
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Warning));
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskMultiLevelLogs());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert - only Warning, Error, Critical should be captured
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldNotBeEmpty();
        logs.Count.ShouldBe(3);
        logs[0].Level.ShouldBe("Warning");
        logs[0].Message.ShouldBe("This is a warning message");
        logs[1].Level.ShouldBe("Error");
        logs[1].Message.ShouldBe("This is an error message");
        logs[2].Level.ShouldBe("Critical");
        logs[2].Message.ShouldBe("This is a critical message");
    }

    [Fact]
    public async Task Should_CaptureAllLevels_WhenMinimumIsTrace()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Trace));
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskMultiLevelLogs());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert - all levels should be captured
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.Count.ShouldBe(6);
        logs[0].Level.ShouldBe("Trace");
        logs[1].Level.ShouldBe("Debug");
        logs[2].Level.ShouldBe("Information");
        logs[3].Level.ShouldBe("Warning");
        logs[4].Level.ShouldBe("Error");
        logs[5].Level.ShouldBe("Critical");
    }

    [Fact]
    public async Task Should_RespectMaxLogsPerTask()
    {
        // Arrange - max 10 logs
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information)
                    .SetMaxLogsPerTask(10));
            });

        // Act - task logs 50 messages
        var taskId = await Dispatcher.Dispatch(new TestTaskManyLogs(50));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert - only first 10 logs should be captured
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.Count.ShouldBe(10);
        logs[0].Message.ShouldBe("Log message 1 of 50");
        logs[9].Message.ShouldBe("Log message 10 of 50");
    }

    [Fact]
    public async Task Should_CaptureExceptionDetails()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskLogWithException());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.Count.ShouldBe(3);

        // First log: no exception
        logs[0].Message.ShouldBe("Starting task");
        logs[0].ExceptionDetails.ShouldBeNull();

        // Second log: with exception
        logs[1].Message.ShouldBe("Caught exception");
        logs[1].Level.ShouldBe("Error");
        logs[1].ExceptionDetails.ShouldNotBeNull();
        logs[1].ExceptionDetails!.ShouldContain("InvalidOperationException");
        logs[1].ExceptionDetails!.ShouldContain("Inner exception");

        // Third log: no exception
        logs[2].Message.ShouldBe("Task completed");
        logs[2].ExceptionDetails.ShouldBeNull();
    }

    [Fact]
    public async Task Should_AssociateLogsWithCorrectTask()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
            });

        // Act - dispatch multiple tasks
        var taskId1 = await Dispatcher.Dispatch(new TestTaskWithLogs("task1"));
        var taskId2 = await Dispatcher.Dispatch(new TestTaskWithLogs("task2"));

        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - each task should have its own logs
        var logs1 = await Storage.GetExecutionLogsAsync(taskId1, CancellationToken.None);
        var logs2 = await Storage.GetExecutionLogsAsync(taskId2, CancellationToken.None);

        logs1.ShouldNotBeEmpty();
        logs2.ShouldNotBeEmpty();

        logs1.ShouldAllBe(log => log.TaskId == taskId1);
        logs2.ShouldAllBe(log => log.TaskId == taskId2);

        logs1[0].Message.ShouldContain("task1");
        logs2[0].Message.ShouldContain("task2");
    }

    [Fact]
    public async Task Should_HandleNullMaxLogs()
    {
        // Arrange - null = unlimited
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information)
                    .SetMaxLogsPerTask(null)); // Unlimited
            });

        // Act - task logs 200 messages
        var taskId = await Dispatcher.Dispatch(new TestTaskManyLogs(200));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert - all 200 logs should be captured
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.Count.ShouldBe(200);
        logs[0].Message.ShouldBe("Log message 1 of 200");
        logs[199].Message.ShouldBe("Log message 200 of 200");
    }

    [Fact]
    public async Task Should_NotImpactPerformance_WhenDisabled()
    {
        // Arrange - log capture disabled
        await CreateIsolatedHostAsync(
            channelCapacity: 30,
            maxDegreeOfParallelism: 10, // Run tasks in parallel
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log.Disable());
            });

        var start = DateTimeOffset.UtcNow;

        // Act - dispatch many tasks
        var taskIds = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var taskId = await Dispatcher.Dispatch(new TestTaskManyLogs(40));
            taskIds.Add(taskId);
        }

        // Wait for all to complete
        foreach (var taskId in taskIds)
        {
            await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs:10000);
        }

        var elapsed = DateTimeOffset.UtcNow - start;

        // Assert - should complete quickly (no log overhead)
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));

        // Verify no logs stored
        foreach (var taskId in taskIds)
        {
            var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
            logs.ShouldBeEmpty();
        }
    }
}
