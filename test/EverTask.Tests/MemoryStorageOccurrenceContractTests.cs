using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// The in-memory store is the fifth implementation of the durable-occurrence contract, and the reference the
/// other four are read against — its relational siblings are pinned by <c>EfCoreTaskStorageTestsBase</c>,
/// case for case. Three families live here. The one that writes: an occurrence appears and the cursor moves in
/// the same step, a slot is materialized at most once, and the last slot ends the series without a second
/// write. The one that must not: a caller that lost a race re-reads the schedule and retries with the cursor
/// it read back, which is <c>null</c> once the series has ended, or it cancels a schedule a concurrent
/// <c>Remove</c> has already deleted. And the one the relational providers get from the schema for free: the
/// check constraint, the self-referencing foreign key and the unique index on (parent, slot), which this store
/// has to enforce by hand — a row it accepted and they refuse would make the same test pass here and fail there.
/// </summary>
public class MemoryStorageOccurrenceContractTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private static readonly DateTimeOffset Cursor = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    private async Task<Guid> SeedScheduleAsync()
    {
        var id = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id           = id,
            Type         = "DurableSchedule",
            Request      = "{}",
            Handler      = "DurableHandler",
            CreatedAtUtc = Cursor.AddMinutes(-10),
            Status       = QueuedTaskStatus.Queued,
            IsRecurring  = true,
            NextRunUtc   = Cursor
        });
        return id;
    }

    private static QueuedTask NewOccurrence(Guid parentId) => new()
    {
        Id                    = TestGuidGenerator.New(),
        Type                  = "DurableSchedule",
        Request               = "{}",
        Handler               = "DurableHandler",
        CreatedAtUtc          = Cursor,
        ScheduledExecutionUtc = Cursor,
        Status                = QueuedTaskStatus.WaitingQueue,
        ParentTaskId          = parentId
    };

    private async Task<QueuedTask> ReloadAsync(Guid id) => (await _storage.Get(t => t.Id == id))[0];

    [Fact]
    public async Task Should_insert_the_occurrence_and_advance_the_cursor_together()
    {
        var id       = await SeedScheduleAsync();
        var nextSlot = Cursor.AddMinutes(5);

        var occurrence = NewOccurrence(id);
        var outcome    = await _storage.MaterializeOccurrence(id, 0, Cursor, occurrence, nextSlot, AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);

        var child = (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem();
        child.ParentTaskId.ShouldBe(id);
        child.ScheduledExecutionUtc.ShouldBe(Cursor);
        child.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        var parent = await ReloadAsync(id);
        parent.NextRunUtc.ShouldBe(nextSlot);
        parent.CurrentRunCount.ShouldBe(1, "a materialization IS the run of a durable series");
        parent.Status.ShouldBe(QueuedTaskStatus.Queued, "the series continues");
    }

    [Fact]
    public async Task Should_report_AlreadyExists_and_leave_the_cursor_untouched()
    {
        var id = await SeedScheduleAsync();
        await _storage.MaterializeOccurrence(id, 0, Cursor, NewOccurrence(id), Cursor.AddMinutes(5),
            AuditLevel.Full);

        // Rewind the cursor by hand so the compare-and-swap passes and only the duplicate slot can refuse —
        // the same setup the relational providers use, where the refusal comes from the unique index instead.
        var rewind = await ReloadAsync(id);
        rewind.NextRunUtc = Cursor;
        await _storage.UpdateTask(rewind);

        var duplicate = NewOccurrence(id);
        var outcome = await _storage.MaterializeOccurrence(id, 0, Cursor, duplicate, Cursor.AddMinutes(5),
            AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.AlreadyExists);
        (await _storage.Get(t => t.Id == duplicate.Id)).ShouldBeEmpty();

        var parent = await ReloadAsync(id);
        parent.NextRunUtc.ShouldBe(Cursor, "a refused materialization must not advance the cursor");
        parent.CurrentRunCount.ShouldBe(1, "and must not consume a run either");
    }

    [Fact]
    public async Task Should_finalize_the_series_in_the_same_step_as_its_last_slot()
    {
        var id = await SeedScheduleAsync();

        var occurrence = NewOccurrence(id);
        var outcome = await _storage.MaterializeOccurrence(id, 0, Cursor, occurrence, newCursorUtc: null,
            AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem(
            "the last slot is materialized like any other");

        var parent = await ReloadAsync(id);
        parent.Status.ShouldBe(QueuedTaskStatus.Completed);
        parent.NextRunUtc.ShouldBeNull("a finished series must stop matching the recovery filter");
        parent.CurrentRunCount.ShouldBe(1);
        parent.StatusAudits.Count.ShouldBe(1, "the finalization is one transition, so it audits once");
        parent.LastExecutionUtc.ShouldNotBeNull(
            "the finalization stamps the row even though no handler ran — it is what the retention window " +
            "is then measured from");
    }

    [Fact]
    public async Task Should_report_ParentInactive_when_the_series_has_already_finished()
    {
        var id = await SeedScheduleAsync();
        (await _storage.TrySetRecurringSeriesCompleted(id, Cursor, QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full))
            .ShouldBeTrue();

        var occurrence = NewOccurrence(id);
        var outcome = await _storage.MaterializeOccurrence(id, 0, expectedCursorUtc: null, occurrence,
            newCursorUtc: Cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.ParentInactive);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();

        var parent = await ReloadAsync(id);
        parent.Status.ShouldBe(QueuedTaskStatus.Completed);
        parent.NextRunUtc.ShouldBeNull("a finished series must never get its cursor back");
        (parent.CurrentRunCount ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task Should_report_CursorMoved_when_a_null_cursor_is_expected_on_a_live_series()
    {
        var id = await SeedScheduleAsync();

        var occurrence = NewOccurrence(id);
        var outcome = await _storage.MaterializeOccurrence(id, 0, expectedCursorUtc: null, occurrence,
            Cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.CursorMoved);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();

        var parent = await ReloadAsync(id);
        parent.NextRunUtc.ShouldBe(Cursor);
        (parent.CurrentRunCount ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task Should_cancel_every_occurrence_recovery_would_put_back_in_a_queue()
    {
        // R7: the cancelled set is the exact complement of the requeued one. ServiceStopped is recoverable, so
        // an occurrence left in it would come back at the next restart and run for a cancelled schedule.
        var id = await SeedScheduleAsync();

        var waiting = NewOccurrence(id);
        await _storage.Persist(waiting);

        var stopped = NewOccurrence(id);
        stopped.ScheduledExecutionUtc = Cursor.AddMinutes(1);
        stopped.Status                = QueuedTaskStatus.ServiceStopped;
        await _storage.Persist(stopped);

        var running = NewOccurrence(id);
        running.ScheduledExecutionUtc = Cursor.AddMinutes(2);
        running.Status                = QueuedTaskStatus.InProgress;
        await _storage.Persist(running);

        await _storage.CancelSchedule(id, AuditLevel.Full);

        (await ReloadAsync(waiting.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await ReloadAsync(stopped.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await ReloadAsync(running.Id)).Status
            .ShouldBe(QueuedTaskStatus.InProgress, "an occurrence already executing owns a live delivery");

        (await _storage.TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, stopped.Id, AuditLevel.Full))
            .ShouldBeFalse("and recovery must then refuse to put it back in a queue");
    }

    [Fact]
    public async Task Should_write_nothing_when_cancelling_a_schedule_that_is_already_gone()
    {
        var id = await SeedScheduleAsync();
        await _storage.Remove(id);

        await Should.NotThrowAsync(() => _storage.CancelSchedule(id, AuditLevel.Full));

        (await _storage.Get(t => t.Id == id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_reject_an_occurrence_that_does_not_name_its_slot()
    {
        // Check constraint CK_QueuedTasks_OccurrenceSlot. A slotless occurrence has no nominal slot to be
        // deduplicated on, so the unique index below could never refuse its twin and the schedule would
        // materialize the same moment twice.
        var id = await SeedScheduleAsync();

        var slotless = NewOccurrence(id);
        slotless.ScheduledExecutionUtc = null;

        var error = await Should.ThrowAsync<InvalidOperationException>(() => _storage.Persist(slotless));
        error.Message.ShouldContain("CK_QueuedTasks_OccurrenceSlot");

        (await _storage.Get(t => t.Id == slotless.Id)).ShouldBeEmpty("a refused row must not be stored");
    }

    [Fact]
    public async Task Should_reject_an_occurrence_whose_schedule_no_longer_exists()
    {
        // The self-referencing foreign key (FK_QueuedTasks_QueuedTasks_ParentTaskId in the relational schema,
        // which this store's message shortens to FK_QueuedTasks_Parent). The providers restrict it precisely
        // so a Remove of the schedule racing a materialization cannot leave orphans: rows nothing advances,
        // nothing cancels and nothing prunes, since every occurrence query starts from a parent id.
        var id = await SeedScheduleAsync();
        await _storage.Remove(id);

        var orphan = NewOccurrence(id);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => _storage.Persist(orphan));
        error.Message.ShouldContain("FK_QueuedTasks_Parent");

        (await _storage.Get(t => t.Id == orphan.Id)).ShouldBeEmpty("a refused row must not be stored");
    }

    [Fact]
    public async Task Should_reject_a_second_row_for_the_same_slot()
    {
        // Unique index UX_QueuedTasks_Occurrence — the guarantee the whole at-least-once story rests on: two
        // rows for one slot are two deliveries with distinct PersistenceIds, which the delivery registry
        // cannot dedup.
        var id = await SeedScheduleAsync();
        await _storage.Persist(NewOccurrence(id));

        var duplicate = NewOccurrence(id);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => _storage.Persist(duplicate));
        error.Message.ShouldContain("UX_QueuedTasks_Occurrence");

        (await _storage.GetOccurrences(id)).ShouldHaveSingleItem()
            .Id.ShouldNotBe(duplicate.Id, "the slot keeps the occurrence that won it");
    }

    [Fact]
    public async Task Should_store_the_canonical_occurrence_shape_whatever_the_caller_hands_over()
    {
        // The three optimized providers hardcode the shape in their INSERT column list; the stores that keep
        // the caller's entity have to stamp it, or the same call would persist a different row per backend.
        var id       = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = "DurableSchedule",
            Request         = "{}",
            Handler         = "DurableHandler",
            CreatedAtUtc    = Cursor.AddMinutes(-10),
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            NextRunUtc      = Cursor,
            ScheduleVersion = 5
        });

        var occurrence = NewOccurrence(id);
        occurrence.Status          = QueuedTaskStatus.Queued;
        occurrence.CurrentRunCount = 7;
        occurrence.IsRecurring     = true;
        occurrence.TaskKey         = "a-key-that-belongs-to-the-schedule";
        occurrence.RecurringTask   = "{\"copied\":true}";
        occurrence.MaxRuns         = 3;
        occurrence.RunUntil        = Cursor.AddDays(1);
        occurrence.NextRunUtc      = Cursor.AddMinutes(5);
        occurrence.QueueName       = "recurring";

        var outcome = await _storage.MaterializeOccurrence(id, 5, Cursor, occurrence, Cursor.AddMinutes(5),
            AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);

        var child = await ReloadAsync(occurrence.Id);
        child.ScheduleVersion.ShouldBe(5, "the occurrence belongs to the version it was materialized against");
        child.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        (child.CurrentRunCount ?? 0).ShouldBe(0);
        child.IsRecurring.ShouldBeFalse();
        child.TaskKey.ShouldBeNull();
        child.RecurringTask.ShouldBeNull();
        child.MaxRuns.ShouldBeNull();
        child.RunUntil.ShouldBeNull();
        child.NextRunUtc.ShouldBeNull("a cursor on a child would make it look like a series to recovery");

        child.ParentTaskId.ShouldBe(id);
        child.ScheduledExecutionUtc.ShouldBe(Cursor);
        child.QueueName.ShouldBe("recurring");
    }
}
