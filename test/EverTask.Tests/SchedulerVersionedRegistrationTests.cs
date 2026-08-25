using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// The conditional half of latest-wins (S1/S4): <c>TrySchedule</c> leaves a registration carrying a NEWER
/// schedule version alone, where <c>Schedule</c> replaces whatever it finds.
/// </summary>
/// <remarks>
/// A reschedule commits the new definition, parks its executor and only then publishes the version, so every
/// re-park still holding an executor of the definition it replaced — the next occurrence an advance had
/// already computed, the rate-limit gate's deferral, the materializer's operational retry — used to overwrite
/// that registration with a grid nobody owns any more. The published version then dropped it the moment it
/// fired, leaving the series in no scheduler, no queue and no delivery until a restart. The comparison has to
/// happen INSIDE the registry's own swap, which is why it lives here and not at the call sites.
/// [UNIT-necessario: direct inspection of the scheduler registry and heap.]
/// </remarks>
public class SchedulerVersionedRegistrationTests
{
    private static TaskHandlerExecutor CreateExecutor(DateTimeOffset executionTime, Guid id, int scheduleVersion) =>
        new TaskHandlerExecutor(new ResilienceCounterTask(0),
            new object(),
            null,
            executionTime,
            null, null, null, null, null,
            id,
            null,
            null,
            AuditLevel.Full) { ScheduleVersion = scheduleVersion };

    private static PeriodicTimerScheduler NewPeriodicScheduler() =>
        new(new Mock<IWorkerQueueManager>().Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromSeconds(30));

    private static ShardedScheduler NewShardedScheduler() =>
        new(new Mock<IWorkerQueueManager>().Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            taskStorage: null,
            shardCount: 2);

    [Fact]
    public void A_registration_of_an_older_schedule_version_is_refused_and_leaves_the_heap_alone()
    {
        using var scheduler = NewPeriodicScheduler();

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        var current   = CreateExecutor(future, id, scheduleVersion: 1);
        var superseded = CreateExecutor(future, id, scheduleVersion: 0);

        scheduler.Schedule(current);

        scheduler.TrySchedule(superseded).ShouldBeFalse();

        scheduler.GetQueue().Count.ShouldBe(1, "a refused registration must not leave an entry behind either");
        scheduler.TryUnschedule(id, current).ShouldBeTrue("the newer registration is the one still parked");
    }

    [Fact]
    public void A_registration_of_the_same_or_a_newer_version_replaces_it()
    {
        using var scheduler = NewPeriodicScheduler();

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        scheduler.Schedule(CreateExecutor(future, id, scheduleVersion: 1));

        var sameVersion = CreateExecutor(future, id, scheduleVersion: 1);
        scheduler.TrySchedule(sameVersion).ShouldBeTrue(
            "an ordinary advance re-parks the version it ran, which is not superseded by itself");
        scheduler.TryUnschedule(id, sameVersion).ShouldBeTrue();

        scheduler.Schedule(CreateExecutor(future, id, scheduleVersion: 1));

        var newer = CreateExecutor(future, id, scheduleVersion: 2);
        scheduler.TrySchedule(newer).ShouldBeTrue();
        scheduler.TryUnschedule(id, newer).ShouldBeTrue();
    }

    [Fact]
    public void An_unconditional_Schedule_still_replaces_a_newer_registration()
    {
        // The legacy path is untouched: a dispatch is the newest writer by definition and never asks.
        using var scheduler = NewPeriodicScheduler();

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        scheduler.Schedule(CreateExecutor(future, id, scheduleVersion: 3));

        var older = CreateExecutor(future, id, scheduleVersion: 0);
        scheduler.Schedule(older);

        scheduler.TryUnschedule(id, older).ShouldBeTrue();
    }

    [Fact]
    public void A_first_registration_is_never_refused()
    {
        using var scheduler = NewPeriodicScheduler();

        var id = Guid.NewGuid();

        scheduler.TrySchedule(CreateExecutor(DateTimeOffset.UtcNow.AddHours(1), id, scheduleVersion: 0))
                 .ShouldBeTrue("nothing is parked, so there is no newer registration to preserve");

        scheduler.IsScheduled(id).ShouldBeTrue();
    }

    [Fact]
    public void The_sharded_scheduler_answers_the_same_way()
    {
        using var scheduler = NewShardedScheduler();

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        var current = CreateExecutor(future, id, scheduleVersion: 1);
        scheduler.Schedule(current);

        scheduler.TrySchedule(CreateExecutor(future, id, scheduleVersion: 0)).ShouldBeFalse();
        scheduler.GetQueueCount(id).ShouldBe(1);
        scheduler.TryUnschedule(id, current).ShouldBeTrue();

        scheduler.Schedule(current);

        var newer = CreateExecutor(future, id, scheduleVersion: 2);
        scheduler.TrySchedule(newer).ShouldBeTrue();
        scheduler.TryUnschedule(id, newer).ShouldBeTrue();
    }

    [Fact]
    public void A_scheduler_compiled_before_the_member_keeps_registering_unconditionally()
    {
        // The default body: an external IScheduler that cannot compare versions behaves exactly as it always
        // did, which is what keeps the member additive.
        var external = new VersionBlindScheduler();
        IScheduler scheduler = external;

        var id = Guid.NewGuid();

        scheduler.TrySchedule(CreateExecutor(DateTimeOffset.UtcNow.AddHours(1), id, scheduleVersion: 0))
                 .ShouldBeTrue();

        external.Registered.ShouldBe(1);
    }

    private sealed class VersionBlindScheduler : IScheduler
    {
        public int Registered { get; private set; }

        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) => Registered++;

        public bool TryUnschedule(Guid persistenceId) => false;
    }
}
