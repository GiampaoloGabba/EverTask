using System.Collections.Concurrent;
using EverTask.Monitoring;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// What a dashboard receives for a materialized occurrence, produced by the real thing: a child row created by
/// the storage's own <c>MaterializeOccurrence</c>, recovered and executed by a real host, reported through the
/// worker's own monitoring gate.
/// </summary>
/// <remarks>
/// A canonical occurrence carries NO schedule definition — the contract strips it and leaves the parent id and
/// the version behind — so a mapping that read the version off the definition published null on precisely the
/// rows that report a schedule's executions. A hand-built executor with a definition on it cannot catch that:
/// it is a shape the materializer never writes.
/// </remarks>
public class OccurrenceMonitoringContextTests : IsolatedIntegrationTestBase
{
    private const int ScheduleVersion = 7;

    private readonly ResilienceTestState                _state  = new();
    private readonly ConcurrentQueue<EverTaskEventData> _events = new();

    private Task CollectEvent(EverTaskEventData data)
    {
        _events.Enqueue(data);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_executed_occurrence_reports_its_schedule_and_version_to_the_subscribers()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var slot     = DateTimeOffset.UtcNow.AddMinutes(-5);
        var parentId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = parentId,
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            CreatedAtUtc    = slot.AddHours(-1),
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(new RecurringTask { MinuteInterval = new MinuteInterval(5) }),
            NextRunUtc      = slot,
            ScheduleVersion = ScheduleVersion
        });

        var child = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            Type                  = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new ResilienceRecurringTask()),
            Handler               = "seeded-by-test",
            Status                = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = slot,
            ScheduledExecutionUtc = slot,
            ParentTaskId          = parentId
        };

        // The last slot of the series: the same call that creates the child ends the schedule, so the only row
        // left for the recovery to pick up is the occurrence itself.
        (await Storage.MaterializeOccurrence(parentId, ScheduleVersion, slot, child, newCursorUtc: null,
             AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);

        var stored = (await Storage.Get(t => t.Id == child.Id)).ShouldHaveSingleItem();
        stored.RecurringTask.ShouldBeNull("the canonical occurrence carries no definition — that is the point");
        stored.ScheduleVersion.ShouldBe(ScheduleVersion);

        WorkerExecutor.TaskEventOccurredAsync += CollectEvent;

        try
        {
            await Host!.StartAsync();

            await WaitForTaskStatusAsync(child.Id, QueuedTaskStatus.Completed,
                TestEnvironment.GetTimeout(8000, 30000));

            // Monitoring is fire-and-forget, so the event can land just after the terminal status write.
            await TaskWaitHelper.WaitForConditionAsync(
                () => _events.Any(e => e.TaskId == child.Id),
                TestEnvironment.GetTimeout(5000, 30000));
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= CollectEvent;
        }

        _state.ExecutedIndexes.Count.ShouldBe(1, "the occurrence is an ordinary one-shot row and must run once");

        var reported = _events.Where(e => e.TaskId == child.Id).ToArray();
        reported.ShouldNotBeEmpty();

        foreach (var data in reported)
        {
            data.ParentTaskId.ShouldBe(parentId);
            data.ScheduledAtUtc.ShouldBe(slot, "the nominal slot of the occurrence is what a dashboard shows");
            data.ScheduleVersion.ShouldBe(ScheduleVersion,
                "an occurrence has no definition to read the version from: it comes from the row, or the " +
                "execution cannot be associated with the version of the schedule that produced it");
        }
    }
}
