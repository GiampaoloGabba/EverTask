using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Provides intelligent polling-based wait helpers for integration tests to avoid timing issues with fixed Task.Delay
/// </summary>
public static class TaskWaitHelper
{
    private const int DefaultTimeoutMs = 5000;
    private const int DefaultPollingIntervalMs = 50;

    /// <summary>
    /// Waits until a condition is met or timeout is reached using intelligent polling
    /// </summary>
    /// <typeparam name="T">Return type of the getter function</typeparam>
    /// <param name="getter">Function to retrieve the value to check</param>
    /// <param name="condition">Condition to evaluate on the retrieved value</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds</param>
    /// <param name="pollingIntervalMs">Interval between checks in milliseconds</param>
    /// <returns>The value that satisfied the condition</returns>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout</exception>
    public static async Task<T> WaitUntilAsync<T>(
        Func<Task<T>> getter,
        Func<T, bool> condition,
        int timeoutMs = DefaultTimeoutMs,
        int pollingIntervalMs = DefaultPollingIntervalMs)
    {
        var startTime = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow - startTime < timeout)
        {
            var value = await getter();
            if (condition(value))
            {
                return value;
            }

            await Task.Delay(pollingIntervalMs);
        }

        throw new TimeoutException($"Condition was not met within {timeoutMs}ms");
    }

    /// <summary>
    /// Waits until a condition is met or timeout is reached using intelligent polling (synchronous condition check)
    /// </summary>
    public static async Task WaitForConditionAsync(
        Func<bool> condition,
        int timeoutMs = DefaultTimeoutMs,
        int pollingIntervalMs = DefaultPollingIntervalMs)
    {
        var startTime = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow - startTime < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(pollingIntervalMs);
        }

        throw new TimeoutException($"Condition was not met within {timeoutMs}ms");
    }

    /// <summary>
    /// Waits until a task in storage reaches the expected status
    /// </summary>
    /// <param name="storage">Task storage instance</param>
    /// <param name="taskId">ID of the task to monitor</param>
    /// <param name="expectedStatus">Expected status to wait for</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds</param>
    /// <returns>The task from storage with the expected status</returns>
    public static async Task<QueuedTask> WaitForTaskStatusAsync(
        ITaskStorage storage,
        Guid taskId,
        QueuedTaskStatus expectedStatus,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitUntilAsync(
            async () =>
            {
                var tasks = await storage.GetAll();
                return tasks.FirstOrDefault(t => t.Id == taskId);
            },
            task => task != null && task.Status == expectedStatus,
            timeoutMs
        ) ?? throw new InvalidOperationException($"Task {taskId} not found in storage");
    }

    /// <summary>
    /// Waits until a task exists in storage (useful for checking task creation)
    /// </summary>
    public static async Task<QueuedTask> WaitForTaskExistsAsync(
        ITaskStorage storage,
        Guid taskId,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitUntilAsync(
            async () =>
            {
                var tasks = await storage.GetAll();
                return tasks.FirstOrDefault(t => t.Id == taskId);
            },
            task => task != null,
            timeoutMs
        ) ?? throw new InvalidOperationException($"Task {taskId} was not created within timeout");
    }

    /// <summary>
    /// Waits until a storage query returns the expected number of items
    /// </summary>
    public static async Task<QueuedTask[]> WaitForTaskCountAsync(
        ITaskStorage storage,
        int expectedCount,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitUntilAsync(
            async () => await storage.GetAll(),
            tasks => tasks.Length == expectedCount,
            timeoutMs
        );
    }

    /// <summary>
    /// Waits until pending tasks count reaches expected value
    /// </summary>
    public static async Task<QueuedTask[]> WaitForPendingCountAsync(
        ITaskStorage storage,
        int expectedCount,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitUntilAsync(
            async () => await storage.RetrievePending(null,  null, 10),
            tasks => tasks.Length == expectedCount,
            timeoutMs
        );
    }

    /// <summary>
    /// Waits until a counter reaches expected value (for test tasks with counters)
    /// </summary>
    public static async Task WaitForCounterAsync(
        Func<int> getCounter,
        int expectedValue,
        int timeoutMs = DefaultTimeoutMs)
    {
        await WaitForConditionAsync(
            () => getCounter() == expectedValue,
            timeoutMs
        );
    }

    /// <summary>
    /// Waits for a recurring task to complete a specific number of runs
    /// </summary>
    public static async Task<QueuedTask> WaitForRecurringRunsAsync(
        ITaskStorage storage,
        Guid taskId,
        int expectedRuns,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitUntilAsync(
            async () =>
            {
                var tasks = await storage.GetAll();
                return tasks.FirstOrDefault(t => t.Id == taskId);
            },
            task =>
            {
                if (task == null) return false;

                try
                {
                    // Create a snapshot to avoid "Collection was modified" exception
                    // when RunsAudits is being modified by background threads.
                    // If the collection is modified during ToArray(), catch the exception
                    // and return false to retry the polling.
                    var audits = task.RunsAudits.ToArray();
                    var completedCount = audits.Count(x => x != null && x.Status == QueuedTaskStatus.Completed);

                    // IMPORTANT: Wait for BOTH RunsAudits AND CurrentRunCount to be updated
                    // There's a race condition where RunsAudits is updated before CurrentRunCount
                    var currentRunCount = task.CurrentRunCount ?? 0;
                    return completedCount >= expectedRuns && currentRunCount >= expectedRuns;
                }
                catch (ArgumentException)
                {
                    // Collection was modified during ToArray() - retry on next poll
                    return false;
                }
            },
            timeoutMs
        ) ?? throw new InvalidOperationException($"Task {taskId} not found in storage");
    }

    /// <summary>
    /// Waits for task to reach expected status AND for execution logs to be persisted.
    /// Use this to avoid race conditions where status is updated before logs are saved.
    /// </summary>
    public static async Task<(QueuedTask Task, IReadOnlyList<TaskExecutionLog> Logs)> WaitForTaskStatusWithLogsAsync(
        ITaskStorage storage,
        Guid taskId,
        QueuedTaskStatus expectedStatus,
        int expectedLogCount,
        int timeoutMs = DefaultTimeoutMs)
    {
        // First wait for task to reach expected status
        var task = await WaitForTaskStatusAsync(storage, taskId, expectedStatus, timeoutMs);

        // Then wait for logs to be persisted (small additional timeout)
        var logs = await WaitUntilAsync(
            async () => await storage.GetExecutionLogsAsync(taskId, CancellationToken.None),
            logList => logList.Count >= expectedLogCount,
            timeoutMs: 2000, // Additional 2s for log persistence
            pollingIntervalMs: 50
        );

        return (task, logs);
    }

    /// <summary>
    /// Waits for task to complete AND for execution logs to be persisted.
    /// Convenience method for completed tasks.
    /// </summary>
    public static async Task<(QueuedTask Task, IReadOnlyList<TaskExecutionLog> Logs)> WaitForTaskCompletionWithLogsAsync(
        ITaskStorage storage,
        Guid taskId,
        int expectedLogCount,
        int timeoutMs = DefaultTimeoutMs)
    {
        return await WaitForTaskStatusWithLogsAsync(storage, taskId, QueuedTaskStatus.Completed, expectedLogCount, timeoutMs);
    }
}
