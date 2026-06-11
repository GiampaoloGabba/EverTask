using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// Unit tests for the scheduler resilience behaviors: per-task idempotent scheduling (dedupe),
/// non-blocking dispatch with retry backoff on full queues, and thread-safe wake-up signaling.
/// </summary>
public class SchedulerResilienceTests
{
    private static TaskHandlerExecutor CreateExecutor(
        DateTimeOffset? executionTime = null,
        Guid? id = null,
        string? queueName = null) =>
        new(new ResilienceCounterTask(0),
            new object(),
            null,
            executionTime,
            null,
            null,
            null,
            null,
            null,
            id ?? Guid.NewGuid(),
            queueName,
            null,
            AuditLevel.Full);

    private static Mock<IWorkerQueueManager> CreateManagerMock(Func<string?, EnqueueResult> resultFactory)
    {
        var mock = new Mock<IWorkerQueueManager>();
        mock.Setup(x => x.TryEnqueueImmediate(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, TaskHandlerExecutor, CancellationToken>(
                (queueName, _, _) => Task.FromResult(resultFactory(queueName)));
        return mock;
    }

    [Fact]
    public async Task Should_dispatch_once_when_same_task_is_scheduled_twice()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50));

        // Same PersistenceId registered twice (e.g. startup recovery racing with a taskKey
        // re-registration): the stale entry must be discarded, single execution per occurrence.
        var item1 = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(200));
        var item2 = item1 with { };
        scheduler.Schedule(item1);
        scheduler.Schedule(item2);

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 1, timeoutMs: 5000);
        await Task.Delay(700);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Should_dispatch_once_when_same_task_is_scheduled_twice_on_sharded_scheduler()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        using var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 4);

        // Hash-based sharding routes the same PersistenceId to the same shard,
        // so the per-shard dedupe must apply.
        var item1 = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(200));
        var item2 = item1 with { };
        scheduler.Schedule(item1);
        scheduler.Schedule(item2);

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 1, timeoutMs: 5000);
        await Task.Delay(700);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Should_retry_dispatch_with_backoff_until_queue_has_space()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
            Interlocked.Increment(ref calls) <= 2 ? EnqueueResult.QueueFull : EnqueueResult.Enqueued);

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50))
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50)));

        // Two full-queue rejections, then success.
        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 3, timeoutMs: 5000);

        // After the successful dispatch the task must not be retried again.
        await Task.Delay(500);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Should_keep_dispatching_other_queues_when_one_queue_is_full()
    {
        var fullCalls = 0;
        var okCalls = 0;
        var mock = CreateManagerMock(queueName =>
        {
            if (queueName == "full")
            {
                Interlocked.Increment(ref fullCalls);
                return EnqueueResult.QueueFull;
            }

            Interlocked.Increment(ref okCalls);
            return EnqueueResult.Enqueued;
        });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50))
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(200)
        };

        // The task for the saturated queue is due FIRST: before the fix the scheduler loop
        // blocked on it and the task behind it (other queue) was never dispatched.
        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50), queueName: "full"));
        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(100), queueName: "ok"));

        await TaskWaitHelper.WaitForConditionAsync(() => okCalls >= 1, timeoutMs: 5000);

        okCalls.ShouldBe(1);
        fullCalls.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Should_retry_dispatch_with_backoff_until_queue_has_space_on_sharded_scheduler()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
            Interlocked.Increment(ref calls) <= 2 ? EnqueueResult.QueueFull : EnqueueResult.Enqueued);

        using var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 4)
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

        // Hash-based sharding keeps the retries on the same shard.
        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50)));

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 3, timeoutMs: 5000);

        await Task.Delay(500);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Should_not_retry_dispatch_when_cancelled_by_shutdown()
    {
        var calls = 0;
        var mock = new Mock<IWorkerQueueManager>();
        mock.Setup(x => x.TryEnqueueImmediate(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, TaskHandlerExecutor, CancellationToken>((_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new OperationCanceledException();
            });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50))
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50)));

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 1, timeoutMs: 5000);
        await Task.Delay(600);

        // Shutdown cancellation: the task stays in its recoverable status for the next startup,
        // no retry loop and (crucially) no Failed status.
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Should_retry_dispatch_when_enqueue_fails_transiently()
    {
        var calls = 0;
        var mock = new Mock<IWorkerQueueManager>();
        mock.Setup(x => x.TryEnqueueImmediate(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, TaskHandlerExecutor, CancellationToken>((_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) <= 2)
                    throw new InvalidOperationException("transient storage error");
                return Task.FromResult(EnqueueResult.Enqueued);
            });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50))
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50)));

        // Transient errors must NOT mark the task Failed: the dispatch is parked and retried.
        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 3, timeoutMs: 5000);

        await Task.Delay(500);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Should_not_dispatch_after_TryUnschedule()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50));

        var item = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(300));
        scheduler.Schedule(item);

        scheduler.TryUnschedule(item.PersistenceId).ShouldBeTrue();

        await Task.Delay(900);
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task Should_not_dispatch_after_TryUnschedule_on_sharded_scheduler()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        using var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 4);

        var item = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(300));
        scheduler.Schedule(item);

        scheduler.TryUnschedule(item.PersistenceId).ShouldBeTrue();

        await Task.Delay(900);
        calls.ShouldBe(0);
    }

    [Fact]
    public void Should_not_throw_when_scheduling_concurrently_on_sharded_scheduler()
    {
        var mock = CreateManagerMock(_ => EnqueueResult.Enqueued);

        using var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 2);

        var due = DateTimeOffset.UtcNow.AddMinutes(5);

        // Before the fix the wake-up signal used a racy check-then-act on a max-count-1
        // semaphore: concurrent Schedule() calls could throw SemaphoreFullException.
        Should.NotThrow(() => Parallel.For(0, 2000, _ => scheduler.Schedule(CreateExecutor(due))));
    }
}
