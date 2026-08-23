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
    /// Races, on many independent schedules at once, an advance computed against version 0 against a
    /// reschedule that consumes that same version 0. Both linearizable orders leave the SAME observable
    /// state — the reschedule's cursor and definition — because the advance either ran first and was
    /// overwritten, or lost the compare-and-swap and wrote nothing. A cursor check that does not share the
    /// write's critical section produces a third, illegal outcome: the advance reports <c>Applied</c> and
    /// leaves its old-definition cursor on a row that has already moved to version 1.
    /// </summary>
    private async Task RunAdvanceVersusRescheduleRaceAsync(Func<Guid, DateTimeOffset, Task<ScheduleCasResult>> advance)
    {
        const int schedules = 400;

        var ids = new Guid[schedules];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = await SeedScheduleAsync();

        var outcomes = new ScheduleCasResult[schedules];
        var gate     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers   = new List<Task>(schedules * 2);

        for (var i = 0; i < ids.Length; i++)
        {
            var index = i;
            var id    = ids[i];

            racers.Add(Task.Run(async () =>
            {
                await gate.Task;
                outcomes[index] = await advance(id, StaleNextRun);
            }));

            racers.Add(Task.Run(async () =>
            {
                await gate.Task;
                (await _storage.UpdateSchedule(id, 0, NewDefinition, null, RescheduledCursor, null, null, null))
                    .ShouldBeTrue("no advance bumps the version, so the reschedule always owns version 0");
            }));
        }

        gate.SetResult();
        await Task.WhenAll(racers);

        for (var i = 0; i < ids.Length; i++)
        {
            var row = await ReloadAsync(ids[i]);

            row.ScheduleVersion.ShouldBe(1);
            row.RecurringTask.ShouldBe(NewDefinition);
            row.NextRunUtc.ShouldBe(RescheduledCursor,
                "an advance that won its compare-and-swap ran BEFORE the reschedule, so the reschedule's " +
                "cursor is the one left behind; one that lost wrote nothing at all");
            row.CurrentRunCount.ShouldBe(outcomes[i] == ScheduleCasResult.Applied ? 1 : 0,
                "the run counter must move if and only if the compare-and-swap reported Applied");
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
