using EverTask.Resilience;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Verifies the per-queue retry/timeout defaults resolution chain:
/// handler override → queue default → global default.
/// Queue-level <c>SetDefaultRetryPolicy</c>/<c>SetDefaultTimeout</c> were documented but never
/// consumed at execution time before v3.7.0 — these tests pin the fixed behavior.
/// </summary>
public class QueueDefaultsIntegrationTests : IsolatedIntegrationTestBase
{
    public record QueueRetryTask : IEverTask;

    public class QueueRetryHandler : EverTaskHandler<QueueRetryTask>
    {
        private static int _attemptCount;
        public static int AttemptCount => Volatile.Read(ref _attemptCount);
        public static void Reset() => _attemptCount = 0;

        public override string? QueueName => "queue-defaults-retry";

        // NO RetryPolicy override: the QUEUE default must apply
        public override Task Handle(QueueRetryTask backgroundTask, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);
            throw new InvalidOperationException("always fails (transient)");
        }
    }

    [Fact]
    public async Task Should_apply_queue_default_retry_policy_when_handler_has_no_override()
    {
        QueueRetryHandler.Reset();

        await CreateIsolatedHostWithBuilderAsync(builder => builder
            .AddQueue("queue-defaults-retry", q => q
                .SetMaxDegreeOfParallelism(2)
                .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(30))))
            .AddMemoryStorage());

        var taskId = await Dispatcher.Dispatch(new QueueRetryTask());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // LinearRetryPolicy(5) = initial attempt + 5 retries; the global default (3) would
        // produce 4 attempts — proving the queue-level policy is the one applied
        QueueRetryHandler.AttemptCount.ShouldBe(6,
            "the queue default retry policy must apply when the handler declares none");
    }

    public record QueueTimeoutTask : IEverTask;

    public class QueueTimeoutHandler : EverTaskHandler<QueueTimeoutTask>
    {
        public override string? QueueName => "queue-defaults-timeout";

        // NO Timeout override: the QUEUE default must apply
        public override async Task Handle(QueueTimeoutTask backgroundTask, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        }
    }

    [Fact]
    public async Task Should_apply_queue_default_timeout_when_handler_has_no_override()
    {
        await CreateIsolatedHostWithBuilderAsync(builder => builder
            .AddQueue("queue-defaults-timeout", q => q
                .SetMaxDegreeOfParallelism(2)
                .SetDefaultTimeout(TimeSpan.FromMilliseconds(300)))
            .AddMemoryStorage());

        var taskId = await Dispatcher.Dispatch(new QueueTimeoutTask());

        // TimeoutException is not retryable by default, so the task fails fast: without the
        // queue timeout the handler would run 8s and this wait (6s) would expire
        var task = await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 6000);
        task.Exception.ShouldNotBeNull();
        task.Exception.ShouldContain("TimeoutException",
            customMessage: "the queue default timeout must apply when the handler declares none");
    }

    public record HandlerWinsTask : IEverTask;

    public class HandlerWinsHandler : EverTaskHandler<HandlerWinsTask>
    {
        private static int _attemptCount;
        public static int AttemptCount => Volatile.Read(ref _attemptCount);
        public static void Reset() => _attemptCount = 0;

        public override string? QueueName => "queue-defaults-override";

        // Handler override must WIN over the queue default
        public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(20));

        public override Task Handle(HandlerWinsTask backgroundTask, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);
            throw new InvalidOperationException("always fails (transient)");
        }
    }

    [Fact]
    public async Task Should_prefer_handler_retry_policy_over_queue_default()
    {
        HandlerWinsHandler.Reset();

        await CreateIsolatedHostWithBuilderAsync(builder => builder
            .AddQueue("queue-defaults-override", q => q
                .SetMaxDegreeOfParallelism(2)
                .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(30))))
            .AddMemoryStorage());

        var taskId = await Dispatcher.Dispatch(new HandlerWinsTask());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // LinearRetryPolicy(1) = initial attempt + 1 retry
        HandlerWinsHandler.AttemptCount.ShouldBe(2,
            "the handler override must win over the queue default (chain: handler → queue → global)");
    }
}
