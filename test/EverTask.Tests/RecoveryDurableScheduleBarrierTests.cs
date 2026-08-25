using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Top-level so Type.GetType(AssemblyQualifiedName) resolves it during recovery deserialization.
public record BarrierProbeTask : IEverTask;

/// <summary>
/// M7, the "children before parents" barrier of startup recovery: a durable schedule row asks how many of its
/// occurrences are still active the moment it is back, so it must not be recovered while an occurrence of its
/// own is still sitting in a page nobody has read yet — the count would come back phantom-low and the
/// materializer would overshoot <c>MaxPendingOccurrences</c>.
/// </summary>
/// <remarks>
/// The barrier is across the WHOLE recovered set, not page by page, and only a MULTI-PAGE backlog can tell the
/// two apart: the schedules here sit on the first page and their occurrences run past it, so a per-page
/// implementation would dispatch a schedule before occurrences it never saw.
/// Real <see cref="MemoryTaskStorage"/>, real keyset pagination, real <see cref="WorkerService"/>; the
/// dispatcher is the observation point, and the order it is called in is the fact under test.
/// </remarks>
public class RecoveryDurableScheduleBarrierTests
{
    /// <summary>Mirrors the constant in <see cref="WorkerService.ProcessPendingAsync"/>.</summary>
    private const int RecoveryPageSize = 100;

    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);
    private readonly List<Guid>        _dispatchOrder = [];

    private WorkerService CreateRecovery(ITaskStorage? storage = null)
    {
        var dispatcher = new Mock<ITaskDispatcherInternal>();
        dispatcher.Setup(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
                      It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
                      It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>(),
                      It.IsAny<DispatchRowMetadata>()))
                  .Returns((IEverTask _, DateTimeOffset? _, RecurringTask? _, int? _, CancellationToken _,
                            Guid? id, string? _, AuditLevel? _, bool _, DispatchRowMetadata _) =>
                  {
                      lock (_dispatchOrder) _dispatchOrder.Add(id!.Value);
                      return Task.FromResult(id!.Value);
                  });

        return RecoveryHarness.CreateRecoveryService(storage ?? _storage, dispatcher: dispatcher.Object);
    }

    private static QueuedTask Row(DateTimeOffset createdAt) => new()
    {
        Id           = Guid.NewGuid(),
        Type         = typeof(BarrierProbeTask).AssemblyQualifiedName!,
        Request      = EverTaskJson.Serialize(new BarrierProbeTask()),
        Handler      = "seeded-by-test",
        Status       = QueuedTaskStatus.Queued,
        CreatedAtUtc = createdAt
    };

    private async Task<QueuedTask> SeedDurableScheduleAsync(DateTimeOffset createdAt, DateTimeOffset cursor)
    {
        var row = Row(createdAt);
        row.IsRecurring   = true;
        row.RecurringTask = EverTaskJson.Serialize(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5),
            OccurrenceMode = OccurrenceMode.Durable
        });
        row.NextRunUtc = cursor;
        row.QueueName  = "recurring";

        await _storage.Persist(row);
        return row;
    }

    private async Task<QueuedTask> SeedOccurrenceAsync(Guid parentId, DateTimeOffset createdAt, DateTimeOffset slot)
    {
        var row = Row(createdAt);
        row.ParentTaskId          = parentId;
        row.ScheduledExecutionUtc = slot;
        row.Status                = QueuedTaskStatus.WaitingQueue;
        row.QueueName             = "recurring";

        await _storage.Persist(row);
        return row;
    }

    [Fact]
    public async Task Every_occurrence_is_recovered_before_any_durable_schedule_even_across_pages()
    {
        var origin = DateTimeOffset.UtcNow.AddHours(-6);

        // Both schedules land on the FIRST page — one as its very first row — while their occurrences run
        // well past it. Per page, the second schedule would go back before the ~150 occurrences behind it.
        var firstSchedule  = await SeedDurableScheduleAsync(origin, origin.AddHours(12));
        var secondSchedule = await SeedDurableScheduleAsync(origin.AddSeconds(50), origin.AddHours(12));

        var occurrences = new List<Guid>();
        for (var i = 0; i < 240; i++)
        {
            var parent = i % 2 == 0 ? firstSchedule.Id : secondSchedule.Id;
            var seeded = await SeedOccurrenceAsync(parent, origin.AddSeconds(100 + i), origin.AddMinutes(5 * i));
            occurrences.Add(seeded.Id);
        }

        // The premise the assertion rests on: the schedules really are on the first page, and the backlog
        // really does span several.
        var firstPage = await _storage.RetrievePending(DateTimeOffset.UtcNow, null, null, RecoveryPageSize);
        firstPage.Length.ShouldBe(RecoveryPageSize);
        firstPage.ShouldContain(t => t.Id == firstSchedule.Id);
        firstPage.ShouldContain(t => t.Id == secondSchedule.Id);
        firstPage.Count(t => occurrences.Contains(t.Id))
                 .ShouldBeLessThan(occurrences.Count, "most occurrences must sit on later pages");

        await CreateRecovery().ProcessPendingAsync();

        _dispatchOrder.Count.ShouldBe(242, "every seeded row must be recovered exactly once");

        var lastOccurrence = occurrences.Max(id => _dispatchOrder.IndexOf(id));
        _dispatchOrder.IndexOf(firstSchedule.Id).ShouldBeGreaterThan(lastOccurrence,
            "a durable schedule must not be recovered while an occurrence of its own is still in a later page");
        _dispatchOrder.IndexOf(secondSchedule.Id).ShouldBeGreaterThan(lastOccurrence);

        occurrences.ShouldAllBe(id => _dispatchOrder.Contains(id));
    }

    [Fact]
    public async Task A_second_keyset_scan_finds_the_schedules_instead_of_a_buffered_id_list()
    {
        // R8. Holding the schedule rows back used to mean holding something in memory for the whole
        // pagination — first the rows with their deserialized payload and definition, then their ids, a list
        // that still grew with the number of schedules. The second wave is now the SAME keyset query run a
        // second time over the same cutoff, keeping only the durable schedules: what crosses the loop is two
        // scalars, and a recovery holds one page whatever the backlog is.
        var probe  = new FaultInjectingTaskStorage(_storage);
        var origin = DateTimeOffset.UtcNow.AddHours(-6);

        var firstSchedule  = await SeedDurableScheduleAsync(origin, origin.AddHours(12));
        var secondSchedule = await SeedDurableScheduleAsync(origin.AddSeconds(50), origin.AddHours(12));

        for (var i = 0; i < 240; i++)
        {
            var parent = i % 2 == 0 ? firstSchedule.Id : secondSchedule.Id;
            await SeedOccurrenceAsync(parent, origin.AddSeconds(100 + i), origin.AddMinutes(5 * i));
        }

        // 242 rows in pages of 100: four calls walk the whole query once (three pages plus the empty one that
        // ends the loop). Anything more than that is a second pass, which is the fact under test.
        const int pagesForOnePass = 4;

        await CreateRecovery(probe).ProcessPendingAsync();

        probe.Calls[nameof(ITaskStorage.RetrievePending)].ShouldBeGreaterThan(pagesForOnePass,
            "the durable schedules are found by walking the recovery query a second time, not by reading " +
            "back a list of ids the first pass kept");

        _dispatchOrder.Count(id => id == firstSchedule.Id).ShouldBe(1,
            "a schedule the second scan meets again must still be recovered exactly once");
        _dispatchOrder.Count(id => id == secondSchedule.Id).ShouldBe(1);
        _dispatchOrder.Count.ShouldBe(242);
    }

    [Fact]
    public async Task A_recovery_with_no_durable_schedule_never_pays_for_the_second_scan()
    {
        // The other half of R8: the second pass is skipped outright when the first found nothing to defer, so
        // the legacy host — every host today — reads the same number of pages it always did.
        var probe  = new FaultInjectingTaskStorage(_storage);
        var origin = DateTimeOffset.UtcNow.AddHours(-6);

        for (var i = 0; i < 150; i++)
            await _storage.Persist(Row(origin.AddSeconds(i)));

        await CreateRecovery(probe).ProcessPendingAsync();

        probe.Calls[nameof(ITaskStorage.RetrievePending)].ShouldBe(3,
            "two pages plus the empty one that ends the loop, and no second pass at all");
    }

    [Fact]
    public async Task An_inline_recurring_schedule_is_not_held_back_by_the_barrier()
    {
        // The control: only OccurrenceMode.Durable defers a row to the second wave. An inline series has no
        // occurrence rows to wait for, so holding it back would delay every legacy schedule behind the whole
        // backlog for nothing.
        var origin = DateTimeOffset.UtcNow.AddHours(-6);

        var inline = Row(origin);
        inline.IsRecurring   = true;
        inline.RecurringTask = EverTaskJson.Serialize(new RecurringTask { MinuteInterval = new MinuteInterval(5) });
        inline.NextRunUtc    = origin.AddHours(12);
        await _storage.Persist(inline);

        var durable = await SeedDurableScheduleAsync(origin.AddSeconds(1), origin.AddHours(12));

        for (var i = 0; i < 120; i++)
            await SeedOccurrenceAsync(durable.Id, origin.AddSeconds(100 + i), origin.AddMinutes(5 * i));

        await CreateRecovery().ProcessPendingAsync();

        _dispatchOrder.IndexOf(inline.Id).ShouldBeLessThan(_dispatchOrder.IndexOf(durable.Id));
        _dispatchOrder.IndexOf(inline.Id).ShouldBeLessThan(RecoveryPageSize,
            "an inline series is recovered with the page it arrived on");
    }
}
