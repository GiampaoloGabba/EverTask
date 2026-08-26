using System.Collections.Concurrent;
using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Top-level so Type.GetType(AssemblyQualifiedName) resolves it during recovery deserialization.
public record PipelineProbeTask : IEverTask;

/// <summary>
/// #39: startup recovery used to wait for the WHOLE page before asking for the next one. The per-queue
/// fan-out inside a wave stops a saturated queue from taking the other queues' slots within a page, but it
/// said nothing about the pages behind it — so one slow delivery delayed the recovery of rows belonging to
/// queues that were completely idle, for as long as it lasted.
/// </summary>
/// <remarks>
/// Real <see cref="MemoryTaskStorage"/>, real keyset pagination, real <see cref="WorkerService"/>; the
/// dispatcher is the observation point and the gate a chosen row blocks on. Every wait is a
/// <see cref="TaskCompletionSource"/> or a poll on what the dispatcher has really been handed — nothing here
/// waits for a duration and hopes.
/// </remarks>
public class RecoveryPagePipelineTests
{
    /// <summary>Mirrors the constant in <see cref="WorkerService.ProcessPendingAsync"/>.</summary>
    private const int RecoveryPageSize = 100;

    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private readonly ConcurrentDictionary<Guid, int>                    _dispatched = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource>   _gates      = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource>   _entered    = new();

    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow.AddHours(-6);
    private          int            _seeded;

    private WorkerService CreateRecovery(int? pagesInFlight = null)
    {
        var dispatcher = new Mock<ITaskDispatcherInternal>();
        dispatcher.Setup(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
                      It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
                      It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>(),
                      It.IsAny<DispatchRowMetadata>()))
                  .Returns((IEverTask _, DateTimeOffset? _, RecurringTask? _, int? _, CancellationToken _,
                            Guid? id, string? _, AuditLevel? _, bool _, DispatchRowMetadata _) =>
                      DispatchAsync(id!.Value));

        var service = RecoveryHarness.CreateRecoveryService(_storage, dispatcher: dispatcher.Object);

        if (pagesInFlight is { } cap)
            service.MaxRecoveryPagesInFlight = cap;

        return service;
    }

    private async Task<Guid> DispatchAsync(Guid id)
    {
        if (_gates.TryGetValue(id, out var gate))
        {
            _entered[id].TrySetResult();
            await gate.Task;
        }

        _dispatched.AddOrUpdate(id, 1, static (_, count) => count + 1);
        return id;
    }

    /// <summary>Makes the re-dispatch of <paramref name="id"/> block until the returned gate is released.</summary>
    private TaskCompletionSource Wedge(Guid id)
    {
        _entered[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[id] = gate;
        return gate;
    }

    /// <summary>Seeds <paramref name="count"/> recoverable one-shots on one queue, in page order.</summary>
    private async Task<Guid[]> SeedAsync(int count, string queueName)
    {
        var ids = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            var row = new QueuedTask
            {
                Id           = Guid.NewGuid(),
                Type         = typeof(PipelineProbeTask).AssemblyQualifiedName!,
                Request      = EverTaskJson.Serialize(new PipelineProbeTask()),
                Handler      = "seeded-by-test",
                Status       = QueuedTaskStatus.Queued,
                CreatedAtUtc = _origin.AddMilliseconds(_seeded++),
                QueueName    = queueName
            };

            await _storage.Persist(row);
            ids[i] = row.Id;
        }

        return ids;
    }

    private Task WaitForEntry(Guid id) => _entered[id].Task.WaitAsync(TimeSpan.FromSeconds(15));

    [Fact]
    public async Task A_slow_delivery_does_not_hold_back_the_recovery_of_the_page_behind_it()
    {
        // Page 1 is one queue's worth of rows and one of them never comes back, so its wave stays in flight
        // for the whole test: pre-fix that is the reader's own await, and nothing past this page is read.
        var slow  = await SeedAsync(RecoveryPageSize, "slow");
        var wedge = Wedge(slow[0]);

        // Page 2 belongs to a queue with nothing wrong with it. Reaching it is the fact under test.
        var idle = await SeedAsync(40, "idle");

        // The premise the assertion rests on: the wedged row really is on the first page and every idle row
        // really is behind it.
        var firstPage = await _storage.RetrievePending(DateTimeOffset.UtcNow, null, null, RecoveryPageSize);
        firstPage.Length.ShouldBe(RecoveryPageSize);
        firstPage.ShouldContain(t => t.Id == slow[0]);
        firstPage.ShouldNotContain(t => idle.Contains(t.Id));

        var recovery = Task.Run(() => CreateRecovery().ProcessPendingAsync());

        try
        {
            await WaitForEntry(slow[0]);

            // RED without the pipeline: the reader is parked inside the first page's wave, page two is never
            // asked for, and no idle row is ever handed to the dispatcher.
            await TaskWaitHelper.WaitForConditionAsync(
                () => idle.All(_dispatched.ContainsKey), timeoutMs: 15000);
        }
        finally
        {
            wedge.TrySetResult();
        }

        await recovery;

        _dispatched.Count.ShouldBe(RecoveryPageSize + idle.Length, "every seeded row must be recovered");
        _dispatched.Values.ShouldAllBe(count => count == 1, "and exactly once");
    }

    [Fact]
    public async Task No_more_pages_are_read_than_the_cap_allows_and_a_freed_slot_releases_the_next_one()
    {
        // Two pages, each wedged on its own queue, then a page of ordinary rows. With a cap of two, the
        // reader stops after the second page — and starts again the moment one of them finishes.
        var firstSlow  = await SeedAsync(RecoveryPageSize, "slow-a");
        var secondSlow = await SeedAsync(RecoveryPageSize, "slow-b");
        var idle       = await SeedAsync(40, "idle");

        var firstGate  = Wedge(firstSlow[0]);
        var secondGate = Wedge(secondSlow[0]);

        var recovery = Task.Run(() => CreateRecovery(pagesInFlight: 2).ProcessPendingAsync());

        try
        {
            await WaitForEntry(firstSlow[0]);
            await WaitForEntry(secondSlow[0]);

            // The cap is a negative fact, and a negative fact needs a window: an unbounded pipeline is
            // already several pages ahead by the time both wedges are observed, so an interval in which not
            // one idle row is dispatched is what says the reader really stopped. That it stopped at a CAP and
            // not for good is the positive wait below.
            await Should.ThrowAsync<TimeoutException>(() => TaskWaitHelper.WaitForConditionAsync(
                () => idle.Any(_dispatched.ContainsKey), timeoutMs: 500));

            firstGate.TrySetResult();

            await TaskWaitHelper.WaitForConditionAsync(
                () => idle.All(_dispatched.ContainsKey), timeoutMs: 15000);
        }
        finally
        {
            firstGate.TrySetResult();
            secondGate.TrySetResult();
        }

        await recovery;

        _dispatched.Count.ShouldBe(RecoveryPageSize * 2 + idle.Length);
        _dispatched.Values.ShouldAllBe(count => count == 1);
    }

    [Fact]
    public async Task The_barrier_still_waits_for_every_wave_the_pipeline_started()
    {
        // The other half of the pipeline: ProcessPendingAsync may not return while a wave it handed over is
        // still running, or the M7 barrier — and the recovery summary — would speak for work still in
        // flight. The wedge is on the LAST page, the one the reader breaks out of the loop right after.
        var early = await SeedAsync(RecoveryPageSize, "default");
        var late  = await SeedAsync(40, "default");

        var wedge = Wedge(late[^1]);

        var recovery = Task.Run(() => CreateRecovery().ProcessPendingAsync());

        await WaitForEntry(late[^1]);

        recovery.IsCompleted.ShouldBeFalse("the recovery must not end while one of its waves is still running");

        wedge.TrySetResult();

        await recovery;

        _dispatched.Count.ShouldBe(early.Length + late.Length);
        _dispatched.Values.ShouldAllBe(count => count == 1);
    }
}
