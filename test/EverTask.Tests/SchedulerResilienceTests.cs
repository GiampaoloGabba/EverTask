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
    public async Task Should_retry_dispatch_when_previous_delivery_is_still_unwinding()
    {
        // DuplicateInProcess: the slot fired while the previous delivery of the same task was
        // still unwinding (its registration not yet released by DoWork's outer finally). The
        // scheduler must retry like a full queue — treating it as success would consume the
        // scheduler registration and strand the only live copy until restart.
        var calls = 0;
        var mock = CreateManagerMock(_ =>
            Interlocked.Increment(ref calls) <= 2 ? EnqueueResult.DuplicateInProcess : EnqueueResult.Enqueued);

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50))
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50)));

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 3, timeoutMs: 5000);

        await Task.Delay(500);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Should_retry_dispatch_when_previous_delivery_is_still_unwinding_on_sharded_scheduler()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
            Interlocked.Increment(ref calls) <= 2 ? EnqueueResult.DuplicateInProcess : EnqueueResult.Enqueued);

        using var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 4)
        {
            FullQueueRetryDelay = TimeSpan.FromMilliseconds(100)
        };

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
    public async Task Should_not_remove_newer_registration_when_conditional_TryUnschedule_targets_stale_one()
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

        // Same PersistenceId, two registrations (latest wins). The conditional unschedule
        // of the STALE one must fail and must NOT remove the newer registration.
        var stale = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(300));
        var newer = stale with { ExecutionTime = DateTimeOffset.UtcNow.AddMilliseconds(400) };
        scheduler.Schedule(stale);
        scheduler.Schedule(newer);

        scheduler.TryUnschedule(stale.PersistenceId, stale).ShouldBeFalse();

        // The newer registration is still live and must dispatch
        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 1, timeoutMs: 5000);
        calls.ShouldBe(1);

        // Conditional unschedule of the CURRENT registration succeeds when it is still parked
        var parked = CreateExecutor(DateTimeOffset.UtcNow.AddMinutes(5));
        scheduler.Schedule(parked);
        scheduler.TryUnschedule(parked.PersistenceId, parked).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_not_remove_newer_registration_when_conditional_TryUnschedule_targets_stale_one_on_sharded_scheduler()
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

        var stale = CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(300));
        var newer = stale with { ExecutionTime = DateTimeOffset.UtcNow.AddMilliseconds(400) };
        scheduler.Schedule(stale);
        scheduler.Schedule(newer);

        scheduler.TryUnschedule(stale.PersistenceId, stale).ShouldBeFalse();

        await TaskWaitHelper.WaitForConditionAsync(() => calls >= 1, timeoutMs: 5000);
        calls.ShouldBe(1);

        var parked = CreateExecutor(DateTimeOffset.UtcNow.AddMinutes(5));
        scheduler.Schedule(parked);
        scheduler.TryUnschedule(parked.PersistenceId, parked).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_ignore_schedule_after_dispose_without_throwing()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50));

        scheduler.Dispose();

        // Scheduling after Dispose must not throw into the caller (the task stays in its
        // recoverable status and is re-dispatched at the next startup) and must not dispatch.
        Should.NotThrow(() => scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50))));

        await Task.Delay(400);
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task Should_ignore_schedule_after_dispose_without_throwing_on_sharded_scheduler()
    {
        var calls = 0;
        var mock = CreateManagerMock(_ =>
        {
            Interlocked.Increment(ref calls);
            return EnqueueResult.Enqueued;
        });

        var scheduler = new ShardedScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 2);

        scheduler.Dispose();

        Should.NotThrow(() => scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(50))));

        await Task.Delay(400);
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
