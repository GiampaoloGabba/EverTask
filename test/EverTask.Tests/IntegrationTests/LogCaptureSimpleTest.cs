using EverTask.Tests.TestHelpers;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.IntegrationTests;

public class LogCaptureSimpleTest : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_CaptureLogsWhenEnabled()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TaskThatLogs("test-data"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldNotBeEmpty();
        logs.Count.ShouldBe(2);
        logs[0].Message.ShouldBe("Processing data: test-data");
        logs[1].Message.ShouldBe("Processing completed");
    }

    [Fact]
    public async Task Should_NotCaptureLogsWhenDisabled()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log.Disable());
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TaskThatLogs("test"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_CaptureLogsEvenWhenTaskFails()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            configureEverTask: cfg =>
            {
                cfg.WithPersistentLogger(log => log
                    .SetMinimumLevel(LogLevel.Information));
                // Default retry policy: 3 retries (1 initial + 3 retries = 4 attempts total)
                // 2 logs per attempt × 4 attempts = 8 logs total
            });

        // Act
        var taskId = await Dispatcher.Dispatch(new TaskThatFailsWithLogs());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Assert - logs should include ALL retry attempts
        var logs = await Storage.GetExecutionLogsAsync(taskId, CancellationToken.None);
        logs.ShouldNotBeEmpty();
        logs.Count.ShouldBe(8); // 2 logs × 4 attempts (1 initial + 3 retries)

        // Verify first attempt logs
        logs[0].Message.ShouldBe("Task starting");
        logs[1].Message.ShouldBe("About to fail");

        // Verify second attempt logs (first retry)
        logs[2].Message.ShouldBe("Task starting");
        logs[3].Message.ShouldBe("About to fail");

        // Verify all attempts have correct messages
        for (int i = 0; i < 4; i++)
        {
            logs[i * 2].Message.ShouldBe("Task starting");
            logs[i * 2 + 1].Message.ShouldBe("About to fail");
        }
    }
}
