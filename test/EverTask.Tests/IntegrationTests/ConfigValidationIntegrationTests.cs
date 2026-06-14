using System.Collections.Concurrent;
using System.Threading.Channels;
using EverTask.Configuration;
using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using EverTask.Worker;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// M2 — config fail-fast + non-throwing logging:
/// F5 (zero-consumer deadlock), G19 (Default queue FallbackToDefault self-reference),
/// CU5/L12 (Drop* silently loses a persisted task), G9 (handler log render faults the task),
/// CU18/L26 (OnError-override fault masks the real failure report).
/// </summary>
public class ConfigValidationIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly ConfigValidationState _state = new();

    [Fact]
    public async Task Should_fail_fast_at_startup_on_zero_maxdegreeofparallelism()
    {
        // F5: MaxDegreeOfParallelism = 0 starts ZERO consumers; with FullMode=Wait the queue fills
        // and every producer blocks forever. The configuration must never deadlock — it is clamped
        // to at least one consumer.
        await CreateIsolatedHostAsync(
            maxDegreeOfParallelism: 0,
            configureServices: s => s.AddSingleton(_state));

        await Dispatcher.Dispatch(new ConfigCounterTask(1));

        await TaskWaitHelper.WaitForConditionAsync(() => _state.Executed.Contains(1), timeoutMs: 6000);
        _state.Executed.ShouldContain(1);
    }

    [Fact]
    public async Task Should_apply_wait_backpressure_when_default_queue_set_fallbacktodefault()
    {
        // G19: a Default queue configured with FallbackToDefault has no queue to fall back to (itself):
        // a full Default must apply Wait backpressure, NOT throw QueueFullException at the caller.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.ConfigureDefaultQueue(q => q.SetFullBehavior(QueueFullBehavior.FallbackToDefault)
                                          .SetChannelCapacity(1)
                                          .SetMaxDegreeOfParallelism(1));
            b.Services.AddSingleton(_state);
        });

        // Occupy the single consumer (channel drains), then fill the channel.
        await Dispatcher.Dispatch(new ConfigBlockingTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Dispatcher.Dispatch(new ConfigBlockingTask());

        // The third dispatch hits the full Default queue: it must WAIT, not throw.
        var third = Dispatcher.Dispatch(new ConfigBlockingTask());
        await Task.Delay(300);
        _state.BlockingGate.Release(10);

        QueueFullException? thrown = null;
        try
        {
            await third;
        }
        catch (QueueFullException e)
        {
            thrown = e;
        }

        thrown.ShouldBeNull("a full Default queue with FallbackToDefault must apply Wait backpressure, not throw");
    }

    [Fact]
    public async Task Should_not_silently_drop_persisted_task_with_drop_channel_mode()
    {
        // CU5/L12: with a Drop* full mode TryWrite never rejects — it silently evicts another queued
        // task whose storage row stays Queued (looks enqueued forever, lost in this process). The
        // evicted victim must be made recoverable (reverted to WaitingQueue), not silently dropped.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("dropq", q =>
            {
                q.SetMaxDegreeOfParallelism(1);
                q.SetChannelOptions(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            });
            b.Services.AddSingleton(_state);
        });

        // First task occupies the consumer (channel drains).
        await Dispatcher.Dispatch(new ConfigDropTask(1));
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Second sits in the capacity-1 channel; the third evicts it (DropOldest).
        var victimId = await Dispatcher.Dispatch(new ConfigDropTask(2));
        await Dispatcher.Dispatch(new ConfigDropTask(3));

        // The evicted victim must not be left silently Queued — it is reverted to WaitingQueue
        // (recoverable at the next startup), not lost.
        var victim = await TaskWaitHelper.WaitForTaskStatusAsync(
            Storage, victimId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);
        victim.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_complete_task_when_handler_logs_invalid_format_specifier()
    {
        // G9: a malformed format specifier / alignment in a handler's own log call must not fault the
        // task — the log render is non-throwing (falls back to the raw template).
        await CreateIsolatedHostAsync(
            configureEverTask: cfg => cfg.WithPersistentLogger(l => l.SetMinimumLevel(LogLevel.Trace)));

        var id = await Dispatcher.Dispatch(new ConfigBadLogTask());

        await WaitForTaskStatusAsync(id, QueuedTaskStatus.Completed, timeoutMs: 8000);
    }

    [Fact]
    public async Task Should_report_real_failure_when_onerror_override_throws()
    {
        // CU18/L26: when an OnError override throws, the callback-error report used the template "{1}"
        // with a single arg, throwing FormatException eagerly and masking the REAL failure report
        // (RegisterError for the handler exception). The real failure must still be reported.
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var events = new ConcurrentBag<EverTaskEventData>();
        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            events.Add(data);
            return Task.CompletedTask;
        };

        var id = await Dispatcher.Dispatch(new ConfigOnErrorThrowsTask());

        await WaitForTaskStatusAsync(id, QueuedTaskStatus.Failed, timeoutMs: 8000);

        // Events are published fire-and-forget: poll briefly for the real-failure error event.
        await TaskWaitHelper.WaitForConditionAsync(
            () => events.Any(e => e.TaskId == id
                                  && e.Severity == nameof(SeverityLevel.Error)
                                  && e.Exception != null
                                  && e.Exception.Contains(ConfigOnErrorThrowsTaskHandler.RealFailureMarker)),
            timeoutMs: 5000);

        events.ShouldContain(e => e.TaskId == id
                                  && e.Severity == nameof(SeverityLevel.Error)
                                  && e.Exception != null
                                  && e.Exception.Contains(ConfigOnErrorThrowsTaskHandler.RealFailureMarker),
            "the real handler failure must be reported to the monitor even when OnError throws");
    }
}
