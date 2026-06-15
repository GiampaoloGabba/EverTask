using EverTask.Configuration;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// P-E.2 / L34: startup recovery used a SINGLE global-parallelism Parallel.ForEachAsync, so blocking
/// enqueues toward one saturated queue occupied every global slot and head-of-line-blocked the
/// recovery of unrelated, idle queues. Recovery must be partitioned per target queue, so a wedged
/// queue cannot starve the recovery of others.
/// </summary>
public class RecoveryParallelismIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    private static QueuedTask CreateSeededTask(IEverTask task, QueuedTaskStatus status, DateTimeOffset createdAt,
                                               string? queueName)
        => new()
        {
            Id           = Guid.NewGuid(),
            Type         = task.GetType().AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(task),
            Handler      = "seeded-by-test",
            Status       = status,
            CreatedAtUtc = createdAt,
            QueueName    = queueName
        };

    [Fact]
    public async Task Should_not_hol_block_recovery_of_idle_queue_behind_saturated_queue()
    {
        // "blocked": capacity 1, single consumer. Global recovery parallelism = 2.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                            .SetMaxDegreeOfParallelism(1)
                                            .SetFullBehavior(QueueFullBehavior.Wait));
                b.Services.AddSingleton(_state);
            },
            startHost: false,
            configureEverTask: cfg => cfg.SetChannelOptions(10).SetMaxDegreeOfParallelism(2));

        var seededAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Saturate "blocked": more blocking tasks than (channel + consumer + global recovery slots),
        // created FIRST so recovery picks them before the idle-queue tasks. The gate is never released
        // during the assert, so every blocking enqueue past the first two wedges a recovery slot.
        for (var i = 0; i < 6; i++)
        {
            await Storage.Persist(CreateSeededTask(
                new ResilienceBlockingTask(), QueuedTaskStatus.Queued, seededAt.AddMilliseconds(i), "blocked"));
        }

        // Idle "default" queue tasks, created AFTER the blocking ones.
        const int idleCount = 4;
        for (var i = 0; i < idleCount; i++)
        {
            await Storage.Persist(CreateSeededTask(
                new ResilienceDefaultQueueTask(), QueuedTaskStatus.Queued, seededAt.AddMilliseconds(100 + i), null));
        }

        await Host!.StartAsync();

        try
        {
            // Pre-fix: every global recovery slot wedges on the saturated "blocked" queue, so the idle
            // queue's tasks are never even recovered while the gate is held → WaitForConditionAsync
            // times out and throws (RED). Post-fix: recovery is partitioned per queue, so the default
            // group runs independently and completes.
            await TaskWaitHelper.WaitForConditionAsync(
                () => Volatile.Read(ref _state.DefaultQueueCompleted) >= idleCount, timeoutMs: 15000);

            Volatile.Read(ref _state.DefaultQueueCompleted).ShouldBe(idleCount,
                "the idle queue's recovery must not be head-of-line-blocked by the saturated queue (L34)");
        }
        finally
        {
            // Drain the wedged blocking tasks.
            _state.BlockingGate.Release(100);
        }
    }
}
