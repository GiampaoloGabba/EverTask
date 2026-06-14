using System.Linq.Expressions;
using System.Threading.Channels;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

/// <summary>
/// Unit tests for the WorkerQueue resilience behaviors: in-channel dedupe registry,
/// SetQueued-before-write ordering, revert-to-WaitingQueue on the full-queue race,
/// and the blacklist semantics of WorkerQueueManager.TryEnqueue.
/// </summary>
public class WorkerQueueResilienceTests
{
    private static TaskHandlerExecutor CreateExecutor(Guid? id = null, string? queueName = null) =>
        new(new ResilienceCounterTask(0),
            new object(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            id ?? Guid.NewGuid(),
            queueName,
            null,
            AuditLevel.Full);

    private static WorkerQueue CreateQueue(
        int capacity,
        ITaskStorage? storage = null,
        IWorkerBlacklist? blacklist = null,
        QueueFullBehavior fullBehavior = QueueFullBehavior.Wait,
        TaskDeliveryRegistry? registry = null)
    {
        var config = new QueueConfiguration
        {
            Name = "test",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait },
            QueueFullBehavior = fullBehavior
        };
        return new WorkerQueue(config, Mock.Of<ILogger>(), blacklist ?? Mock.Of<IWorkerBlacklist>(), storage, registry);
    }

    [Fact]
    public async Task Should_reject_duplicate_try_enqueue_while_delivery_is_in_flight()
    {
        var registry = new TaskDeliveryRegistry();
        var queue    = CreateQueue(capacity: 5, registry: registry);
        var task     = CreateExecutor();

        (await queue.TryQueue(task)).ShouldBe(EnqueueResult.Enqueued);
        // Duplicate delivery of the same persisted task (e.g. recovery racing a live dispatch):
        // rejected at the write boundary, single channel entry.
        (await queue.TryQueue(task)).ShouldBe(EnqueueResult.DuplicateInProcess);
        queue.Count.ShouldBe(1);

        // The registration SURVIVES the dequeue (it covers the dequeue->execution window):
        // a re-delivery is still rejected while the task would be executing.
        await queue.Dequeue(CancellationToken.None);
        queue.Count.ShouldBe(0);
        registry.IsDelivering(task.PersistenceId).ShouldBeTrue();
        (await queue.TryQueue(task)).ShouldBe(EnqueueResult.DuplicateInProcess);

        // Only the delivery's End (WorkerExecutor.DoWork outer finally) frees the id.
        registry.End(task.PersistenceId);
        (await queue.TryQueue(task)).ShouldBe(EnqueueResult.Enqueued);
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_release_registration_when_channel_drops_item_in_drop_mode()
    {
        // Drop* full modes silently discard items: the itemDropped callback must release the
        // delivery registration, or the dropped id would stay registered forever and block
        // every later re-delivery of the same task.
        var registry = new TaskDeliveryRegistry();
        var config = new QueueConfiguration
        {
            Name = "drop",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }
        };
        var queue = new WorkerQueue(config, Mock.Of<ILogger>(), Mock.Of<IWorkerBlacklist>(), null, registry);

        var first  = CreateExecutor();
        var second = CreateExecutor();

        (await queue.TryQueue(first)).ShouldBe(EnqueueResult.Enqueued);
        (await queue.TryQueue(second)).ShouldBe(EnqueueResult.Enqueued); // capacity 1: drops first

        registry.IsDelivering(first.PersistenceId).ShouldBeFalse();  // released by itemDropped
        registry.IsDelivering(second.PersistenceId).ShouldBeTrue();
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_skip_duplicate_blocking_enqueue_when_task_already_in_channel()
    {
        var queue = CreateQueue(capacity: 5);
        var task  = CreateExecutor();

        await queue.Queue(task);
        await queue.Queue(task); // duplicate: must not write a second entry nor block
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_revert_status_to_WaitingQueue_when_queue_fills_between_check_and_write()
    {
        var storage = new Mock<ITaskStorage>();
        WorkerQueue queue = null!;

        var outerTask  = CreateExecutor();
        var fillerTask = CreateExecutor();
        var filled     = false;

        // Generic stubs (loose mock would return null Task and break the awaits)
        storage.Setup(s => s.SetQueued(It.IsAny<Guid>(), It.IsAny<AuditLevel>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        storage.Setup(s => s.SetStatus(It.IsAny<Guid>(), It.IsAny<QueuedTaskStatus>(), It.IsAny<Exception?>(),
                   It.IsAny<AuditLevel>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        // The conditional revert reads the current status: the row is still Queued, so the revert proceeds.
        storage.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { new QueuedTask { Id = outerTask.PersistenceId, Status = QueuedTaskStatus.Queued } });

        // While the outer TryQueue is between its capacity check and its TryWrite, another
        // writer fills the (capacity 1) channel: the outer TryWrite must fail and the status
        // must be reverted to WaitingQueue so the task stays visible to startup recovery.
        storage.Setup(s => s.SetQueued(outerTask.PersistenceId, It.IsAny<AuditLevel>(), It.IsAny<CancellationToken>()))
               .Returns(async () =>
               {
                   if (!filled)
                   {
                       filled = true;
                       (await queue.TryQueue(fillerTask)).ShouldBe(EnqueueResult.Enqueued);
                   }
               });

        queue = CreateQueue(capacity: 1, storage: storage.Object);

        (await queue.TryQueue(outerTask)).ShouldBe(EnqueueResult.QueueFull);

        storage.Verify(s => s.SetStatus(outerTask.PersistenceId, QueuedTaskStatus.WaitingQueue,
            null, It.IsAny<AuditLevel>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Once);
        queue.Count.ShouldBe(1); // only the filler made it in
    }

    [Fact]
    public async Task Should_set_queued_in_storage_before_writing_to_channel()
    {
        var storage = new Mock<ITaskStorage>();
        WorkerQueue queue = null!;

        var task = CreateExecutor();
        var countAtSetQueued = -1;

        storage.Setup(s => s.SetQueued(task.PersistenceId, It.IsAny<AuditLevel>(), It.IsAny<CancellationToken>()))
               .Returns(() =>
               {
                   // The task must NOT be visible in the channel yet: a consumer picking it up
                   // before SetQueued completes could have its InProgress/Completed overwritten.
                   countAtSetQueued = queue.Count;
                   return Task.CompletedTask;
               });

        queue = CreateQueue(capacity: 5, storage: storage.Object);

        (await queue.TryQueue(task)).ShouldBe(EnqueueResult.Enqueued);

        countAtSetQueued.ShouldBe(0);
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_return_false_without_exception_when_blacklisted_task_hits_ThrowException_queue()
    {
        var manager = CreateManager(QueueFullBehavior.ThrowException, out _);

        // Blacklisted (cancelled) task: nothing to enqueue, but NOT a queue-full error.
        var result = await manager.TryEnqueue("isolated", CreateExecutor(queueName: "isolated"));

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_not_fallback_to_default_queue_when_task_is_blacklisted()
    {
        var manager = CreateManager(QueueFullBehavior.FallbackToDefault, out var defaultQueue);

        var result = await manager.TryEnqueue("isolated", CreateExecutor(queueName: "isolated"));

        result.ShouldBeFalse();
        defaultQueue.Count.ShouldBe(0); // a cancelled task must never be re-routed
    }

    private static IWorkerQueueManager CreateManager(QueueFullBehavior isolatedBehavior, out IWorkerQueue defaultQueue)
    {
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["default"] = new()
            {
                Name = "default",
                ChannelOptions = new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait }
            },
            ["isolated"] = new()
            {
                Name = "isolated",
                ChannelOptions = new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait },
                QueueFullBehavior = isolatedBehavior
            }
        };

        var blacklist = new Mock<IWorkerBlacklist>();
        blacklist.Setup(b => b.IsBlacklisted(It.IsAny<Guid>())).Returns(true);

        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        var manager = new WorkerQueueManager(
            configurations,
            Mock.Of<IEverTaskLogger<WorkerQueueManager>>(),
            blacklist.Object,
            loggerFactory.Object);

        defaultQueue = manager.GetQueue("default");
        return manager;
    }
}
