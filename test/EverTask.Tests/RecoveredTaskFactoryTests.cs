using EverTask.Handler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Worker;

namespace EverTask.Tests;

/// <summary>
/// The single place that turns a persisted row back into something runnable. Recovery used to re-derive that
/// metadata argument by argument at each call site, which is how a recovered task silently lost its audit
/// level; here every field comes from one mapping, and the two failure kinds stay apart because they lead to
/// opposite outcomes.
/// </summary>
public class RecoveredTaskFactoryTests
{
    public record ProbeTask(string Value) : IEverTask;

    private static QueuedTask Row(Action<QueuedTask>? customize = null)
    {
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(ProbeTask).AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(new ProbeTask("payload")),
            Handler      = "H",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        customize?.Invoke(row);
        return row;
    }

    [Fact]
    public void Rebuilds_every_field_a_re_dispatch_needs()
    {
        var slot   = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var parent = Guid.NewGuid();

        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.NextRunUtc      = slot;
            r.AuditLevel      = (int)AuditLevel.Minimal;
            r.ParentTaskId    = parent;
            r.RuntimeInfo     = "{\"SlotUtc\":\"x\"}";
            r.ScheduleVersion = 4;
            r.CurrentRunCount = 9;
            r.TaskKey         = "key";
            r.QueueName       = "recurring";
        }));

        recovered.Task.ShouldBeOfType<ProbeTask>().Value.ShouldBe("payload");
        recovered.AuditLevel.ShouldBe(AuditLevel.Minimal);
        recovered.ExecutionTime.ShouldBe(slot);
        recovered.ParentTaskId.ShouldBe(parent);
        recovered.RuntimeInfo.ShouldBe("{\"SlotUtc\":\"x\"}");
        recovered.ScheduleVersion.ShouldBe(4);
        recovered.CurrentRunCount.ShouldBe(9);
        recovered.TaskKey.ShouldBe("key");
        recovered.QueueName.ShouldBe("recurring");
        recovered.TypeWasLoadable.ShouldBeTrue();
        recovered.PayloadError.ShouldBeNull();
        recovered.ScheduleError.ShouldBeNull();

        // The re-dispatch's compare-and-swap expectations travel with the row: the finalization must expect
        // the status and version THIS page read, never the ones a later read would find.
        recovered.Status.ShouldBe(QueuedTaskStatus.Queued);
        recovered.RowMetadata.Status.ShouldBe(QueuedTaskStatus.Queued);
        recovered.RowMetadata.ScheduleVersion.ShouldBe(4);
    }

    [Fact]
    public void An_occurrence_takes_its_slot_and_its_run_number_from_its_own_metadata()
    {
        // The two values are deliberately distinguishable from what the columns would give (the slot column
        // is an hour off, the run counter is a child's zero): only the metadata can produce this answer, so
        // the test says WHICH source was read, not just that the numbers look plausible.
        var slot = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.ParentTaskId          = Guid.NewGuid();
            r.ScheduledExecutionUtc = slot.AddHours(1);
            r.CurrentRunCount       = 0;
            r.RuntimeInfo           = EverTaskJson.Serialize(
                new OccurrenceRuntimeInfo { SlotUtc = slot, RunNumber = 42 });
        }));

        recovered.RowMetadata.NominalSlotUtc.ShouldBe(slot,
            "the durable slot is what the row states, never the moment the scheduler happens to fire it");
        recovered.RowMetadata.RunNumber.ShouldBe(42,
            "an occurrence is a one-shot: its own counter says nothing about the run of the series it is");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("{\"Halted\":{\"Reason\":\"CapExceeded\"}}")]
    public void An_occurrence_without_readable_metadata_falls_back_to_its_columns(string? runtimeInfo)
    {
        // Unreadable or foreign metadata must not cost a delivery: the caller keeps the column-derived
        // answer, which is what every row written before the metadata existed gets anyway.
        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.ParentTaskId          = Guid.NewGuid();
            r.ScheduledExecutionUtc = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
            r.RuntimeInfo           = runtimeInfo;
        }));

        recovered.RowMetadata.NominalSlotUtc.ShouldBeNull();
        recovered.RowMetadata.RunNumber.ShouldBeNull();
        recovered.ExecutionTime.ShouldBe(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            "the scheduled column is the fallback the executor uses for the slot");
    }

    [Fact]
    public void A_schedule_rows_own_runtime_state_is_never_read_as_occurrence_metadata()
    {
        // The same column holds the runtime state of a durable SCHEDULE. A schedule is not an occurrence of
        // anything, so nothing there may end up stamped on its delivery.
        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.IsRecurring   = true;
            r.RecurringTask = EverTaskJson.Serialize(new RecurringTask { SecondInterval = new SecondInterval(30) });
            r.RuntimeInfo   = EverTaskJson.Serialize(new OccurrenceRuntimeInfo { RunNumber = 99 });
        }));

        recovered.ParentTaskId.ShouldBeNull();
        recovered.RowMetadata.RunNumber.ShouldBeNull("a schedule's run number comes from its own counter");
        recovered.RowMetadata.NominalSlotUtc.ShouldBeNull();
    }

    [Fact]
    public void A_row_written_before_per_task_audit_levels_recovers_as_Full()
    {
        RecoveredTaskFactory.FromRow(Row(r => r.AuditLevel = null)).AuditLevel.ShouldBe(AuditLevel.Full);
    }

    [Fact]
    public void A_recurring_row_resumes_from_its_cursor_and_everything_else_from_its_scheduled_time()
    {
        var scheduled = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var cursor    = scheduled.AddHours(5);

        RecoveredTaskFactory.FromRow(Row(r => r.ScheduledExecutionUtc = scheduled)).ExecutionTime
                            .ShouldBe(scheduled);

        RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.ScheduledExecutionUtc = scheduled;
            r.NextRunUtc            = cursor;
        })).ExecutionTime.ShouldBe(cursor);
    }

    [Fact]
    public void An_unreadable_payload_is_reported_apart_from_a_loadable_type()
    {
        // The type loads, the payload does not: that may heal in a later build, so it must not be confused
        // with a type that is simply gone.
        var recovered = RecoveredTaskFactory.FromRow(Row(r => r.Request = "{ this is not json"));

        recovered.TypeWasLoadable.ShouldBeTrue();
        recovered.Task.ShouldBeNull();
        recovered.PayloadError.ShouldNotBeNull();
        recovered.ScheduleError.ShouldBeNull();
    }

    [Fact]
    public void A_type_that_no_longer_exists_is_reported_as_not_loadable()
    {
        var recovered = RecoveredTaskFactory.FromRow(Row(r => r.Type = "Gone.Type, Gone.Assembly"));

        recovered.TypeWasLoadable.ShouldBeFalse();
        recovered.Task.ShouldBeNull();
    }

    [Fact]
    public void A_schedule_that_deserializes_but_is_corrupt_is_reported_as_a_schedule_error()
    {
        // An unparseable cron survives deserialization and only blows up at next-run time, where it becomes a
        // bounded per-restart failure instead of a terminal poison — so it is validated here.
        var corrupt = EverTaskJson.Serialize(new RecurringTask { CronInterval = new CronInterval("not a cron") });

        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.IsRecurring   = true;
            r.RecurringTask = corrupt;
        }));

        recovered.Recurring.ShouldBeNull("a null schedule is what routes the row to the terminal poison path");
        recovered.ScheduleError.ShouldNotBeNull();
    }

    [Fact]
    public void An_occurrence_mode_outside_the_defined_values_is_a_schedule_error()
    {
        // B2/R3: the tolerant enum converter passes an unknown numeric value through rather than failing the
        // whole payload, so enforcing the defined set is Validate()'s job. Left alone, (OccurrenceMode)2 is
        // simply "not Durable": the row would quietly run on the inline path — the schedule executing its own
        // handler — instead of being poisoned like every other corrupt schedule value.
        var corrupt = EverTaskJson.Serialize(new RecurringTask
                                  {
                                      SecondInterval = new SecondInterval(30),
                                      OccurrenceMode = OccurrenceMode.Durable
                                  })
                                  .Replace("\"OccurrenceMode\":1", "\"OccurrenceMode\":2", StringComparison.Ordinal);

        corrupt.ShouldContain("\"OccurrenceMode\":2");

        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.IsRecurring   = true;
            r.RecurringTask = corrupt;
        }));

        recovered.Recurring.ShouldBeNull("a null schedule is what routes the row to the terminal poison path");
        recovered.ScheduleError.ShouldNotBeNull();
        recovered.IsDurableSchedule.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OccurrenceMode.Inline, false)]
    [InlineData(OccurrenceMode.Durable, true)]
    public void The_occurrence_mode_decides_whether_the_row_is_a_durable_schedule(OccurrenceMode mode, bool expected)
    {
        var schedule = EverTaskJson.Serialize(new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            OccurrenceMode = mode
        });

        var recovered = RecoveredTaskFactory.FromRow(Row(r =>
        {
            r.IsRecurring   = true;
            r.RecurringTask = schedule;
        }));

        recovered.Recurring.ShouldNotBeNull().OccurrenceMode.ShouldBe(mode);
        recovered.IsDurableSchedule.ShouldBe(expected,
            "the recovery defers durable schedules to a second pass, after every occurrence is back");
    }

    [Theory]
    [InlineData(OccurrenceMode.Inline, false)]
    [InlineData(OccurrenceMode.Durable, true)]
    public void An_executor_is_schedule_only_exactly_when_its_schedule_is_durable(OccurrenceMode mode, bool expected)
    {
        var executor = new TaskHandlerExecutor(
            new ProbeTask("x"), new object(), null, DateTimeOffset.UtcNow,
            new RecurringTask { SecondInterval = new SecondInterval(30), OccurrenceMode = mode },
            null, null, null, null, Guid.NewGuid(), null, null, AuditLevel.Full);

        executor.IsScheduleOnly.ShouldBe(expected);
    }

    [Fact]
    public void A_one_shot_executor_is_never_schedule_only()
    {
        var executor = new TaskHandlerExecutor(
            new ProbeTask("x"), new object(), null, DateTimeOffset.UtcNow, null,
            null, null, null, null, Guid.NewGuid(), null, null, AuditLevel.Full);

        executor.IsScheduleOnly.ShouldBeFalse();
    }
}
