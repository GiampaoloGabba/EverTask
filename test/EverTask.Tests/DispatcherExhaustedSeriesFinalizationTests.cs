using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// X3, the dispatcher half: when startup recovery finds a series whose every remaining slot falls past its
/// bounds, the dispatcher ends it — and that terminal write is a compare-and-swap on the values the DECISION
/// was computed from, which is the row the recovery page read.
/// </summary>
/// <remarks>
/// Reading the row back at write time is what this pins against. A <c>Cancel</c> that linearizes between the
/// page read and the finalization leaves <c>NextRunUtc</c> and <c>ScheduleVersion</c> untouched (<c>SetStatus</c>
/// deliberately does not stamp a cancellation), so a fresh read would hand <c>Cancelled</c> to the
/// compare-and-swap as the expected status: the guard would match, and the status the user chose would be
/// overwritten with <c>Completed</c>. Real <see cref="MemoryTaskStorage"/> throughout — the compare-and-swap
/// itself is what has to lose.
/// </remarks>
public class DispatcherExhaustedSeriesFinalizationTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);
    private readonly Dispatcher.Dispatcher _dispatcher;

    public DispatcherExhaustedSeriesFinalizationTests()
    {
        var serviceProvider = new Mock<IServiceProvider>();
        var scopeFactory    = new Mock<IServiceScopeFactory>();
        var scope           = new Mock<IServiceScope>();

        scope.Setup(s => s.ServiceProvider).Returns(() => serviceProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        serviceProvider.Setup(s => s.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactory.Object);
        serviceProvider.Setup(s => s.GetService(typeof(IGateInvalidationRegistry)))
                       .Returns(new GateInvalidationRegistry());

        _dispatcher = new Dispatcher.Dispatcher(
            serviceProvider.Object,
            new Mock<IWorkerQueueManager>().Object,
            new Mock<IScheduler>().Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskLogger<Dispatcher.Dispatcher>>().Object,
            new Mock<IWorkerBlacklist>().Object,
            new Mock<ICancellationSourceProvider>().Object,
            _storage);
    }

    /// <summary>A daily series whose slot and bounds are both months old: nothing left to run, no grace.</summary>
    private static RecurringTask ExhaustedDefinition(DateTimeOffset now) => new()
    {
        DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
        RunUntil    = now.AddDays(-60)
    };

    private async Task<QueuedTask> SeedAsync(DateTimeOffset now, RecurringTask recurring)
    {
        var row = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = "{}",
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(recurring),
            NextRunUtc      = now.AddDays(-90),
            RunUntil        = recurring.RunUntil,
            CurrentRunCount = 1,
            CreatedAtUtc    = now.AddDays(-200)
        };

        await _storage.Persist(row);
        return row;
    }

    /// <summary>Replays what the recovery loop does with a page it has already read.</summary>
    private Task<Guid> RecoverAsync(QueuedTask pagedRow, RecurringTask recurring) =>
        ((ITaskDispatcherInternal)_dispatcher).ExecuteDispatch(
            new ResilienceRecurringTask(), pagedRow.NextRunUtc, recurring, pagedRow.CurrentRunCount,
            CancellationToken.None, pagedRow.Id, null, AuditLevel.Full, isRecovery: true,
            rowMetadata: new DispatchRowMetadata(null, null, pagedRow.ScheduleVersion, pagedRow.QueueName,
                pagedRow.Status));

    [Fact]
    public async Task An_exhausted_series_is_finalized_when_nothing_moved_under_the_decision()
    {
        var now       = DateTimeOffset.UtcNow;
        var recurring = ExhaustedDefinition(now);
        var row       = await SeedAsync(now, recurring);

        await RecoverAsync(row, recurring);

        var finalized = (await _storage.Get(t => t.Id == row.Id))[0];
        finalized.Status.ShouldBe(QueuedTaskStatus.Completed);
        finalized.NextRunUtc.ShouldBeNull("the cursor is what kept the zombie alive across restarts");
    }

    [Fact]
    public async Task A_cancel_that_linearized_after_the_page_read_defeats_the_finalization()
    {
        var now       = DateTimeOffset.UtcNow;
        var recurring = ExhaustedDefinition(now);
        var pagedRow  = await SeedAsync(now, recurring);

        // The row as the recovery page read it, before the user's cancel.
        var pagedSnapshot = new QueuedTask
        {
            Id              = pagedRow.Id,
            Status          = pagedRow.Status,
            NextRunUtc      = pagedRow.NextRunUtc,
            ScheduleVersion = pagedRow.ScheduleVersion,
            CurrentRunCount = pagedRow.CurrentRunCount
        };

        // ITaskDispatcher.Cancel's last write. SetStatus deliberately leaves a cancellation's cursor and
        // version alone, so nothing about the row tells the finalization apart from the state it decided on
        // — except the status it was supposed to expect.
        await _storage.SetCancelledByUser(pagedRow.Id, AuditLevel.Full);

        await RecoverAsync(pagedSnapshot, recurring);

        var afterRecovery = (await _storage.Get(t => t.Id == pagedRow.Id))[0];
        afterRecovery.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the compare-and-swap must lose to the cancellation, not absorb it into its own expectation");
        afterRecovery.NextRunUtc.ShouldNotBeNull("nothing rewrote the cancelled row");
        afterRecovery.StatusAudits.Select(a => a.NewStatus)
                     .ShouldNotContain(QueuedTaskStatus.Completed,
                         "a Cancelled -> Completed audit is the fingerprint of the overwrite");
    }

    [Fact]
    public async Task A_reschedule_that_bumped_the_version_after_the_page_read_defeats_the_finalization()
    {
        var now       = DateTimeOffset.UtcNow;
        var recurring = ExhaustedDefinition(now);
        var pagedRow  = await SeedAsync(now, recurring);

        var pagedSnapshot = new QueuedTask
        {
            Id              = pagedRow.Id,
            Status          = pagedRow.Status,
            NextRunUtc      = pagedRow.NextRunUtc,
            ScheduleVersion = pagedRow.ScheduleVersion,
            CurrentRunCount = pagedRow.CurrentRunCount
        };

        // A reschedule bumps the version while leaving the cursor where it was: from phase 5 on, this window
        // is the one that would close a series with work still ahead of it.
        var live = (await _storage.Get(t => t.Id == pagedRow.Id))[0];
        live.ScheduleVersion = 1;
        await _storage.UpdateTask(live);

        await RecoverAsync(pagedSnapshot, recurring);

        var afterRecovery = (await _storage.Get(t => t.Id == pagedRow.Id))[0];
        afterRecovery.Status.ShouldBe(QueuedTaskStatus.Queued, "the schedule was rescheduled under the decision");
        afterRecovery.NextRunUtc.ShouldNotBeNull();
    }
}
