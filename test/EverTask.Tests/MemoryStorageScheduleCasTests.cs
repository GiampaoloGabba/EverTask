using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// S1 for the in-memory provider: MemoryTaskStorage advertises <c>SupportsScheduleVersioning</c>, so its
/// versioned overloads of <c>UpdateCurrentRun</c> / <c>CompleteRecurringRun</c> owe callers a real
/// compare-and-swap — the same contract a conditional UPDATE gives the relational providers (pinned there by
/// <c>EfCoreTaskStorageTestsBase</c>). Check and write must share ONE critical section: while the check
/// released the store lock before the write, a reschedule landing in the gap let the stale run report
/// <c>Applied</c> and still write its old-definition cursor, so the schedule kept firing on the definition
/// the user had just replaced.
/// </summary>
public class MemoryStorageScheduleCasTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private static readonly DateTimeOffset Cursor            = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StaleNextRun      = Cursor.AddHours(1);
    private static readonly DateTimeOffset RescheduledCursor = Cursor.AddHours(5);

    private const string OldDefinition = """{"every":"hour"}""";
    private const string NewDefinition = """{"every":"day"}""";

    private async Task<Guid> SeedScheduleAsync(int scheduleVersion = 0, int currentRunCount = 0)
    {
        var id = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = "T",
            Request         = "{}",
            Handler         = "H",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            RecurringTask   = OldDefinition,
            NextRunUtc      = Cursor,
            CurrentRunCount = currentRunCount,
            ScheduleVersion = scheduleVersion
        });
        return id;
    }

    private async Task<QueuedTask> ReloadAsync(Guid id) => (await _storage.Get(t => t.Id == id))[0];

    [Fact]
    public async Task Should_write_nothing_when_UpdateCurrentRun_carries_a_stale_schedule_version()
    {
        var id = await SeedScheduleAsync(scheduleVersion: 5);

        (await _storage.UpdateCurrentRun(id, 12, StaleNextRun, AuditLevel.Full, 4))
            .ShouldBe(ScheduleCasResult.VersionMismatch);

        var row = await ReloadAsync(id);
        row.NextRunUtc.ShouldBe(Cursor, "a run that finished after a reschedule must not force its stale next run");
        row.CurrentRunCount.ShouldBe(0);
        row.ExecutionTimeMs.ShouldBe(0);
        row.RunsAudits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_advance_when_UpdateCurrentRun_carries_the_current_schedule_version()
    {
        var id = await SeedScheduleAsync(scheduleVersion: 5);

        (await _storage.UpdateCurrentRun(id, 12, StaleNextRun, AuditLevel.Full, 5))
            .ShouldBe(ScheduleCasResult.Applied);

        var row = await ReloadAsync(id);
        row.NextRunUtc.ShouldBe(StaleNextRun);
        row.CurrentRunCount.ShouldBe(1);
        row.ExecutionTimeMs.ShouldBe(12);
        row.RunsAudits.Count.ShouldBe(1, "the winning CAS must produce the same audit trail as the plain overload");
    }

    [Fact]
    public async Task Should_write_nothing_when_CompleteRecurringRun_carries_a_stale_schedule_version()
    {
        var id = await SeedScheduleAsync(scheduleVersion: 2);

        (await _storage.CompleteRecurringRun(id, 8, StaleNextRun, AuditLevel.Full, 1))
            .ShouldBe(ScheduleCasResult.VersionMismatch);

        var row = await ReloadAsync(id);
        row.Status.ShouldBe(QueuedTaskStatus.Queued);
        row.NextRunUtc.ShouldBe(Cursor);
        row.CurrentRunCount.ShouldBe(0);
        row.StatusAudits.ShouldBeEmpty();
        row.RunsAudits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_complete_when_CompleteRecurringRun_carries_the_current_schedule_version()
    {
        var id = await SeedScheduleAsync(scheduleVersion: 2);

        (await _storage.CompleteRecurringRun(id, 8, StaleNextRun, AuditLevel.Full, 2))
            .ShouldBe(ScheduleCasResult.Applied);

        var row = await ReloadAsync(id);
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.NextRunUtc.ShouldBe(StaleNextRun);
        row.CurrentRunCount.ShouldBe(1);
        row.StatusAudits.Count.ShouldBe(1);
        row.RunsAudits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_move_the_cursor_of_a_skipped_slot_without_counting_a_run()
    {
        // The one write a SKIP needs: nothing executed, so no run, no status transition and no audit — only
        // the cursor moves. Every kept slot carries its own jump inside the materialization instead.
        var id = await SeedScheduleAsync();

        (await _storage.TryAdvanceScheduleCursor(id, 0, Cursor, RescheduledCursor)).ShouldBeTrue();

        var row = await ReloadAsync(id);
        row.NextRunUtc.ShouldBe(RescheduledCursor);
        row.CurrentRunCount.ShouldBe(0, "nothing ran, so nothing may be counted against MaxRuns");
        row.Status.ShouldBe(QueuedTaskStatus.Queued);
        row.StatusAudits.ShouldBeEmpty();
        row.RunsAudits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_refuse_a_cursor_advance_computed_against_a_state_that_moved()
    {
        var rescheduled = await SeedScheduleAsync(scheduleVersion: 2);

        (await _storage.TryAdvanceScheduleCursor(rescheduled, 0, Cursor, RescheduledCursor))
            .ShouldBeFalse("a schedule rescheduled under the caller is a lost race");

        var advanced = await SeedScheduleAsync();
        (await _storage.TryAdvanceScheduleCursor(advanced, 0, StaleNextRun, RescheduledCursor))
            .ShouldBeFalse("and so is a cursor another writer already moved");

        (await ReloadAsync(advanced)).NextRunUtc.ShouldBe(Cursor);
    }

    [Fact]
    public async Task Should_report_VersionMismatch_when_the_schedule_row_is_gone()
    {
        // Parity with the relational providers, where the conditional UPDATE simply matches zero rows.
        var missing = TestGuidGenerator.New();

        (await _storage.UpdateCurrentRun(missing, 1, StaleNextRun, AuditLevel.Full, 0))
            .ShouldBe(ScheduleCasResult.VersionMismatch);
        (await _storage.CompleteRecurringRun(missing, 1, StaleNextRun, AuditLevel.Full, 0))
            .ShouldBe(ScheduleCasResult.VersionMismatch);
    }

    [Fact]
    public Task Should_keep_the_new_cursor_when_UpdateCurrentRun_races_a_reschedule() =>
        RunAdvanceVersusRescheduleRaceAsync(
            (id, next) => _storage.UpdateCurrentRun(id, 12, next, AuditLevel.Full, 0));

    [Fact]
    public Task Should_keep_the_new_cursor_when_CompleteRecurringRun_races_a_reschedule() =>
        RunAdvanceVersusRescheduleRaceAsync(
            (id, next) => _storage.CompleteRecurringRun(id, 12, next, AuditLevel.Full, 0));

    /// <summary>
    /// Races, on many independent schedules at once, an advance computed against version 0 and cursor
    /// <see cref="Cursor"/> against a reschedule that read that SAME state. Exactly one of them may commit:
    /// each carries the reading it decided from into its own compare-and-swap, so whichever runs second finds
    /// the row moved and writes nothing.
    /// </summary>
    /// <remarks>
    /// The reschedule's expectation has to include the CURSOR, not the version alone: an advance moves the
    /// cursor and the run counter without ever touching the version, so a version-only guard lets a
    /// reschedule that decided on a run count and a cursor the completion has already superseded commit over
    /// it — which is how a rebase computed from that stale reading gets parked one occurrence past the budget
    /// it was given.
    /// </remarks>
    private async Task RunAdvanceVersusRescheduleRaceAsync(Func<Guid, DateTimeOffset, Task<ScheduleCasResult>> advance)
    {
        const int schedules = 400;

        var ids = new Guid[schedules];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = await SeedScheduleAsync();

        var advanced    = new ScheduleCasResult[schedules];
        var rescheduled = new bool[schedules];
        var gate        = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers      = new List<Task>(schedules * 2);

        for (var i = 0; i < ids.Length; i++)
        {
            var index = i;
            var id    = ids[i];

            racers.Add(Task.Run(async () =>
            {
                await gate.Task;
                advanced[index] = await advance(id, StaleNextRun);
            }));

            racers.Add(Task.Run(async () =>
            {
                await gate.Task;
                rescheduled[index] = await _storage.UpdateSchedule(id, 0, Cursor, NewDefinition, null,
                    RescheduledCursor, null, null, null);
            }));
        }

        gate.SetResult();
        await Task.WhenAll(racers);

        for (var i = 0; i < ids.Length; i++)
        {
            var row = await ReloadAsync(ids[i]);
            var ran = advanced[i] == ScheduleCasResult.Applied;

            ran.ShouldNotBe(rescheduled[i],
                "both readings are of the same state, so exactly one of the two writes may commit");

            row.CurrentRunCount.ShouldBe(ran ? 1 : 0,
                "the run counter must move if and only if the compare-and-swap reported Applied");

            if (ran)
            {
                row.ScheduleVersion.ShouldBe(0);
                row.RecurringTask.ShouldBe(OldDefinition);
                row.NextRunUtc.ShouldBe(StaleNextRun,
                    "the advance got there first, so the reschedule lost on the cursor and wrote nothing");
            }
            else
            {
                row.ScheduleVersion.ShouldBe(1);
                row.RecurringTask.ShouldBe(NewDefinition);
                row.NextRunUtc.ShouldBe(RescheduledCursor);
            }
        }
    }

    [Fact]
    public async Task Should_stamp_LastExecutionUtc_on_a_finalization_that_ran_nothing()
    {
        // The fifth implementation of what EfCoreTaskStorageTestsBase pins on the four relational ones (X3):
        // finalizing is a terminal transition, so it writes LastExecutionUtc even though the slots left
        // behind never executed — and the retention window measured from that column moves with it. Both
        // terminal writes owe the same stamp, the unconditional one and the compare-and-swap.
        var unconditional = await SeedScheduleAsync(currentRunCount: 4);
        var conditional   = await SeedScheduleAsync(currentRunCount: 4);

        var before = DateTimeOffset.UtcNow;

        await _storage.SetRecurringSeriesCompleted(unconditional, 0, AuditLevel.Full);
        (await _storage.TrySetRecurringSeriesCompleted(conditional, Cursor, QueuedTaskStatus.Queued, 0, 0,
            AuditLevel.Full)).ShouldBeTrue();

        var after = DateTimeOffset.UtcNow;

        foreach (var id in new[] { unconditional, conditional })
        {
            var row = await ReloadAsync(id);

            row.Status.ShouldBe(QueuedTaskStatus.Completed);
            row.NextRunUtc.ShouldBeNull();
            row.CurrentRunCount.ShouldBe(4, "the slots left behind were never executed (Option B)");
            row.RunsAudits.ShouldBeEmpty("no run, no runs audit");

            row.LastExecutionUtc.ShouldNotBeNull().ShouldBeInRange(before, after,
                "a series that ends without running still records when it ended");
        }
    }

    [Fact]
    public async Task Should_refuse_an_UpdateSchedule_on_a_series_that_was_cancelled_under_it()
    {
        // The memory store's half of what EfCoreTaskStorageTestsBase pins on the four relational ones: a
        // cancel writes the STATUS and leaves the version and the cursor exactly as they were, so both halves
        // of the compare-and-swap still match and only the status can refuse the write.
        var id = await SeedScheduleAsync();

        await _storage.SetStatus(id, QueuedTaskStatus.Cancelled, null, AuditLevel.Full);

        (await _storage.UpdateSchedule(id, 0, Cursor, NewDefinition, null, RescheduledCursor, null, null, null))
            .ShouldBeFalse("a cancellation is terminal for the series");

        var row = await ReloadAsync(id);
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled);
        row.ScheduleVersion.ShouldBe(0);
        row.NextRunUtc.ShouldBe(Cursor);
        row.RecurringTask.ShouldBe(OldDefinition);
    }

    [Fact]
    public async Task Should_refuse_a_null_expected_cursor_on_both_conditional_schedule_writes()
    {
        // The memory store's half of the contract pinned for the relational providers in
        // EfCoreTaskStorageTestsBase: a null expected cursor describes no live schedule, only the rows that
        // have already ended, so both writes must lose instead of matching them.
        var id = await SeedScheduleAsync();

        (await _storage.TrySetRecurringSeriesCompleted(id, null, QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full))
            .ShouldBeFalse();
        (await _storage.TryHaltSchedule(id, 0, null, QueuedTaskStatus.Queued, "{}")).ShouldBeFalse();

        var live = await ReloadAsync(id);
        live.Status.ShouldBe(QueuedTaskStatus.Queued);
        live.NextRunUtc.ShouldBe(Cursor);
        live.RuntimeInfo.ShouldBeNull();

        (await _storage.TrySetRecurringSeriesCompleted(id, Cursor, QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full))
            .ShouldBeTrue();

        (await _storage.TryHaltSchedule(id, 0, null, QueuedTaskStatus.Completed, "{\"Halted\":{}}"))
            .ShouldBeFalse("a finished series is not a schedule to halt");

        var finished = await ReloadAsync(id);
        finished.NextRunUtc.ShouldBeNull();
        finished.RuntimeInfo.ShouldBeNull();
    }
}
