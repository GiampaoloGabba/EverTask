using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.Serialization;

/// <summary>
/// B3 / P1-1: the <c>OnTimes</c> setter on <see cref="DayInterval"/>/<see cref="WeekInterval"/>/
/// <see cref="MonthInterval"/> dereferences <c>value</c> without a null guard, so a persisted
/// <c>"OnTimes":null</c> makes the REAL <see cref="EverTaskJson"/> deserialize throw inside the setter. A
/// "strange but well-formed" recurring row must not crash the deserialize; the setter must default to
/// midnight. Asserted against the REAL serializer (a pure (de)serialization invariant, reachable without a
/// host — hence a unit test) plus an end-to-end recovery variant that proves the fix on the real flow.
/// </summary>
public class OnTimesNullSafeTests : IsolatedIntegrationTestBase
{
    [Fact]
    public void DayInterval_with_null_OnTimes_deserializes_to_midnight()
    {
        var day = EverTaskJson.Deserialize<DayInterval>("{\"Interval\":1,\"OnTimes\":null}")!;
        day.OnTimes.ShouldBe(new[] { new TimeOnly(0, 0) });
    }

    [Fact]
    public void WeekInterval_with_null_OnTimes_deserializes_to_midnight()
    {
        var week = EverTaskJson.Deserialize<WeekInterval>("{\"Interval\":1,\"OnTimes\":null}")!;
        week.OnTimes.ShouldBe(new[] { new TimeOnly(0, 0) });
    }

    [Fact]
    public void MonthInterval_with_null_OnTimes_deserializes_to_midnight()
    {
        var month = EverTaskJson.Deserialize<MonthInterval>("{\"Interval\":1,\"OnTimes\":null}")!;
        month.OnTimes.ShouldBe(new[] { new TimeOnly(0, 0) });
    }

    [Fact]
    public void Empty_OnTimes_is_preserved_and_does_not_throw()
    {
        // An EMPTY OnTimes is an already-safe "no time-of-day constraint" state (it never crashed the setter,
        // unlike null) and carries distinct downstream semantics — so the null fix must NOT rewrite it.
        EverTaskJson.Deserialize<DayInterval>("{\"Interval\":1,\"OnTimes\":[]}")!
            .OnTimes.ShouldBeEmpty();
    }

    [Fact]
    public void Valid_OnTimes_still_roundtrips_sorted()
    {
        var day = EverTaskJson.Deserialize<DayInterval>(
            EverTaskJson.Serialize(new DayInterval(1) { OnTimes = new[] { new TimeOnly(18, 0), new TimeOnly(9, 0) } }))!;
        day.OnTimes.ShouldBe(new[] { new TimeOnly(9, 0), new TimeOnly(18, 0) });
    }

    [Fact]
    public async Task Recurring_row_with_null_OnTimes_recovers_normally_and_is_not_poisoned()
    {
        // End-to-end: a recurring row whose persisted DayInterval has OnTimes:null must recover normally with
        // B3 (the setter defaults to midnight), NOT be poisoned because the deserialize threw. The schedule is
        // otherwise valid (OnDays Monday), so the row stays a live recurring task after startup recovery.
        var state = new ResilienceTestState();
        await CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddMemoryStorage();
                builder.Services.AddSingleton(state);
            },
            startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceRecurringTask().GetType().AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            // DayInterval with OnDays=[Monday] but OnTimes explicitly null (a manipulated/strange-but-formed row).
            RecurringTask   = "{\"DayInterval\":{\"Interval\":0,\"OnDays\":[1],\"OnTimes\":null}}",
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(30),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        // A sentinel one-shot proves the startup recovery actually ran before we assert (no timing/sleep).
        var sentinelId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id           = sentinelId,
            Type         = new ResilienceCounterTask(321).GetType().AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(new ResilienceCounterTask(321)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => state.ExecutedIndexes.Contains(321), timeoutMs: 10000);

        // Recovery has run. The OnTimes:null row must NOT have been poisoned: with B3 the setter defaults to
        // midnight, so the deserialize no longer throws and the recurring row stays alive with its schedule.
        var revived = (await Storage.GetAll()).First(t => t.Id == taskId);
        revived.Status.ShouldNotBe(QueuedTaskStatus.Failed,
            "a recurring row with OnTimes:null must recover normally with B3, not be poisoned (P1-1)");
        revived.NextRunUtc.ShouldNotBeNull(
            "the revived recurring row keeps its schedule (NextRunUtc preserved)");
    }
}
