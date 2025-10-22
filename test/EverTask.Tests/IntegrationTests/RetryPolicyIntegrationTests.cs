using EverTask.Abstractions;
using EverTask.Resilience;
using EverTask.Storage;
using EverTask.Monitoring;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for retry policy enhancements covering real-world scenarios.
/// These tests verify the end-to-end behavior of OnRetry callbacks and exception filtering.
/// </summary>
public class RetryPolicyIntegrationTests : IsolatedIntegrationTestBase
{
    // No constructor or instance fields needed - IsolatedIntegrationTestBase provides everything

    #region Scenario 1: OnRetry Callback Invocation

    public record OnRetryCallbackTask(int FailTimes) : IEverTask;

    public class OnRetryCallbackHandler : EverTaskHandler<OnRetryCallbackTask>
    {
        private static int _attemptCount = 0;
        private static readonly ConcurrentBag<RetryAttempt> _retryAttempts = new();

        public class RetryAttempt
        {
            public Guid TaskId { get; set; }
            public int AttemptNumber { get; set; }
            public string ExceptionMessage { get; set; } = string.Empty;
            public TimeSpan Delay { get; set; }
        }

        public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(50));

        public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
        {
            _retryAttempts.Add(new RetryAttempt
            {
                TaskId = taskId,
                AttemptNumber = attemptNumber,
                ExceptionMessage = exception.Message,
                Delay = delay
            });
            return ValueTask.CompletedTask;
        }

        public override async Task Handle(OnRetryCallbackTask task, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);

            if (attempt <= task.FailTimes)
            {
                throw new InvalidOperationException($"Transient error on attempt {attempt}");
            }

            await Task.CompletedTask;
        }

        public static void Reset()
        {
            _attemptCount = 0;
            _retryAttempts.Clear();
        }

        public static List<RetryAttempt> GetRetryAttempts() => _retryAttempts.ToList();
    }

    [Fact]
    public async Task Scenario1_OnRetryCallback_InvokedOnEachRetry()
    {
        // Arrange
        OnRetryCallbackHandler.Reset();
        await CreateIsolatedHostAsync();

        // Act - Task fails 2 times, succeeds on 3rd attempt
        var taskId = await Dispatcher.Dispatch(new OnRetryCallbackTask(FailTimes: 2));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.Single();

        Assert.Equal(QueuedTaskStatus.Completed, task.Status);

        var retryAttempts = OnRetryCallbackHandler.GetRetryAttempts().OrderBy(r => r.AttemptNumber).ToList();
        Assert.Equal(2, retryAttempts.Count); // 2 retries before success

        Assert.Equal(1, retryAttempts[0].AttemptNumber);
        Assert.Equal(2, retryAttempts[1].AttemptNumber);

        Assert.All(retryAttempts, attempt => Assert.Equal(taskId, attempt.TaskId));
        Assert.All(retryAttempts, attempt => Assert.Contains("Transient error", attempt.ExceptionMessage));
    }

    #endregion

    #region Scenario 2: Exception Filtering with Handle

    public record FilteredExceptionTask(bool ThrowHandled) : IEverTask;

    public class FilteredExceptionHandler : EverTaskHandler<FilteredExceptionTask>
    {
        private static int _attemptCount = 0;
        private static int _onRetryCallCount = 0;

        public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(50))
            .Handle<InvalidOperationException>()
            .Handle<TimeoutException>();

        public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
        {
            Interlocked.Increment(ref _onRetryCallCount);
            return ValueTask.CompletedTask;
        }

        public override Task Handle(FilteredExceptionTask task, CancellationToken ct)
        {
            Interlocked.Increment(ref _attemptCount);

            if (task.ThrowHandled)
            {
                throw new InvalidOperationException("This should be retried");
            }
            else
            {
                throw new ArgumentException("This should NOT be retried - permanent error");
            }
        }

        public static void Reset()
        {
            _attemptCount = 0;
            _onRetryCallCount = 0;
        }

        public static (int attempts, int retries) GetCounts() => (_attemptCount, _onRetryCallCount);
    }

    [Fact]
    public async Task Scenario2_ExceptionFiltering_OnlyRetriesConfiguredExceptions()
    {
        // Arrange - Test non-retryable exception
        FilteredExceptionHandler.Reset();
        await CreateIsolatedHostAsync();

        // Act - Throw non-retryable exception (ArgumentException)
        var taskId = await Dispatcher.Dispatch(new FilteredExceptionTask(ThrowHandled: false));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.Single();

        Assert.Equal(QueuedTaskStatus.Failed, task.Status);
        Assert.Contains("ArgumentException", task.Exception);

        var (attempts, retries) = FilteredExceptionHandler.GetCounts();
        Assert.Equal(1, attempts); // Only 1 attempt, no retries
        Assert.Equal(0, retries); // OnRetry NOT called
    }

    [Fact]
    public async Task Scenario2_ExceptionFiltering_RetriesHandledException()
    {
        // Arrange - Test retryable exception
        FilteredExceptionHandler.Reset();
        await CreateIsolatedHostAsync();

        // Act - Throw retryable exception (InvalidOperationException)
        var taskId = await Dispatcher.Dispatch(new FilteredExceptionTask(ThrowHandled: true));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Assert
        var (attempts2, retries2) = FilteredExceptionHandler.GetCounts();
        Assert.Equal(6, attempts2); // Initial + 5 retries
        Assert.Equal(5, retries2); // OnRetry called 5 times
    }

    #endregion

    #region Scenario 3: Predicate-Based Filtering

    public record PredicateFilterTask(int StatusCode) : IEverTask;

    public class HttpStatusException : Exception
    {
        public int StatusCode { get; }
        public HttpStatusException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    public class PredicateFilterHandler : EverTaskHandler<PredicateFilterTask>
    {
        private static int _attemptCount = 0;
        private static int _onRetryCallCount = 0;

        public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(50))
            .HandleWhen(ex =>
            {
                // Only retry HTTP 5xx errors (server errors), not 4xx (client errors)
                if (ex is HttpStatusException httpEx)
                {
                    return httpEx.StatusCode >= 500 && httpEx.StatusCode < 600;
                }
                return false;
            });

        public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
        {
            Interlocked.Increment(ref _onRetryCallCount);
            return ValueTask.CompletedTask;
        }

        public override Task Handle(PredicateFilterTask task, CancellationToken ct)
        {
            Interlocked.Increment(ref _attemptCount);
            throw new HttpStatusException(task.StatusCode, $"HTTP {task.StatusCode}");
        }

        public static void Reset()
        {
            _attemptCount = 0;
            _onRetryCallCount = 0;
        }

        public static (int attempts, int retries) GetCounts() => (_attemptCount, _onRetryCallCount);
    }

    [Fact]
    public async Task Scenario3_Predicate_Retries5xxNotRetries4xx()
    {
        // Arrange
        PredicateFilterHandler.Reset();
        await CreateIsolatedHostAsync();

        // Act - 404 (client error) - should NOT retry
        var taskId404 = await Dispatcher.Dispatch(new PredicateFilterTask(StatusCode: 404));
        await WaitForTaskStatusAsync(taskId404, QueuedTaskStatus.Failed);

        var (attempts404, retries404) = PredicateFilterHandler.GetCounts();

        PredicateFilterHandler.Reset();

        // Act - 503 (server error) - should retry
        var taskId503 = await Dispatcher.Dispatch(new PredicateFilterTask(StatusCode: 503));
        await WaitForTaskStatusAsync(taskId503, QueuedTaskStatus.Failed);

        var (attempts503, retries503) = PredicateFilterHandler.GetCounts();

        // Assert
        Assert.Equal(1, attempts404); // No retries for 404
        Assert.Equal(0, retries404);

        Assert.Equal(4, attempts503); // Initial + 3 retries for 503
        Assert.Equal(3, retries503);
    }

    #endregion

    #region Scenario 4: Derived Exception Type Matching

    public record DerivedExceptionTask : IEverTask;

    public class DerivedExceptionHandler : EverTaskHandler<DerivedExceptionTask>
    {
        private static int _attemptCount = 0;
        private static int _onRetryCallCount = 0;

        public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(50))
            .Handle<IOException>();

        public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
        {
            Interlocked.Increment(ref _onRetryCallCount);
            return ValueTask.CompletedTask;
        }

        public override Task Handle(DerivedExceptionTask task, CancellationToken ct)
        {
            Interlocked.Increment(ref _attemptCount);

            // FileNotFoundException derives from IOException
            throw new FileNotFoundException("File not found - derived from IOException");
        }

        public static void Reset()
        {
            _attemptCount = 0;
            _onRetryCallCount = 0;
        }

        public static (int attempts, int retries) GetCounts() => (_attemptCount, _onRetryCallCount);
    }

    [Fact]
    public async Task Scenario4_DerivedExceptionType_RetriesCorrectly()
    {
        // Arrange
        DerivedExceptionHandler.Reset();
        await CreateIsolatedHostAsync();

        // Act - Throw FileNotFoundException (derives from IOException)
        var taskId = await Dispatcher.Dispatch(new DerivedExceptionTask());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Assert
        var (attempts, retries) = DerivedExceptionHandler.GetCounts();
        Assert.Equal(4, attempts); // Initial + 3 retries
        Assert.Equal(3, retries); // OnRetry called 3 times
    }

    #endregion

    #region Scenario 5: Monitoring Events Published During Retries

    public record MonitoringEventTask : IEverTask;

    public class MonitoringEventHandler : EverTaskHandler<MonitoringEventTask>
    {
        private static int _attemptCount = 0;

        public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(2, TimeSpan.FromMilliseconds(50));

        public override async Task Handle(MonitoringEventTask task, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _attemptCount);

            if (count < 2)
            {
                throw new InvalidOperationException($"Attempt {count} failed");
            }

            await Task.CompletedTask;
        }

        public static void Reset()
        {
            _attemptCount = 0;
        }
    }

    [Fact]
    public async Task Scenario5_MonitoringEvents_PublishedDuringRetries()
    {
        // Arrange
        MonitoringEventHandler.Reset();
        var events = new ConcurrentBag<EverTaskEventData>();

        await CreateIsolatedHostAsync();

        // Subscribe to monitoring events
        WorkerExecutor.TaskEventOccurredAsync += async (data) =>
        {
            events.Add(data);
            await Task.CompletedTask;
        };

        // Act
        var taskId = await Dispatcher.Dispatch(new MonitoringEventTask());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var retryEvents = events.Where(e => e.Message.Contains("retry attempt")).ToList();
        Assert.NotEmpty(retryEvents);
        Assert.All(retryEvents, e => Assert.Equal("Warning", e.Severity));
    }

    #endregion
}
