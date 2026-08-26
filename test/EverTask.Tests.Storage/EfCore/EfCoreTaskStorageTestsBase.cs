using EverTask.Abstractions;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

public abstract class EfCoreTaskStorageTestsBase
{
    private readonly ITaskStorage _storage;
    private readonly ITaskStoreDbContext _mockedDbContext;

    protected abstract ITaskStoreDbContext CreateDbContext();
    protected abstract ITaskStorage GetStorage();

    public EfCoreTaskStorageTestsBase()
    {
        Initialize();
        _storage         = GetStorage();
        _mockedDbContext = CreateDbContext();
    }

    public List<QueuedTask> QueuedTasks =>
    [
        new QueuedTask
        {
            Id                    = GetGuidForProvider(),
            CreatedAtUtc          = DateTimeOffset.UtcNow,
            LastExecutionUtc      = DateTimeOffset.UtcNow,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddDays(1),
            Type                  = "Type1",
            Request               = "Request1",
            Handler               = "Handler1",
            Status                = QueuedTaskStatus.InProgress,
            /*StatusAudits = new List<StatusAudit>()
            {
                new StatusAudit
                {
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    NewStatus    = QueuedTaskStatus.Queued
                },
                new StatusAudit
                {
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    NewStatus    = QueuedTaskStatus.InProgress
                }
            }*/
        },

        new QueuedTask
        {
            Id                    = GetGuidForProvider(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(1),
            LastExecutionUtc      = DateTimeOffset.UtcNow.AddMinutes(1),
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1),
            Type                  = "Type2",
            Request               = "Request2",
            Handler               = "Handler2",
            Status                = QueuedTaskStatus.Queued,
            /*StatusAudits = new List<StatusAudit>()
            {
                new StatusAudit
                {
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    NewStatus    = QueuedTaskStatus.Queued
                }
            }*/
        }
    ];


    protected virtual void Initialize()
    {
    }

    /// <summary>
    /// Generates a GUID optimized for the specific database provider being tested.
    /// Override in provider-specific tests to use database-optimized GUID generation.
    /// </summary>
    protected virtual Guid GetGuidForProvider() => TestGuidGenerator.New();

    /// <summary>
    /// Floors a DateTimeOffset to whole microseconds (R13). The exact <c>NextRunUtc.ShouldBe(nextRun)</c>
    /// assertions compare at .NET tick precision (100 ns), but Postgres <c>timestamptz</c> stores microseconds
    /// (1 µs = 10 ticks). Flooring the INPUT before persisting keeps the round-trip exact on EVERY provider
    /// (SQL Server / SQLite round-trip ticks unchanged, so this neither weakens nor changes their behavior),
    /// without loosening the equality assertions.
    /// </summary>
    protected static DateTimeOffset FloorToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    [Fact]
    public async Task Get_ReturnsExpectedTasks()
    {
        var queued = QueuedTasks;
        _mockedDbContext.QueuedTasks.AddRange(queued);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        var result = await _storage.Get(x => x.Id == queued.FirstOrDefault()!.Id);

        result.Length.ShouldBe(1);
        result[0].Type.ShouldBe("Type1");
        result[0].Request.ShouldBe("Request1");
    }

    [Fact]
    public async Task GetAll_ReturnsAllTasks()
    {
        var queued = QueuedTasks;
        _mockedDbContext.QueuedTasks.AddRange(queued);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);
        var result = await _storage.GetAll();
        Assert.Equal(QueuedTasks.Count, result.Count(x => queued.Any(y => y.Id == x.Id)));
    }

    [Fact]
    public async Task PersistTask_ShouldPersistTask()
    {
        var queued = QueuedTasks[1];
        await _storage.Persist(queued);
        _mockedDbContext.QueuedTasks.Count(x => x.Id == queued.Id).ShouldBe(1);

        var result = await _storage.Get(x => x.Id == queued.Id);

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type2");
    }

    [Fact]
    public async Task Should_RetrivePendingTasks()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        await _storage.RetrievePending(null, null, 100, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == queued.Id).ShouldBe(1);

        var result = await _storage.Get(x => x.Id == queued.Id);

        result.ShouldNotBeNull();
        result[0].Type.ShouldBe("Type1");
    }

    [Fact]
    public async Task Should_SetTaskQueued()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetQueued(result[0].Id, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        //using ToList because sqlite doesnt support order by DateTimeOffset
        _mockedDbContext.StatusAudit.ToList().OrderBy(x => x.UpdatedAtUtc)
                        .LastOrDefault(x => x.QueuedTaskId == result[0].Id)?.NewStatus
                        .ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_transition_only_recoverable_tasks()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = (await _storage.Get(x => x.Id == queued.Id))[0].Id;

        // Recoverable status: the conditional transition succeeds
        (await _storage.TrySetQueuedIfRecoverable(taskId, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(x => x.Id == taskId))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        // Terminal status (non-recurring Completed): the transition is REFUSED and the status
        // stays untouched — the startup recovery must never resurrect a finished task
        await _storage.SetCompleted(taskId, 10.0, AuditLevel.Full);
        (await _storage.TrySetQueuedIfRecoverable(taskId, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == taskId))[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_apply_RunUntil_and_MaxRuns_guards()
    {
        // The conditional transition must apply the SAME MaxRuns/RunUntil guards as RetrievePending
        // (canonical QueuedTask.IsRecoverable), on every provider: the atomic UPDATE on SQL Server,
        // the client-side override on SQLite, the client-side fallback on EF Core InMemory.
        var now = DateTimeOffset.UtcNow;

        // Recurring task between runs but past its RunUntil: must NOT be resurrected
        var pastRunUntil = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            CreatedAtUtc = now,
            Type         = "RecPastRunUntil", Request = "{}", Handler = "H",
            Status       = QueuedTaskStatus.Completed,
            IsRecurring  = true,
            NextRunUtc   = now.AddMinutes(5),
            RunUntil     = now.AddMinutes(-5)
        };
        await _storage.Persist(pastRunUntil);
        (await _storage.TrySetQueuedIfRecoverable(pastRunUntil.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == pastRunUntil.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Recurring task that exhausted MaxRuns: must NOT be resurrected
        var exhausted = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecMaxRuns", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            MaxRuns         = 3,
            CurrentRunCount = 4
        };
        await _storage.Persist(exhausted);
        (await _storage.TrySetQueuedIfRecoverable(exhausted.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(x => x.Id == exhausted.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Recurring task within both bounds: recoverable
        var recoverable = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecOk", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            RunUntil        = now.AddMinutes(10),
            MaxRuns         = 10,
            CurrentRunCount = 1
        };
        await _storage.Persist(recoverable);
        (await _storage.TrySetQueuedIfRecoverable(recoverable.Id, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(x => x.Id == recoverable.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_SetTaskInProgress()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);
        result.ShouldNotBeEmpty();

        var taskId = result[0].Id;
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        await _storage.SetInProgress(taskId, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.InProgress);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == taskId)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.InProgress);
    }

    [Fact]
    public async Task Should_SetTaskCompleted()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result         = await _storage.Get(x => x.Id == queued.Id);
        var startingLength = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id);

        await _storage.SetCompleted(result[0].Id, 100.0, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == result[0].Id).ShouldBe(startingLength + 1);
        _mockedDbContext.StatusAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == result[0].Id)
                        ?.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_PersistExecutionTimeMs_When_SetCompleted()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);
        result.Length.ShouldBeGreaterThan(0, "Task should exist after persist");
        const double expectedExecutionTime = 123.45;

        // Act
        await _storage.SetCompleted(result[0].Id, expectedExecutionTime, AuditLevel.Full);

        // Assert
        result = await _storage.Get(x => x.Id == queued.Id);
        result.Length.ShouldBeGreaterThan(0, "Task should still exist after SetCompleted");
        result[0].ExecutionTimeMs.ShouldBe(expectedExecutionTime, "ExecutionTimeMs should be persisted when setting task as completed");
    }

    [Fact]
    public async Task Should_PersistExecutionTimeMs_When_UpdateCurrentRun()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "RecurringTask",
            Request = "{}",
            Handler = "RecurringHandler",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsRecurring = true,
            CurrentRunCount = 0
        };
        await _storage.Persist(task);
        const double expectedExecutionTime = 234.56;
        var nextRun = DateTimeOffset.UtcNow.AddMinutes(5);

        // Act
        await _storage.UpdateCurrentRun(taskId, expectedExecutionTime, nextRun, AuditLevel.Full);

        // Assert
        var result = await _storage.Get(x => x.Id == taskId);
        result.Length.ShouldBeGreaterThan(0, "Task should exist after UpdateCurrentRun");
        result[0].ExecutionTimeMs.ShouldBe(expectedExecutionTime, "ExecutionTimeMs should be persisted when updating current run");

        // Verify RunsAudit also has ExecutionTimeMs
        var runsAudit = _mockedDbContext.RunsAudit.OrderBy(x => x.Id).LastOrDefault(x => x.QueuedTaskId == taskId);
        runsAudit.ShouldNotBeNull("RunsAudit should be created");
        runsAudit.ExecutionTimeMs.ShouldBe(expectedExecutionTime, "RunsAudit should have ExecutionTimeMs");
    }

    [Fact]
    public async Task Should_SetTaskCancelledByUser()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);

        await _storage.SetCancelledByUser(result[0].Id, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_SetTaskCancelledByService()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var result = await _storage.Get(x => x.Id == queued.Id);

        var exception = new Exception("Test Exception");
        await _storage.SetCancelledByService(result[0].Id, exception, AuditLevel.Full);

        result = await _storage.Get(x => x.Id == queued.Id);
        result[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);
        result[0].Exception!.ShouldContain("Test Exception");
    }

    [Fact]
    public async Task GetCurrentRunCount_Should_ReturnCorrectCount()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        var count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(0); // Inizialmente zero

        await _storage.UpdateCurrentRun(taskId, 100.0, null, AuditLevel.Full); // Aggiorna la corsa
        count = await _storage.GetCurrentRunCount(taskId);
        count.ShouldBe(1); // Dovrebbe essere incrementato
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_UpdateRunCountAndNextRun()
    {
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(1)); // R13: µs floor for exact round-trip

        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_advance_counter_by_one_per_real_execution()
    {
        // Option B accounting (f7-f8 tech-debt paydown): each call advances CurrentRunCount by exactly
        // one real execution. Occurrences skipped to realign the schedule after a downtime never inflate
        // the counter, so CurrentRunCount tracks real executions only. Audited tracked path here, fast
        // ExecuteUpdate path below.
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(1)); // R13: µs floor for exact round-trip

        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(3);
        task[0].NextRunUtc.ShouldBe(nextRun);
    }

    [Fact]
    public async Task UpdateCurrentRun_Should_advance_counter_by_one_on_none_fastpath()
    {
        // Option B accounting on the AuditLevel.None server-side fast path (ExecuteUpdate, no SELECT):
        // exactly +1 per call.
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        await _storage.UpdateCurrentRun(taskId, 100.0, null, AuditLevel.None);
        await _storage.UpdateCurrentRun(taskId, 100.0, null, AuditLevel.None);

        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(2);
    }

    [Fact]
    public async Task SetRecurringSeriesCompleted_should_complete_and_clear_NextRunUtc_without_advancing_counter()
    {
        // Fix for bug A: a recurring series that ENDS on a skipped occurrence (next slot past RunUntil) must
        // be finalized so recovery cannot resurrect it — Completed AND NextRunUtc cleared — WITHOUT counting
        // the skip: no CurrentRunCount advance and no RunsAudit row (the occurrence never executed, Option B).
        var now  = DateTimeOffset.UtcNow;
        var task = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecSkipEnd", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            RunUntil        = now.AddMinutes(10),   // >= now: a Completed row with NextRunUtc would be recoverable
            MaxRuns         = 10,
            CurrentRunCount = 2
        };
        await _storage.Persist(task);

        await _storage.SetRecurringSeriesCompleted(task.Id, 123.0, AuditLevel.Full);

        var row = (await _storage.Get(x => x.Id == task.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed, "the ended series is terminal");
        row.NextRunUtc.ShouldBeNull("NextRunUtc must be cleared so recovery cannot resurrect the ended series");
        row.CurrentRunCount.ShouldBe(2, "a skipped occurrence must NOT advance the run counter (Option B)");

        _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == task.Id)
            .ShouldBe(0, "a skipped occurrence never executed: no runs-audit row is written");

        // The finalized row must no longer be recoverable (NextRunUtc cleared).
        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full))
            .ShouldBeFalse("a finalized (NextRunUtc-cleared) series must not be revived by recovery");
    }

    [Fact]
    public async Task Finalizing_a_series_should_stamp_LastExecutionUtc_although_nothing_ran()
    {
        // X3, and documented in docs/storage/custom-storage.md: finalizing is a terminal transition, so it
        // writes LastExecutionUtc even though the remaining slots never executed. That column is what the
        // completed-task retention measures from, so the window of a series whose boundary elapsed during a
        // downtime restarts at the restart that closed it — an accepted consequence, and one no provider may
        // quietly opt out of. Both terminal writes are held to it: the unconditional one, kept by a store
        // without SupportsScheduleVersioning, and the compare-and-swap the recovery prefers.
        var lastRealRun = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-3));
        var cursor      = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));

        async Task<Guid> SeedAsync()
        {
            var row = new QueuedTask
            {
                Id               = GetGuidForProvider(),
                Type             = "RecFinalizeStamp", Request = "{}", Handler = "H",
                CreatedAtUtc     = lastRealRun,
                Status           = QueuedTaskStatus.Queued,
                IsRecurring      = true,
                NextRunUtc       = cursor,
                CurrentRunCount  = 4,
                LastExecutionUtc = lastRealRun
            };
            await _storage.Persist(row);
            return row.Id;
        }

        var unconditional = await SeedAsync();
        var conditional   = await SeedAsync();

        var before = FloorToMicroseconds(DateTimeOffset.UtcNow);

        await _storage.SetRecurringSeriesCompleted(unconditional, 0, AuditLevel.Full);
        (await _storage.TrySetRecurringSeriesCompleted(conditional, cursor, QueuedTaskStatus.Queued, 0, 0,
            AuditLevel.Full)).ShouldBeTrue();

        var after = DateTimeOffset.UtcNow;

        foreach (var id in new[] { unconditional, conditional })
        {
            var row = (await _storage.Get(t => t.Id == id))[0];

            row.Status.ShouldBe(QueuedTaskStatus.Completed);
            row.NextRunUtc.ShouldBeNull();
            row.CurrentRunCount.ShouldBe(4, "the slots left behind were never executed (Option B)");
            _mockedDbContext.RunsAudit.Count(a => a.QueuedTaskId == id).ShouldBe(0, "no run, no runs audit");

            row.LastExecutionUtc.ShouldNotBeNull().ShouldBeInRange(before, after,
                "the finalization instant replaces the last real run's, three days older");
        }
    }

    [Fact]
    public async Task SetRecurringTaskPoisoned_should_mark_Failed_and_clear_NextRunUtc_so_it_is_not_recoverable()
    {
        // B1/P0-1: a recurring row poisoned during recovery must be TERMINAL — Failed AND NextRunUtc cleared
        // in one atomic write — so it stops satisfying IsRecoverable and is never resurrected/re-poisoned at
        // every restart. A plain SetStatus(Failed) would leave NextRunUtc set and the row recoverable.
        var now  = DateTimeOffset.UtcNow;
        var task = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            CreatedAtUtc    = now,
            Type            = "RecPoison", Request = "{}", Handler = "H",
            Status          = QueuedTaskStatus.Completed, // recurring row between runs
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(5),
            CurrentRunCount = 2
        };
        await _storage.Persist(task);

        await _storage.SetRecurringTaskPoisoned(task.Id, new InvalidOperationException("corrupt metadata"),
            AuditLevel.Full);

        var row = (await _storage.Get(x => x.Id == task.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Failed, "a poisoned recurring row is terminal");
        row.NextRunUtc.ShouldBeNull("NextRunUtc must be cleared so recovery cannot resurrect the poisoned series");
        row.Exception.ShouldNotBeNull("the poison reason is recorded");
        row.IsRecoverable(DateTimeOffset.UtcNow)
            .ShouldBeFalse("a poisoned (NextRunUtc-cleared) recurring row must not be recoverable (P0-1)");

        // And the atomic conditional re-queue must refuse it.
        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full))
            .ShouldBeFalse("a poisoned recurring row must not be revived by recovery");
    }

    [Fact]
    public async Task IncrementRecoveryFailure_should_count_and_ClearRecoveryFailure_should_reset()
    {
        // L18: persistent recovery re-dispatch failures are counted durably so the caller can poison
        // the task after a limit; a successful re-dispatch clears the counter.
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId = queued.Id;

        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBeNull();

        (await _storage.IncrementRecoveryFailure(taskId)).ShouldBe(1);
        (await _storage.IncrementRecoveryFailure(taskId)).ShouldBe(2);
        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBe(2);

        await _storage.ClearRecoveryFailure(taskId);
        (await _storage.Get(x => x.Id == taskId))[0].RecoveryDispatchFailureCount.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteRecurringRun_Should_set_completed_and_advance_atomically()
    {
        // CU14/L29: a recurring occurrence's Completed status AND the run-counter / next-run advance must
        // be written in a single atomic operation (one transaction per provider).
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);
        var taskId  = queued.Id;
        var nextRun = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(30)); // R13: µs floor for exact round-trip

        await _storage.CompleteRecurringRun(taskId, 75.0, nextRun, AuditLevel.Full);

        var task = (await _storage.Get(x => x.Id == taskId))[0];
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.CurrentRunCount.ShouldBe(1); // one real execution (Option B)
        task.NextRunUtc.ShouldBe(nextRun);
        task.ExecutionTimeMs.ShouldBe(75.0);
    }

    [Fact]
    public async Task CurrentRunCount_Should_equal_RunsAudit_rows_across_real_executions()
    {
        // Option B headline invariant (f7-f8 tech-debt paydown): CurrentRunCount tracks REAL executions
        // only, so it always equals the number of RunsAudit rows — no skip-inflated divergence. Audited
        // runs here (AuditLevel.Full writes one RunsAudit per advance).
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var nextRun = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 4; i++)
            await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        var task      = (await _storage.Get(x => x.Id == taskId))[0];
        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);

        task.CurrentRunCount.ShouldBe(4);
        runsCount.ShouldBe(4);
        task.CurrentRunCount!.Value.ShouldBe(runsCount, "CurrentRunCount must equal the RunsAudit row count");
    }

    [Theory]
    [InlineData(AuditLevel.Full,       1, 1)] // StatusAudit only at Full; RunsAudit at Full
    [InlineData(AuditLevel.Minimal,    0, 1)] // Minimal: no StatusAudit, but RunsAudit tracks the run
    [InlineData(AuditLevel.ErrorsOnly, 0, 0)] // a successful (Completed, no-exception) run is never an error
    [InlineData(AuditLevel.None,       0, 0)]
    public async Task CompleteRecurringRun_creates_correct_audit_rows_per_level(
        AuditLevel level, int expectedStatusAudits, int expectedRunsAudits)
    {
        // The (Completed, null-exception) transition has DIFFERENT audit thresholds per table:
        // StatusAudit only at Full, RunsAudit at Full+Minimal (AuditPolicy). A single combined audit flag
        // in the SQL Server proc would write a spurious StatusAudit at Minimal -> the Minimal row bites it.
        // The run counter ALWAYS advances +1, regardless of the audit level (Option B).
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "T",
            Request         = "{}",
            Handler         = "H",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startStatus = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        var startRuns   = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);

        await _storage.CompleteRecurringRun(taskId, 50.0, DateTimeOffset.UtcNow.AddHours(1), level);

        var task = (await _storage.Get(x => x.Id == taskId))[0];
        task.CurrentRunCount.ShouldBe(1, "the counter always advances +1, never gated on the audit level");
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        (_mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId) - startStatus)
            .ShouldBe(expectedStatusAudits, "StatusAudit threshold (Full only) must match the EF baseline");
        (_mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId) - startRuns)
            .ShouldBe(expectedRunsAudits, "RunsAudit threshold (Full+Minimal) must match the EF baseline");
    }

    [Fact]
    public async Task CompleteRecurringRun_with_null_nextRun_makes_row_terminal_and_unrecoverable()
    {
        // P0: the last successful occurrence of a bounded series passes nextRun = null. The proc must assign
        // NextRunUtc UNCONDITIONALLY (never COALESCE), otherwise a future stale value survives and recovery
        // resurrects a finished series -> double execution. RunUntil is future on purpose so that, WITH a
        // COALESCE trap, the row would stay recoverable and RetrievePending would (wrongly) include it.
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "T",
            Request         = "{}",
            Handler         = "H",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.InProgress,
            IsRecurring     = true,
            CurrentRunCount = 0,
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(5),  // stale value that must NOT be preserved
            RunUntil        = DateTimeOffset.UtcNow.AddMinutes(30)  // future: with COALESCE the row stays recoverable
        });

        await _storage.CompleteRecurringRun(taskId, 10.0, nextRun: null, AuditLevel.Full);

        var row = (await _storage.Get(x => x.Id == taskId))[0];
        row.NextRunUtc.ShouldBeNull("NextRunUtc must be cleared, never preserved with COALESCE");

        var pending = await _storage.RetrievePending(null, null, 100);
        pending.ShouldNotContain(t => t.Id == taskId, "a terminated series must never be revived by recovery");
    }

    [Fact]
    public async Task SaveExecutionLogsAsync_Should_PersistLogs()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Information",
                Message        = "Log 1",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Warning",
                Message        = "Log 2",
                SequenceNumber = 1
            }
        };

        // Act
        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Assert
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);
        savedLogs.Count.ShouldBe(2);
        savedLogs[0].Message.ShouldBe("Log 1");
        savedLogs[0].Level.ShouldBe("Information");
        savedLogs[1].Message.ShouldBe("Log 2");
        savedLogs[1].Level.ShouldBe("Warning");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_Should_ReturnLogsOrderedBySequenceNumber()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow,
                Level          = "Information",
                Message        = "First log",
                SequenceNumber = 0
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(1),
                Level          = "Warning",
                Message        = "Second log",
                SequenceNumber = 1
            },
            new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(2),
                Level          = "Error",
                Message        = "Third log",
                SequenceNumber = 2
            }
        };

        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Act
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);

        // Assert
        savedLogs.Count.ShouldBe(3);
        savedLogs[0].SequenceNumber.ShouldBe(0);
        savedLogs[0].Message.ShouldBe("First log");
        savedLogs[1].SequenceNumber.ShouldBe(1);
        savedLogs[1].Message.ShouldBe("Second log");
        savedLogs[2].SequenceNumber.ShouldBe(2);
        savedLogs[2].Message.ShouldBe("Third log");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_WithPagination_Should_ReturnCorrectPage()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var logs = new List<TaskExecutionLog>();
        for (var i = 0; i < 10; i++)
        {
            logs.Add(new TaskExecutionLog
            {
                Id             = GetGuidForProvider(),
                TaskId         = queued.Id,
                TimestampUtc   = DateTimeOffset.UtcNow.AddSeconds(i),
                Level          = "Information",
                Message        = $"Log {i}",
                SequenceNumber = i
            });
        }

        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Act - get second page (skip 3, take 3)
        var page = await _storage.GetExecutionLogsAsync(queued.Id, skip: 3, take: 3, CancellationToken.None);

        // Assert
        page.Count.ShouldBe(3);
        page[0].SequenceNumber.ShouldBe(3);
        page[0].Message.ShouldBe("Log 3");
        page[1].SequenceNumber.ShouldBe(4);
        page[1].Message.ShouldBe("Log 4");
        page[2].SequenceNumber.ShouldBe(5);
        page[2].Message.ShouldBe("Log 5");
    }

    [Fact]
    public async Task GetExecutionLogsAsync_ForNonExistentTask_Should_ReturnEmptyList()
    {
        // Arrange
        var nonExistentTaskId = GetGuidForProvider();

        // Act
        var logs = await _storage.GetExecutionLogsAsync(nonExistentTaskId, CancellationToken.None);

        // Assert
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveExecutionLogsAsync_WithExceptionDetails_Should_PersistExceptionInfo()
    {
        // Arrange
        var queued = QueuedTasks[0];
        await _storage.Persist(queued);

        var exception = new InvalidOperationException("Test exception");
        var logs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id               = GetGuidForProvider(),
                TaskId           = queued.Id,
                TimestampUtc     = DateTimeOffset.UtcNow,
                Level            = "Error",
                Message          = "Error occurred",
                ExceptionDetails = exception.ToString(),
                SequenceNumber   = 0
            }
        };

        // Act
        await _storage.SaveExecutionLogsAsync(queued.Id, logs, CancellationToken.None);

        // Assert
        var savedLogs = await _storage.GetExecutionLogsAsync(queued.Id, CancellationToken.None);
        savedLogs.Count.ShouldBe(1);
        savedLogs[0].ExceptionDetails.ShouldNotBeNull();
        savedLogs[0].ExceptionDetails!.ShouldContain("Test exception");
        savedLogs[0].ExceptionDetails!.ShouldContain("InvalidOperationException");
    }

    /// <summary>
    /// SMOKE TEST: Fast basic validation that pagination doesn't duplicate or lose tasks.
    /// Catches obvious bugs in keyset pagination logic.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Should_Page_Without_Duplicates()
    {
        // Arrange - Create 20 tasks with GUID v7 (time-ordered)
        var createdIds = new List<Guid>();
        var start      = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"TestTask{i}",
                Request = "{}",
                Handler = "TestHandler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in two pages of 10
        var page1 = await _storage.RetrievePending(null, null, 10);
        var page2 = await _storage.RetrievePending(page1[^1].CreatedAtUtc, page1[^1].Id, 10);

        // Assert
        page1.Length.ShouldBe(10, "First page should have 10 tasks");
        page2.Length.ShouldBe(10, "Second page should have 10 tasks");

        // CRITICAL: Verify no overlap (duplicate IDs across pages)
        var page1Ids = page1.Select(t => t.Id).ToHashSet();
        var page2Ids = page2.Select(t => t.Id).ToHashSet();
        var overlap  = page1Ids.Intersect(page2Ids).ToList();
        overlap.ShouldBeEmpty($"Pages should not have overlapping IDs. Found duplicates: {string.Join(", ", overlap)}");

        // CRITICAL: Verify no missing tasks
        var allRetrieved = page1.Concat(page2).Select(t => t.Id).ToHashSet();
        allRetrieved.Count.ShouldBe(20, "Should retrieve all 20 unique tasks");

        var missingIds = createdIds.Except(allRetrieved).ToList();
        missingIds.ShouldBeEmpty(
            $"All created tasks should be retrieved. Missing IDs: {string.Join(", ", missingIds)}");
    }

    /// <summary>
    /// EDGE CASE TEST: Detects GUID v7 ordering inconsistencies between database and .NET.
    /// Catches bugs like SQL Server uniqueidentifier sorting differently than Guid.CompareTo().
    /// </summary>
    [Fact]
    public async Task RetrievePending_Database_Ordering_Should_Match_LINQ()
    {
        // Arrange - Create 10 tasks with GUID v7
        var taskIds = new List<Guid>();
        var start   = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            var taskId = GetGuidForProvider();
            taskIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve all in one page (to get database ordering)
        var dbOrdered = await _storage.RetrievePending(null, null, 100);

        // Assert - Database ordering should match LINQ OrderBy (only by CreatedAtUtc, GUID not used as tiebreaker)
        var linqOrdered = dbOrdered.OrderBy(t => t.CreatedAtUtc).ToArray();

        dbOrdered.Length.ShouldBe(linqOrdered.Length, "Should have same number of tasks");

        for (var i = 0; i < dbOrdered.Length; i++)
        {
            dbOrdered[i].Id.ShouldBe(linqOrdered[i].Id,
                $"DB ordering differs from LINQ at index {i}. " +
                $"DB ID: {dbOrdered[i].Id}, LINQ ID: {linqOrdered[i].Id}. " +
                $"This indicates GUID v7 CompareTo() behaves differently in database vs .NET!");
        }
    }

    /// <summary>
    /// STRESS TEST: Validates pagination performance and correctness with large dataset.
    /// Verifies no data loss or duplication across 10 pages.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Should_Handle_Large_Dataset()
    {
        // Arrange - Create 100 tasks
        var createdIds = new List<Guid>();
        var start      = DateTimeOffset.UtcNow;
        for (var i = 0; i < 100; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);

            await _storage.Persist(new QueuedTask
            {
                Id      = taskId,
                Type    = $"Task{i}",
                Request = "{}",
                Handler = "Handler",
                // Unique timestamps: avoids GUID tiebreaker issues with SQL Server
                // (SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo)
                CreatedAtUtc = start.AddMilliseconds(i),
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - Retrieve in pages of 10
        var             allPages      = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid?           lastId        = null;

        for (var pageNum = 0; pageNum < 10; pageNum++)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            page.Length.ShouldBe(10, $"Page {pageNum + 1} should have 10 tasks");

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId        = page[^1].Id;
        }

        // Verify no more pages
        var emptyPage = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
        emptyPage.ShouldBeEmpty("Should have no tasks after last page");

        // Assert - All tasks retrieved, no duplicates
        allPages.Count.ShouldBe(100, "Should retrieve all 100 tasks");

        var retrievedIds = allPages.Select(t => t.Id).ToList();
        var uniqueIds    = retrievedIds.ToHashSet();
        uniqueIds.Count.ShouldBe(100,
            $"Should have 100 unique tasks. Found {retrievedIds.Count - uniqueIds.Count} duplicates");

        var missingIds = createdIds.Except(uniqueIds).ToList();
        missingIds.ShouldBeEmpty($"All created tasks should be retrieved. Missing: {missingIds.Count} tasks");
    }

    /// <summary>
    /// GOLD STANDARD TEST: Comprehensive keyset pagination correctness validation.
    /// This is the most thorough test, verifying ALL correctness properties:
    /// - Completeness (all tasks retrieved)
    /// - Uniqueness (no duplicates)
    /// - Monotonic ordering (CreatedAtUtc always increasing)
    /// - Chronological correctness (tasks in creation order)
    /// Uses unique timestamps to eliminate ordering ambiguity from secondary sort (Id).
    /// </summary>
    [Fact]
    public async Task RetrievePending_Keyset_Pagination_Should_Be_Perfect()
    {
        // Arrange - Create 50 tasks with UNIQUE CreatedAtUtc timestamps
        var createdTasks = new List<QueuedTask>();
        var start        = DateTimeOffset.UtcNow;

        for (var i = 0; i < 50; i++)
        {
            var task = new QueuedTask
            {
                Id      = GetGuidForProvider(),
                Type    = $"Task{i:D3}",
                Request = "{}",
                Handler = "Handler",
                // Wide spacing (100ms): ensures unique timestamps, avoids GUID tiebreaker
                // SQL Server uniqueidentifier sorting differs from .NET Guid.CompareTo(),
                // so tests must not rely on GUID ordering for correctness verification
                CreatedAtUtc = start.AddMilliseconds(i * 100),
                Status       = QueuedTaskStatus.Pending
            };
            createdTasks.Add(task);
            await _storage.Persist(task);
        }

        // Act - Retrieve all tasks via keyset pagination (pages of 10)
        var             allPages      = new List<QueuedTask>();
        DateTimeOffset? lastCreatedAt = null;
        Guid?           lastId        = null;

        while (true)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 10);
            if (page.Length == 0)
                break;

            allPages.AddRange(page);
            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId        = page[^1].Id;
        }

        // Assert 1: Completeness - all 50 tasks retrieved
        allPages.Count.ShouldBe(50, "Should retrieve all 50 tasks via pagination");

        // Assert 2: Uniqueness - no duplicate tasks
        var uniqueIds = allPages.Select(t => t.Id).ToHashSet();
        uniqueIds.Count.ShouldBe(50,
            $"Should have no duplicate tasks. Found {allPages.Count - uniqueIds.Count} duplicates");

        // Assert 3: Correctness - no missing tasks
        var missingIds = createdTasks.Select(t => t.Id).Except(uniqueIds).ToList();
        missingIds.ShouldBeEmpty($"All created tasks should be retrieved. Missing: {string.Join(", ", missingIds)}");

        // Assert 4: No extra tasks
        var extraIds = uniqueIds.Except(createdTasks.Select(t => t.Id)).ToList();
        extraIds.ShouldBeEmpty($"Should not retrieve non-existent tasks. Extra: {string.Join(", ", extraIds)}");

        // Assert 5: CRITICAL - Monotonic ordering by CreatedAtUtc
        for (var i = 1; i < allPages.Count; i++)
        {
            var prev = allPages[i - 1];
            var curr = allPages[i];

            (prev.CreatedAtUtc <= curr.CreatedAtUtc).ShouldBeTrue(
                $"CreatedAtUtc must be monotonically increasing. " +
                $"Task[{i - 1}].CreatedAtUtc={prev.CreatedAtUtc:O} (ID: {prev.Id}), " +
                $"Task[{i}].CreatedAtUtc={curr.CreatedAtUtc:O} (ID: {curr.Id}). " +
                $"Ordering violation detected!");
        }

        // Assert 6: CRITICAL - Chronological correctness (matches creation order)
        // Note: We only order by CreatedAtUtc since timestamps are unique (no GUID tiebreaker needed)
        var expectedOrder = createdTasks
                            .OrderBy(t => t.CreatedAtUtc)
                            .Select(t => t.Id)
                            .ToList();
        var actualOrder = allPages.Select(t => t.Id).ToList();

        for (var i = 0; i < expectedOrder.Count; i++)
        {
            actualOrder[i].ShouldBe(expectedOrder[i],
                $"Position {i}: expected {expectedOrder[i]} (created {createdTasks.First(t => t.Id == expectedOrder[i]).Type}), " +
                $"got {actualOrder[i]} (created {createdTasks.First(t => t.Id == actualOrder[i]).Type}). " +
                $"Pagination order does NOT match creation order!");
        }
    }

    /// <summary>
    /// R1: keyset tie-break on IDENTICAL CreatedAtUtc. Every existing keyset test uses UNIQUE timestamps, so
    /// none exercises the secondary <c>t.Id.CompareTo(lastGuid) &gt; 0</c> tie-break. When many rows share the
    /// same CreatedAtUtc the pagination must still walk them by Id without skipping or duplicating a single row.
    /// This stresses the server-side translation of the Guid keyset on Npgsql (uuid byte-wise ordering, which
    /// for the provider-optimized v7 GUIDs matches .NET Guid.CompareTo). Paginated with take = 1 to force the
    /// tie-break on every step.
    /// </summary>
    [Fact]
    public async Task RetrievePending_Keyset_Should_Not_Skip_Or_Duplicate_On_Identical_CreatedAtUtc()
    {
        // Arrange - all rows share ONE CreatedAtUtc; only the Id distinguishes them in the keyset.
        var sharedCreatedAt = FloorToMicroseconds(DateTimeOffset.UtcNow);
        var createdIds      = new List<Guid>();
        const int total     = 12;

        for (var i = 0; i < total; i++)
        {
            var taskId = GetGuidForProvider();
            createdIds.Add(taskId);
            await _storage.Persist(new QueuedTask
            {
                Id           = taskId,
                Type         = $"TieBreak{i}",
                Request      = "{}",
                Handler      = "Handler",
                CreatedAtUtc = sharedCreatedAt, // identical on purpose -> Id is the only tie-break
                Status       = QueuedTaskStatus.Pending
            });
        }

        // Act - walk one row at a time so the (CreatedAtUtc, Id) tie-break fires on every page.
        var             retrieved     = new List<Guid>();
        DateTimeOffset? lastCreatedAt = null;
        Guid?           lastId        = null;

        while (true)
        {
            var page = await _storage.RetrievePending(lastCreatedAt, lastId, 1);
            if (page.Length == 0)
                break;

            // Restrict to OUR ids: the shared-collection DB may hold leftovers from other tests.
            foreach (var t in page.Where(t => createdIds.Contains(t.Id)))
                retrieved.Add(t.Id);

            lastCreatedAt = page[^1].CreatedAtUtc;
            lastId        = page[^1].Id;
        }

        // Assert - no skips, no duplicates: exactly our 12 ids, each once.
        retrieved.Count.ShouldBe(total, "every same-timestamp row must be paged exactly once (no skip)");
        retrieved.ToHashSet().Count.ShouldBe(total, "no row may be returned twice across pages (no duplicate)");
        createdIds.Except(retrieved).ShouldBeEmpty("no same-timestamp row may be skipped by the keyset tie-break");
    }

    /// <summary>
    /// CurrentRunCount saturates at int.MaxValue instead of overflowing. An unbounded recurring series
    /// (MaxRuns == null) that reaches int.MaxValue real executions keeps running with the counter frozen at
    /// its max: it must NOT wrap to int.MinValue (the C# unchecked paths) NOR raise an out-of-range error
    /// (the server-side procs/CTEs). Covers both base C# paths (the AuditLevel.None fast-path and the audited
    /// path) on SQLite, and the stored-proc / writable-CTE overrides on SQL Server / PostgreSQL.
    /// </summary>
    [Theory]
    [InlineData(AuditLevel.None)]
    [InlineData(AuditLevel.Full)]
    public async Task UpdateCurrentRun_saturates_run_counter_at_int_max(AuditLevel auditLevel)
    {
        var id = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id = id, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, MaxRuns = null, CurrentRunCount = int.MaxValue
        });

        await Should.NotThrowAsync(() =>
            _storage.UpdateCurrentRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(1), auditLevel));

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.CurrentRunCount.ShouldBe(int.MaxValue, "the run counter must saturate at int.MaxValue, never wrap to int.MinValue or throw");
        row.ExecutionTimeMs.ShouldBe(10.0, "the rest of the update still applies (saturation is not a no-op)");
    }

    /// <summary>
    /// The recurring-completion counter saturates at int.MaxValue too, and the occurrence still completes.
    /// </summary>
    [Fact]
    public async Task CompleteRecurringRun_saturates_run_counter_at_int_max()
    {
        var id = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id = id, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, MaxRuns = null, CurrentRunCount = int.MaxValue
        });

        await Should.NotThrowAsync(() =>
            _storage.CompleteRecurringRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full));

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed, "the occurrence still completes");
        row.CurrentRunCount.ShouldBe(int.MaxValue, "the run counter must saturate at int.MaxValue, never wrap or throw");
    }

    #region AuditLevel Tests

    /// <summary>
    /// Tests for AuditLevel.Full - complete audit trail (default behavior)
    /// </summary>
    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_full_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.Queued
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull("LastExecutionUtc should be set for terminal status");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Full audit level should create StatusAudit record");

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_full_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test exception");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test exception");
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1);

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Failed);
        latestAudit.Exception.ShouldNotBeNull();
        latestAudit.Exception!.ShouldContain("Test exception");
    }

    /// <summary>
    /// Tests for AuditLevel.Minimal - only errors create StatusAudit, but always create RunsAudit for recurring
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_minimal_and_task_completes_successfully()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "Minimal audit level should NOT create StatusAudit for successful completion");
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_minimal_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test failure");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test failure");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Minimal audit level should create StatusAudit for failures");

        var latestAudit = _mockedDbContext.StatusAudit.OrderByDescending(x => x.Id)
                                          .FirstOrDefault(x => x.QueuedTaskId == taskId);
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Failed);
        latestAudit.Exception.ShouldNotBeNull();
        latestAudit.Exception!.ShouldContain("Test failure");
    }

    [Fact]
    public async Task
        Should_not_create_status_audit_when_audit_level_is_minimal_and_task_is_service_stopped_without_exception()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - ServiceStopped without exception is a clean shutdown, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, null, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "Minimal audit level should NOT create StatusAudit for clean shutdown");
    }

    /// <summary>
    /// Tests for AuditLevel.ErrorsOnly - only errors create audit records
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_errors_only_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "ErrorsOnly audit level should NOT create StatusAudit for success");
    }

    [Fact]
    public async Task Should_create_status_audit_when_audit_level_is_errors_only_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Error test");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "ErrorsOnly audit level should create StatusAudit for failures");
    }

    /// <summary>
    /// Tests for ServiceStopped with OperationCanceledException (expected shutdown behavior)
    /// Should NOT create audit in ErrorsOnly/Minimal modes
    /// </summary>
    [Fact]
    public async Task
        Should_not_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_errors_only()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - OperationCanceledException during shutdown is expected, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "ErrorsOnly should NOT audit ServiceStopped with OperationCanceledException");
    }

    [Fact]
    public async Task
        Should_not_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_minimal()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - OperationCanceledException during shutdown is expected, not an error
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount,
            "Minimal should NOT audit ServiceStopped with OperationCanceledException");
    }

    /// <summary>
    /// Tests for ServiceStopped with other exceptions (real errors during shutdown)
    /// SHOULD create audit in ErrorsOnly/Minimal modes
    /// </summary>
    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_other_exception_and_errors_only()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Real error during shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Other exceptions during shutdown ARE errors
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "ErrorsOnly should audit ServiceStopped with real exceptions");
    }

    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_other_exception_and_minimal()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Real error during shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Other exceptions during shutdown ARE errors
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Minimal should audit ServiceStopped with real exceptions");
    }

    [Fact]
    public async Task Should_create_status_audit_when_service_stopped_with_operation_cancelled_exception_and_full()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new OperationCanceledException("Service shutdown");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act - Full audit level always creates audit
        await _storage.SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount + 1, "Full audit level should always create StatusAudit");
    }

    /// <summary>
    /// Tests for AuditLevel.None - no audit trail at all
    /// </summary>
    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_none_and_task_completes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].LastExecutionUtc.ShouldNotBeNull();

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "None audit level should NOT create StatusAudit");
    }

    [Fact]
    public async Task Should_not_create_status_audit_when_audit_level_is_none_and_task_fails()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestTask",
            Request      = "{}",
            Handler      = "TestHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status       = QueuedTaskStatus.InProgress
        });

        var exception          = new InvalidOperationException("Test failure");
        var startingAuditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.Failed, exception, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();
        task[0].Exception!.ShouldContain("Test failure");

        var auditCount = _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == taskId);
        auditCount.ShouldBe(startingAuditCount, "None audit level should NOT create StatusAudit even for failures");
    }

    /// <summary>
    /// Tests for UpdateCurrentRun with different audit levels (recurring tasks)
    /// </summary>
    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_full_and_recurring_task_executes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = FloorToMicroseconds(DateTimeOffset.UtcNow.AddHours(1)); // R13: µs floor for exact round-trip

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);
        task[0].NextRunUtc.ShouldBe(nextRun);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1, "Full audit level should create RunsAudit record");
    }

    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_minimal_and_recurring_task_executes()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.Minimal);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1,
            "Minimal audit level should ALWAYS create RunsAudit to track last run");
    }

    [Fact]
    public async Task Should_not_create_runs_audit_when_audit_level_is_errors_only_and_recurring_task_succeeds()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount, "ErrorsOnly audit level should NOT create RunsAudit for successful runs");
    }

    [Fact]
    public async Task Should_create_runs_audit_when_audit_level_is_errors_only_and_recurring_task_fails()
    {
        // Arrange
        var taskId    = GetGuidForProvider();
        var exception = new InvalidOperationException("Recurring task failure");

        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Failed,
            Exception       = exception.ToString(),
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.ErrorsOnly);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1);

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount + 1, "ErrorsOnly audit level should create RunsAudit for failed runs");
    }

    [Fact]
    public async Task Should_not_create_runs_audit_when_audit_level_is_none()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = "TestTask",
            Request         = "{}",
            Handler         = "TestHandler",
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            CurrentRunCount = 0
        });

        var startingRunsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        var nextRun           = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _storage.UpdateCurrentRun(taskId, 100.0, nextRun, AuditLevel.None);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].CurrentRunCount.ShouldBe(1, "CurrentRunCount should be incremented even with None audit level");

        var runsCount = _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == taskId);
        runsCount.ShouldBe(startingRunsCount, "None audit level should NOT create RunsAudit");
    }

    /// <summary>
    /// Tests for terminal vs intermediate states with LastExecutionUtc
    /// </summary>
    [Fact]
    public async Task Should_update_last_execution_utc_for_terminal_states()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.InProgress,
            LastExecutionUtc = null
        });

        // Act - Completed is a terminal state
        await _storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].LastExecutionUtc.ShouldNotBeNull("Terminal states should set LastExecutionUtc");
    }

    [Fact]
    public async Task Should_not_update_last_execution_utc_for_intermediate_states()
    {
        // Arrange
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Queued,
            LastExecutionUtc = null
        });

        // Act - InProgress is an intermediate state
        await _storage.SetStatus(taskId, QueuedTaskStatus.InProgress, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].LastExecutionUtc.ShouldBeNull("Intermediate states should NOT set LastExecutionUtc");
    }

    [Fact]
    public async Task Should_not_set_last_execution_utc_when_reverting_to_waiting_queue()
    {
        // Full-queue revert (TryQueue race): the task never ran, so LastExecutionUtc must stay null
        var taskId = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Queued,
            LastExecutionUtc = null
        });

        // Act
        await _storage.SetStatus(taskId, QueuedTaskStatus.WaitingQueue, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        task[0].LastExecutionUtc.ShouldBeNull("WaitingQueue revert must not stamp a fake execution time");
    }

    [Fact]
    public async Task Should_preserve_last_execution_utc_on_intermediate_transitions()
    {
        // Re-queueing a recurring task between runs must keep the timestamp of its last real run
        var taskId  = GetGuidForProvider();
        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _storage.Persist(new QueuedTask
        {
            Id               = taskId,
            Type             = "TestTask",
            Request          = "{}",
            Handler          = "TestHandler",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = QueuedTaskStatus.Completed,
            IsRecurring      = true,
            LastExecutionUtc = lastRun
        });

        // Act - intermediate transition (next occurrence enqueued)
        await _storage.SetStatus(taskId, QueuedTaskStatus.Queued, null, AuditLevel.Full);

        // Assert
        var task = await _storage.Get(x => x.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Queued);
        task[0].LastExecutionUtc.ShouldNotBeNull("intermediate transitions must PRESERVE the last run timestamp");
        (task[0].LastExecutionUtc!.Value - lastRun).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1),
            "the preserved value must be the previous run timestamp, not a new one");
    }

    #endregion

    #region RetrievePending recovery filter

    private async Task<QueuedTask> PersistTaskWithStatus(
        QueuedTaskStatus status,
        bool isRecurring = false,
        DateTimeOffset? nextRunUtc = null)
    {
        var task = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            Type         = "RecoveryFilterTask",
            Request      = "{}",
            Handler      = "RecoveryFilterHandler",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status       = status,
            IsRecurring  = isRecurring,
            NextRunUtc   = nextRunUtc
        };
        await _storage.Persist(task);
        return task;
    }

    [Fact]
    public async Task RetrievePending_should_include_WaitingQueue_tasks()
    {
        // WaitingQueue = persisted but never delivered to a worker queue (parked in the
        // in-memory scheduler at shutdown, or dropped by a full queue with ThrowException).
        // Without this, delayed one-shot tasks are silently lost on restart.
        var task = await PersistTaskWithStatus(QueuedTaskStatus.WaitingQueue);

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task RetrievePending_should_include_recurring_tasks_between_runs()
    {
        // Between two runs a recurring task sits as Completed (or Failed after a bad run)
        // with a future NextRunUtc: it must be revived at startup.
        var completed = await PersistTaskWithStatus(QueuedTaskStatus.Completed,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));
        var failed = await PersistTaskWithStatus(QueuedTaskStatus.Failed,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldContain(t => t.Id == completed.Id);
        pending.ShouldContain(t => t.Id == failed.Id);
    }

    [Fact]
    public async Task RetrievePending_should_not_include_terminal_one_shot_tasks()
    {
        var completed = await PersistTaskWithStatus(QueuedTaskStatus.Completed);
        var failed    = await PersistTaskWithStatus(QueuedTaskStatus.Failed);
        var cancelled = await PersistTaskWithStatus(QueuedTaskStatus.Cancelled);

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldNotContain(t => t.Id == completed.Id);
        pending.ShouldNotContain(t => t.Id == failed.Id);
        pending.ShouldNotContain(t => t.Id == cancelled.Id);
    }

    [Fact]
    public async Task RetrievePending_should_not_include_recurring_tasks_without_next_run()
    {
        // Recurring task that exhausted its schedule: NextRunUtc is null, must not be revived.
        var exhausted = await PersistTaskWithStatus(QueuedTaskStatus.Completed,
            isRecurring: true, nextRunUtc: null);

        // A cancelled recurring task must never be revived, even with a future NextRunUtc.
        var cancelled = await PersistTaskWithStatus(QueuedTaskStatus.Cancelled,
            isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var pending = await _storage.RetrievePending(null, null, 100);

        pending.ShouldNotContain(t => t.Id == exhausted.Id);
        pending.ShouldNotContain(t => t.Id == cancelled.Id);
    }

    #endregion

    #region Recovery filter: execution vs finalization (X3)

    private async Task<QueuedTask> PersistRecurringRow(
        DateTimeOffset? nextRunUtc, DateTimeOffset? runUntil = null, int? maxRuns = null,
        int? currentRunCount = null, QueuedTaskStatus status = QueuedTaskStatus.Completed)
    {
        var task = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            Type            = "X3Task",
            Request         = "{}",
            Handler         = "X3Handler",
            CreatedAtUtc    = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10)),
            Status          = status,
            IsRecurring     = true,
            NextRunUtc      = nextRunUtc == null ? null : FloorToMicroseconds(nextRunUtc.Value),
            RunUntil        = runUntil == null ? null : FloorToMicroseconds(runUntil.Value),
            MaxRuns         = maxRuns,
            CurrentRunCount = currentRunCount
        };
        await _storage.Persist(task);
        return task;
    }

    [Fact]
    public async Task RetrievePending_should_recover_a_series_whose_pending_slot_precedes_an_elapsed_RunUntil()
    {
        // The occurrence was already scheduled BEFORE the boundary, and the boundary elapsed during the
        // downtime. Ungrouped, the RunUntil term alone dropped the row, so that occurrence was lost and the
        // series stayed Queued forever.
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(-30), runUntil: now.AddMinutes(-10),
            status: QueuedTaskStatus.Queued);

        var pending = await _storage.RetrievePending(now, null, null, 100);

        pending.ShouldContain(t => t.Id == row.Id);
        pending.First(t => t.Id == row.Id).IsRecurringSeriesToFinalize()
               .ShouldBeFalse("this row still has a slot to run, so it is not a finalization");
    }

    [Fact]
    public async Task RetrievePending_should_recover_a_series_to_finalize_when_its_slot_is_past_RunUntil()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(-5), runUntil: now.AddMinutes(-30),
            status: QueuedTaskStatus.Queued);

        var pending = await _storage.RetrievePending(now, null, null, 100);

        pending.ShouldContain(t => t.Id == row.Id,
            "a series whose remaining slots all fall past RunUntil used to match no predicate at all and " +
            "stayed Queued forever");
        pending.First(t => t.Id == row.Id).IsRecurringSeriesToFinalize().ShouldBeTrue();
    }

    [Fact]
    public async Task RetrievePending_should_recover_a_series_to_finalize_when_its_run_budget_is_spent()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(5), maxRuns: 3, currentRunCount: 3);

        var pending = await _storage.RetrievePending(now, null, null, 100);

        pending.ShouldContain(t => t.Id == row.Id);
        pending.First(t => t.Id == row.Id).IsRecurringSeriesToFinalize().ShouldBeTrue();
    }

    [Fact]
    public async Task RetrievePending_should_never_recover_a_cancelled_series_to_finalize()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(-5), runUntil: now.AddMinutes(-30),
            status: QueuedTaskStatus.Cancelled);

        var pending = await _storage.RetrievePending(now, null, null, 100);

        pending.ShouldNotContain(t => t.Id == row.Id,
            "a cancelled series is already terminal and must never be rewritten to Completed");
    }

    [Fact]
    public async Task RetrievePending_should_judge_RunUntil_on_the_clock_it_is_given()
    {
        // The storage must never resolve "now" itself: with a clock BEFORE the boundary the series is simply
        // still running, with one AFTER it the same row becomes a finalization.
        var boundary = FloorToMicroseconds(DateTimeOffset.UtcNow.AddHours(2));
        var row      = await PersistRecurringRow(boundary.AddMinutes(-10), runUntil: boundary,
            status: QueuedTaskStatus.Queued);

        var beforeBoundary = await _storage.RetrievePending(boundary.AddHours(-1), null, null, 100);
        var afterBoundary  = await _storage.RetrievePending(boundary.AddHours(1), null, null, 100);

        beforeBoundary.ShouldContain(t => t.Id == row.Id);
        afterBoundary.ShouldContain(t => t.Id == row.Id);
        afterBoundary.First(t => t.Id == row.Id).IsRecoverableForExecution(boundary.AddHours(1))
                     .ShouldBeTrue("its pending slot precedes the boundary, so it is still an execution");
    }

    [Fact]
    public async Task TrySetQueuedIfRecoverable_should_refuse_a_series_that_only_needs_finalizing()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(5), maxRuns: 1, currentRunCount: 1);

        var requeued = await _storage.TrySetQueuedIfRecoverable(now, row.Id, AuditLevel.Full);

        requeued.ShouldBeFalse("a spent series must be finalized, never handed back to a worker queue");
        (await _storage.Get(t => t.Id == row.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task The_recovery_predicates_should_follow_the_injected_scheduling_clock()
    {
        // P9 on this provider's own translation of the filter. The instant is not a literal the test wrote by
        // hand: it comes from the SAME FakeTimeProvider the core drives the pipeline with, and moving that
        // clock — nothing else — is what turns this row from pending into spent. Whether the RunUntil term is
        // evaluated server-side (SQL Server, PostgreSQL, MySQL) or client-side (SQLite), it must read the
        // parameter and never the database's or the host's own clock.
        var clock = new FakeTimeProvider(FloorToMicroseconds(DateTimeOffset.UtcNow));

        var row = new QueuedTask
        {
            Id           = GetGuidForProvider(),
            Type         = "P9Task",
            Request      = "{}",
            Handler      = "P9Handler",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = FloorToMicroseconds(clock.GetUtcNow().AddMinutes(-10)),
            RunUntil     = FloorToMicroseconds(clock.GetUtcNow().AddHours(2))
        };
        await _storage.Persist(row);

        (await _storage.RetrievePending(clock.GetUtcNow(), null, null, 100))
            .ShouldContain(t => t.Id == row.Id, "two hours before its boundary the row is simply pending");

        clock.Advance(TimeSpan.FromHours(3));

        (await _storage.RetrievePending(clock.GetUtcNow(), null, null, 100))
            .ShouldNotContain(t => t.Id == row.Id,
                "the boundary elapsed on the injected clock, and that is the only clock that moved");

        (await _storage.TrySetQueuedIfRecoverable(clock.GetUtcNow(), row.Id, AuditLevel.Full))
            .ShouldBeFalse("the conditional requeue reads the same instant as the page that fed it");
        (await _storage.Get(t => t.Id == row.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task A_failing_recovery_finalization_should_be_counted_and_cleared_by_a_later_success()
    {
        // L18 on a real engine, through the real WorkerService: a finalization that throws is counted
        // durably, the row stays recoverable while the count is under the limit, and the counter is cleared
        // the moment a later attempt succeeds. The counter, the terminal write and the audit all execute on
        // this provider — the in-memory sibling of this test cannot say whether the relational
        // implementations of IncrementRecoveryFailure / ClearRecoveryFailure agree with it.
        var now = DateTimeOffset.UtcNow;
        var row = await PersistRecurringRow(now.AddMinutes(-4), runUntil: now.AddMinutes(-5),
            status: QueuedTaskStatus.Queued);

        row.IsRecurringSeriesToFinalize().ShouldBeTrue("the scenario only holds if this row is a finalization");

        var faulted = new FaultInjectingTaskStorage(_storage);
        faulted.FailNext(nameof(ITaskStorage.TrySetRecurringSeriesCompleted), times: 1);

        var recovery = RecoveryHarness.CreateRecoveryService(faulted, maxAttempts: 3);

        await recovery.ProcessPendingAsync();

        var afterFailure = (await _storage.Get(t => t.Id == row.Id))[0];
        afterFailure.RecoveryDispatchFailureCount.ShouldBe(1, "a failing finalization is counted durably");
        afterFailure.Status.ShouldBe(QueuedTaskStatus.Queued, "one failure is transient: the row stays recoverable");
        afterFailure.NextRunUtc.ShouldNotBeNull();

        await recovery.ProcessPendingAsync();

        var finalized = (await _storage.Get(t => t.Id == row.Id))[0];
        finalized.Status.ShouldBe(QueuedTaskStatus.Completed);
        finalized.NextRunUtc.ShouldBeNull("finalizing clears the cursor in the same write");
        (finalized.RecoveryDispatchFailureCount ?? 0).ShouldBe(0,
            "a transient failure must not accumulate toward the poison limit once the attempt succeeded");

        _mockedDbContext.RunsAudit.Count(a => a.QueuedTaskId == row.Id).ShouldBe(0, "finalizing is not a run");
        _mockedDbContext.StatusAudit.Where(a => a.QueuedTaskId == row.Id).Select(a => a.NewStatus).ToList()
                        .ShouldBe([QueuedTaskStatus.Completed],
                            "one transition, Queued → Completed: the row never reached a worker queue");
    }

    #endregion

    #region Durable occurrences

    private async Task<QueuedTask> PersistSchedule(DateTimeOffset? cursorUtc, int scheduleVersion = 0,
                                                   QueuedTaskStatus status = QueuedTaskStatus.Queued)
    {
        var schedule = new QueuedTask
        {
            Id              = GetGuidForProvider(),
            Type            = "DurableSchedule",
            Request         = "{}",
            Handler         = "DurableHandler",
            CreatedAtUtc    = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10)),
            Status          = status,
            IsRecurring     = true,
            NextRunUtc      = cursorUtc is { } cursor ? FloorToMicroseconds(cursor) : null,
            ScheduleVersion = scheduleVersion,
            QueueName       = "recurring"
        };
        await _storage.Persist(schedule);
        return schedule;
    }

    private QueuedTask NewOccurrence(Guid parentId, DateTimeOffset slotUtc) => new()
    {
        Id                    = GetGuidForProvider(),
        Type                  = "DurableSchedule",
        Request               = "{}",
        Handler               = "DurableHandler",
        CreatedAtUtc          = FloorToMicroseconds(DateTimeOffset.UtcNow),
        ScheduledExecutionUtc = FloorToMicroseconds(slotUtc),
        Status                = QueuedTaskStatus.WaitingQueue,
        ParentTaskId          = parentId,
        QueueName             = "recurring",
        AuditLevel            = (int)AuditLevel.Full,
        RuntimeInfo           = "{\"SlotUtc\":\"probe\"}"
    };

    /// <summary>
    /// DDL that makes every <c>StatusAudit</c> INSERT fail, and the DDL that removes it again. It is the one
    /// fault point that sits AFTER the occurrence INSERT on all four implementations — the EF base stages the
    /// audit behind the occurrence in the same <c>SaveChanges</c>, the two procedures write it as their last
    /// statement, the Postgres CTE as its last branch — which is what makes
    /// <see cref="MaterializeOccurrence_should_roll_the_inserted_occurrence_back_when_a_later_write_faults"/>
    /// one shared assertion instead of four provider-specific ones. Every provider expresses it as a trigger:
    /// a check constraint would be validated against the rows already in the table.
    /// </summary>
    protected abstract string InstallStatusAuditInsertFaultSql { get; }

    /// <inheritdoc cref="InstallStatusAuditInsertFaultSql" />
    protected abstract string RemoveStatusAuditInsertFaultSql { get; }

    /// <summary>
    /// DDL that makes every <c>QueuedTasks</c> UPDATE fail, and the DDL that removes it again. This is the
    /// fault window the plan names literally — after the occurrence INSERT, before the schedule advance —
    /// and it lands there on the three implementations that insert first: the two procedures and the
    /// Postgres CTE. On the EF base, which stages the advance BEFORE the insert in the same
    /// <c>SaveChanges</c>, the same fault lands one step earlier; the assertion is the same either way
    /// (nothing survives the transaction), and the direction the EF base cannot reach here is covered by
    /// <see cref="MaterializeOccurrence_should_roll_the_inserted_occurrence_back_when_a_later_write_faults"/>.
    /// </summary>
    protected abstract string InstallScheduleAdvanceFaultSql { get; }

    /// <inheritdoc cref="InstallScheduleAdvanceFaultSql" />
    protected abstract string RemoveScheduleAdvanceFaultSql { get; }

    private async Task<IAsyncDisposable> InjectStatusAuditInsertFaultAsync()
    {
        var dbContext = (DbContext)_mockedDbContext;
        await dbContext.Database.ExecuteSqlRawAsync(InstallStatusAuditInsertFaultSql);
        return new RawSqlCleanup(dbContext, RemoveStatusAuditInsertFaultSql);
    }

    private async Task<IAsyncDisposable> InjectScheduleAdvanceFaultAsync()
    {
        var dbContext = (DbContext)_mockedDbContext;
        await dbContext.Database.ExecuteSqlRawAsync(InstallScheduleAdvanceFaultSql);
        return new RawSqlCleanup(dbContext, RemoveScheduleAdvanceFaultSql);
    }

    private sealed class RawSqlCleanup(DbContext dbContext, string sql) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dbContext.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public void A_relational_provider_advertises_both_durable_occurrence_capabilities()
    {
        // The positive half of X2: these four providers really do implement the atomic operations, so they
        // must say so — the region below is the proof that the claim is honoured. The negative half (EF Core
        // InMemory, where every one of them refuses) is in EfCoreNonRelationalCapabilityTests.
        _storage.SupportsDurableOccurrences.ShouldBeTrue();
        _storage.SupportsScheduleVersioning.ShouldBeTrue();
    }

    [Fact]
    public async Task MaterializeOccurrence_should_insert_the_occurrence_and_advance_the_cursor_together()
    {
        var cursor    = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule  = await PersistSchedule(cursor);
        var nextSlot  = FloorToMicroseconds(cursor.AddMinutes(5));
        var occurrence = NewOccurrence(schedule.Id, cursor);

        var outcome = await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, occurrence, nextSlot,
            AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);

        var child = (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem();
        child.ParentTaskId.ShouldBe(schedule.Id);
        child.ScheduledExecutionUtc.ShouldBe(cursor);
        child.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        child.QueueName.ShouldBe("recurring");
        child.RuntimeInfo.ShouldBe("{\"SlotUtc\":\"probe\"}");

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(nextSlot);
        parent.CurrentRunCount.ShouldBe(1, "a materialization IS the run of a durable series");
        parent.Status.ShouldBe(QueuedTaskStatus.Queued, "the series continues");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_finalize_the_series_in_the_same_commit_as_its_last_slot()
    {
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule   = await PersistSchedule(cursor);
        var occurrence = NewOccurrence(schedule.Id, cursor);

        var outcome = await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, occurrence,
            newCursorUtc: null, AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.Status.ShouldBe(QueuedTaskStatus.Completed);
        parent.NextRunUtc.ShouldBeNull("a finished series must stop matching the recovery filter");
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_AlreadyExists_and_leave_the_cursor_untouched()
    {
        // This is also the atomicity proof for the EF base path, where the cursor advance is attempted
        // BEFORE the insert: the duplicate makes the whole transaction roll back, advance included.
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule   = await PersistSchedule(cursor);
        var first      = NewOccurrence(schedule.Id, cursor);

        await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, first, cursor.AddMinutes(5), AuditLevel.Full);

        // Rewind the cursor by hand so the compare-and-swap passes and only the duplicate slot can refuse.
        var rewind = (await _storage.Get(t => t.Id == schedule.Id))[0];
        rewind.NextRunUtc = cursor;
        await _storage.UpdateTask(rewind);

        var duplicate = NewOccurrence(schedule.Id, cursor);
        var outcome   = await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, duplicate,
            cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.AlreadyExists);
        (await _storage.Get(t => t.Id == duplicate.Id)).ShouldBeEmpty();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(cursor, "a refused materialization must not advance the cursor");
        parent.CurrentRunCount.ShouldBe(1, "and must not consume a run either");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_CursorMoved_and_write_nothing()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var occurrence = NewOccurrence(schedule.Id, cursor);
        var outcome    = await _storage.MaterializeOccurrence(schedule.Id, 0,
            expectedCursorUtc: cursor.AddMinutes(-30), occurrence, cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.CursorMoved);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();
        (await _storage.Get(t => t.Id == schedule.Id))[0].NextRunUtc.ShouldBe(cursor);
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_VersionMismatch_when_the_schedule_was_rescheduled()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, scheduleVersion: 4);

        var occurrence = NewOccurrence(schedule.Id, cursor);
        var outcome    = await _storage.MaterializeOccurrence(schedule.Id, expectedScheduleVersion: 3, cursor,
            occurrence, cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.VersionMismatch);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_ParentInactive_for_a_cancelled_schedule()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, status: QueuedTaskStatus.Cancelled);

        var occurrence = NewOccurrence(schedule.Id, cursor);
        var outcome    = await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, occurrence,
            cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.ParentInactive);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_ParentInactive_for_a_finished_series_with_no_cursor()
    {
        // The retry a caller makes after re-reading a schedule it lost a race on: the row it read back has
        // no cursor left, so it retries with a null one. A finished series must refuse it — an insert here
        // would put an occurrence on a Completed schedule AND give it a cursor back, and the resurrected
        // series would match the recovery filter again at the next restart.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        (await _storage.TrySetRecurringSeriesCompleted(schedule.Id, cursor, QueuedTaskStatus.Queued, 0, 0,
            AuditLevel.Full)).ShouldBeTrue();

        var occurrence = NewOccurrence(schedule.Id, cursor);
        var outcome = await _storage.MaterializeOccurrence(schedule.Id, 0, expectedCursorUtc: null, occurrence,
            newCursorUtc: cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.ParentInactive);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.Status.ShouldBe(QueuedTaskStatus.Completed);
        parent.NextRunUtc.ShouldBeNull("a finished series must never get its cursor back");
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "and must not consume a run either");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_CursorMoved_when_a_null_cursor_is_expected()
    {
        // Same retry against a schedule that is still live: a null expected cursor cannot match the one the
        // row carries, so this is an ordinary lost race and nothing is written.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var occurrence = NewOccurrence(schedule.Id, cursor);
        var outcome = await _storage.MaterializeOccurrence(schedule.Id, 0, expectedCursorUtc: null, occurrence,
            cursor.AddMinutes(5), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.CursorMoved);
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldBeEmpty();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(cursor);
        (parent.CurrentRunCount ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task MaterializeOccurrence_should_roll_the_whole_transaction_back_on_an_injected_insert_fault()
    {
        // Atomicity under a fault that is NOT the natural duplicate-slot one. The duplicate slot is a case
        // every implementation checks for and classifies; this one is an arbitrary error raised by the
        // occurrence INSERT itself, with the cursor advance already part of the same transaction, and it must
        // still leave the store exactly as it was. It is injected by giving the occurrence the primary key of
        // a row that already exists — a different constraint from UX_QueuedTasks_Occurrence, so nothing
        // classifies it and the family's rethrow contract applies.
        //
        // The four implementations reach the same point by four different routes (EF base advances then
        // inserts, the SQL Server and MySQL procedures insert then advance inside one procedure, Postgres
        // does both in one writable CTE), which is exactly why the assertion belongs to the shared suite:
        // it is the only thing that proves the SQL Server procedure needs SET XACT_ABORT ON, without which
        // the failed INSERT aborts only its own statement and the procedure happily commits the advance.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var nextSlot = FloorToMicroseconds(cursor.AddMinutes(5));

        var occupied = NewOccurrence(schedule.Id, FloorToMicroseconds(cursor.AddMinutes(-30)));
        await _storage.Persist(occupied);

        var doomed = NewOccurrence(schedule.Id, cursor);
        doomed.Id = occupied.Id; // primary-key collision: the insert cannot succeed

        await Should.ThrowAsync<Exception>(() =>
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, doomed, nextSlot, AuditLevel.Full));

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(cursor, "a materialization that threw must not leave the cursor advanced");
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "nor consume a run");
        parent.Status.ShouldBe(QueuedTaskStatus.Queued);

        var children = await _storage.GetOccurrences(schedule.Id);
        children.ShouldHaveSingleItem().Id.ShouldBe(occupied.Id,
            "only the pre-existing occurrence survives: the failed one was never written");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_roll_back_the_final_advance_too_when_the_insert_faults()
    {
        // Same injected fault on the series-ENDING materialization (newCursor == null), which additionally
        // flips the schedule to Completed and stages its audit row in the same commit. A partial commit here
        // would end a series that never produced its last occurrence.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var occupied = NewOccurrence(schedule.Id, FloorToMicroseconds(cursor.AddMinutes(-30)));
        await _storage.Persist(occupied);

        var doomed = NewOccurrence(schedule.Id, cursor);
        doomed.Id = occupied.Id;

        await Should.ThrowAsync<Exception>(() =>
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, doomed, null, AuditLevel.Full));

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.Status.ShouldBe(QueuedTaskStatus.Queued, "the series must not be finalized by a failed commit");
        parent.NextRunUtc.ShouldBe(cursor);

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(0,
            "the Completed audit is staged in the same transaction and must roll back with it");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_roll_the_inserted_occurrence_back_when_a_later_write_faults()
    {
        // The direction the two tests above cannot reach. There the INSERT is what fails, so on the EF base —
        // which advances the cursor first and inserts last — nothing was ever written for the occurrence, and
        // on the two procedures the advance simply never runs. This one lets the occurrence INSERT SUCCEED and
        // fails a write that comes after it on every implementation, which is the only way to show that a row
        // already in the table goes away again with the transaction.
        //
        // The series-ending materialization is the shape that has such a write: a null new cursor also flips
        // the schedule to Completed and records that transition, and the injected fault refuses exactly that
        // record.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var doomed   = NewOccurrence(schedule.Id, cursor);

        await using (await InjectStatusAuditInsertFaultAsync())
        {
            await Should.ThrowAsync<Exception>(() =>
                _storage.MaterializeOccurrence(schedule.Id, 0, cursor, doomed, null, AuditLevel.Full));
        }

        (await _storage.Get(t => t.Id == doomed.Id)).ShouldBeEmpty(
            "the occurrence row was inserted and must not survive the transaction that inserted it");
        (await _storage.GetOccurrences(schedule.Id)).ShouldBeEmpty();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.Status.ShouldBe(QueuedTaskStatus.Queued, "the series must not be finalized by a failed commit");
        parent.NextRunUtc.ShouldBe(cursor, "nor the cursor cleared");
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "nor a run consumed");
        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(0);

        // And the fault is gone with the trigger: the very same materialization now succeeds, which is what
        // proves the assertions above came from the injected failure and not from a schedule that was never
        // eligible in the first place.
        var retried = NewOccurrence(schedule.Id, cursor);
        (await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, retried, null, AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);
    }

    [Fact]
    public async Task MaterializeOccurrence_should_roll_everything_back_when_the_schedule_advance_faults()
    {
        // The window the plan names: the occurrence INSERT has happened and the schedule has not advanced
        // yet. On the two procedures and the Postgres CTE the fault lands exactly there — they insert the
        // child and then update the schedule — so this is the interleaving under test; on the EF base, which
        // stages the advance first, it lands just before the insert. Either way the pair is one transaction
        // and neither half may survive alone: a child without its advance is a slot that will be materialized
        // twice, an advance without its child is a slot silently skipped.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var nextSlot = FloorToMicroseconds(cursor.AddMinutes(5));
        var doomed   = NewOccurrence(schedule.Id, cursor);

        await using (await InjectScheduleAdvanceFaultAsync())
        {
            await Should.ThrowAsync<Exception>(() =>
                _storage.MaterializeOccurrence(schedule.Id, 0, cursor, doomed, nextSlot, AuditLevel.Full));
        }

        (await _storage.Get(t => t.Id == doomed.Id)).ShouldBeEmpty("the occurrence must not outlive the advance");
        (await _storage.GetOccurrences(schedule.Id)).ShouldBeEmpty();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(cursor, "the cursor is where the failed advance left it");
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "and no run was consumed");
        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(0);

        // The control: with the trigger gone the very same call goes through, so the assertions above came
        // from the injected fault and not from a schedule that was never eligible.
        var retried = NewOccurrence(schedule.Id, cursor);
        (await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, retried, nextSlot, AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);
        (await _storage.Get(t => t.Id == schedule.Id))[0].NextRunUtc.ShouldBe(nextSlot);
    }

    [Fact]
    public async Task MaterializeOccurrence_should_report_AlreadyExists_to_both_callers_racing_a_taken_slot()
    {
        // The concurrent shape AlreadyExists really has. Two writers that both find the cursor where they
        // left it cannot both reach the slot check — the winner advances the cursor in the same commit, so
        // the loser is told CursorMoved (pinned below). AlreadyExists is what a writer is told when the
        // cursor still points AT a slot that is already materialized: an episode replayed after a rewind, a
        // peer that inserted the row from outside the materializer, a retry after a lost answer. Racing two
        // of those is the case the durable-occurrence contract has to survive, and both must be refused
        // without touching the schedule.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var nextSlot = FloorToMicroseconds(cursor.AddMinutes(5));

        var occupant = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(occupant);

        var first  = NewOccurrence(schedule.Id, cursor);
        var second = NewOccurrence(schedule.Id, cursor);

        var outcomes = await Task.WhenAll(
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, first, nextSlot, AuditLevel.Full),
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, second, nextSlot, AuditLevel.Full));

        outcomes.ShouldAllBe(o => o == OccurrenceMaterializationOutcome.AlreadyExists,
            "a taken slot refuses every writer, and the refusal is named for what it is");

        (await _storage.Get(t => t.Id == first.Id)).ShouldBeEmpty();
        (await _storage.Get(t => t.Id == second.Id)).ShouldBeEmpty();
        (await _storage.GetOccurrences(schedule.Id)).ShouldHaveSingleItem().Id.ShouldBe(occupant.Id);

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(cursor, "a refused materialization must not advance the cursor");
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "nor consume a run");
    }

    [Fact]
    public async Task MaterializeOccurrence_should_be_won_by_exactly_one_of_two_concurrent_callers()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var nextSlot = FloorToMicroseconds(cursor.AddMinutes(5));

        var first  = NewOccurrence(schedule.Id, cursor);
        var second = NewOccurrence(schedule.Id, cursor);

        var outcomes = await Task.WhenAll(
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, first, nextSlot, AuditLevel.Full),
            _storage.MaterializeOccurrence(schedule.Id, 0, cursor, second, nextSlot, AuditLevel.Full));

        // The loser's outcome is named, not merely "not Created": the two are not interchangeable to the
        // caller. CursorMoved says the schedule advanced and the loser must re-read before deciding again;
        // AlreadyExists would say the slot is taken while the cursor still points AT it, which is a different
        // situation and cannot arise from this race — the winner advances the cursor in the same commit that
        // creates the row, and every implementation compares the cursor before it looks for the slot. Where
        // AlreadyExists really comes from is pinned separately, by rewinding the cursor by hand.
        outcomes.Order().ToArray().ShouldBe(
            new[]
            {
                OccurrenceMaterializationOutcome.Created,
                OccurrenceMaterializationOutcome.CursorMoved
            },
            "the compare-and-swap on version and cursor admits exactly one writer, and tells the other why");

        var children = await _storage.GetOccurrences(schedule.Id);
        children.Length.ShouldBe(1, "one slot, one row");
        (await _storage.Get(t => t.Id == schedule.Id))[0].CurrentRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task TrySetRecurringSeriesCompleted_should_finalize_only_while_the_expected_state_holds()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var stale = await _storage.TrySetRecurringSeriesCompleted(schedule.Id, cursor.AddMinutes(-5),
            QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full);
        stale.ShouldBeFalse("a finalization computed on a cursor that has since moved must not be written");
        (await _storage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        var applied = await _storage.TrySetRecurringSeriesCompleted(schedule.Id, cursor,
            QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full);
        applied.ShouldBeTrue();

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.NextRunUtc.ShouldBeNull();
    }

    [Fact]
    public async Task The_conditional_schedule_writes_should_refuse_a_null_expected_cursor()
    {
        // Read literally, "the cursor I expect is null" is the shape of every finalized and poisoned row, so
        // a caller that read a schedule back after losing a race and retried with what it found would finalize
        // or halt a series that had already ended. MaterializeOccurrence refuses that expectation explicitly;
        // its two siblings have to refuse it the same way rather than translate it into `NextRunUtc IS NULL`.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        (await _storage.TrySetRecurringSeriesCompleted(schedule.Id, null, QueuedTaskStatus.Queued, 0, 0,
            AuditLevel.Full)).ShouldBeFalse();
        (await _storage.TryHaltSchedule(schedule.Id, 0, null, QueuedTaskStatus.Queued, "{}")).ShouldBeFalse();

        var live = (await _storage.Get(t => t.Id == schedule.Id))[0];
        live.Status.ShouldBe(QueuedTaskStatus.Queued);
        live.NextRunUtc.ShouldBe(cursor);
        live.RuntimeInfo.ShouldBeNull();

        // And on the row the null expectation would otherwise have matched: the series is over, and it stays
        // over rather than being finalized a second time or halted after the fact.
        (await _storage.TrySetRecurringSeriesCompleted(schedule.Id, cursor, QueuedTaskStatus.Queued, 0, 0,
            AuditLevel.Full)).ShouldBeTrue();

        (await _storage.TrySetRecurringSeriesCompleted(schedule.Id, null, QueuedTaskStatus.Completed, 0, 0,
            AuditLevel.Full)).ShouldBeFalse();
        (await _storage.TryHaltSchedule(schedule.Id, 0, null, QueuedTaskStatus.Completed, "{\"Halted\":{}}"))
            .ShouldBeFalse();

        var finished = (await _storage.Get(t => t.Id == schedule.Id))[0];
        finished.NextRunUtc.ShouldBeNull();
        finished.RuntimeInfo.ShouldBeNull("a finished series must not acquire a halt marker");
    }

    [Fact]
    public async Task TrySetRecurringSeriesCompleted_should_lose_to_a_cancel_that_linearized_first()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        await _storage.SetCancelledByUser(schedule.Id, AuditLevel.Full);

        var finalized = await _storage.TrySetRecurringSeriesCompleted(schedule.Id, cursor,
            QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full);

        finalized.ShouldBeFalse();
        (await _storage.Get(t => t.Id == schedule.Id))[0].Status
            .ShouldBe(QueuedTaskStatus.Cancelled, "the user's choice must survive a recovery finalization");
    }

    [Fact]
    public async Task CancelSchedule_should_cancel_the_schedule_and_only_its_pending_occurrences()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var waiting = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(waiting);

        var running = NewOccurrence(schedule.Id, cursor.AddMinutes(1));
        running.Status = QueuedTaskStatus.InProgress;
        await _storage.Persist(running);

        await _storage.CancelSchedule(schedule.Id, AuditLevel.Full);

        (await _storage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await _storage.Get(t => t.Id == waiting.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await _storage.Get(t => t.Id == running.Id))[0].Status
            .ShouldBe(QueuedTaskStatus.InProgress, "an occurrence already executing owns a live delivery");
    }

    [Fact]
    public async Task CancelSchedule_should_be_a_silent_no_op_when_the_schedule_is_already_gone()
    {
        // Cancel racing a Remove of the same schedule. Every provider must simply write nothing: an audit
        // row staged for the vanished schedule would violate the StatusAudit foreign key and surface as a
        // provider-specific exception out of Dispatcher.Cancel.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        await _storage.Remove(schedule.Id);

        await Should.NotThrowAsync(() => _storage.CancelSchedule(schedule.Id, AuditLevel.Full));

        (await _storage.Get(t => t.Id == schedule.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CancelSchedule_should_also_cancel_an_occurrence_the_service_stopped()
    {
        // R7: the cancelled set has to be the exact complement of the set startup recovery puts back in a
        // queue, and ServiceStopped is in that set. Leaving it out means an occurrence of a schedule the user
        // cancelled comes back one restart later and runs.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var stopped = NewOccurrence(schedule.Id, cursor);
        stopped.Status = QueuedTaskStatus.ServiceStopped;
        await _storage.Persist(stopped);

        await _storage.CancelSchedule(schedule.Id, AuditLevel.Full);

        (await _storage.Get(t => t.Id == stopped.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);

        (await _storage.TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, stopped.Id, AuditLevel.Full))
            .ShouldBeFalse("and recovery must then refuse to put it back in a queue");
    }

    // ---- What the audit level does to the durable operations -------------------------------------
    // Every implementation branches on it — `IF @AuditLevel = 0` in the two procedures, the @createAudit /
    // @finalizeAudit flags in the Postgres CTE, AuditPolicy.ShouldCreateStatusAudit in the EF base — and the
    // whole durable region called them with nothing but Full, so the non-audited half of each branch ran on
    // no provider. Minimal is the level the enum's own documentation recommends for a high-frequency
    // recurring task, which is the shape a durable catch-up has; at Minimal only a real error is recorded,
    // and none of the transitions below is one.

    [Theory]
    [InlineData(AuditLevel.Full, 1)]
    [InlineData(AuditLevel.Minimal, 0)]
    [InlineData(AuditLevel.None, 0)]
    public async Task MaterializeOccurrence_should_audit_the_end_of_a_series_only_at_the_level_that_asks_for_it(
        AuditLevel auditLevel, int expectedAudits)
    {
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule   = await PersistSchedule(cursor);
        var occurrence = NewOccurrence(schedule.Id, cursor);

        (await _storage.MaterializeOccurrence(schedule.Id, 0, cursor, occurrence, newCursorUtc: null, auditLevel))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.Status.ShouldBe(QueuedTaskStatus.Completed, "what is WRITTEN never depends on the audit level");
        parent.NextRunUtc.ShouldBeNull();
        (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem();

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(expectedAudits);
    }

    [Theory]
    [InlineData(AuditLevel.Full, 1)]
    [InlineData(AuditLevel.Minimal, 0)]
    [InlineData(AuditLevel.None, 0)]
    public async Task CancelSchedule_should_audit_the_cancellation_only_at_the_level_that_asks_for_it(
        AuditLevel auditLevel, int expectedAuditsPerRow)
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var pending = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(pending);

        await _storage.CancelSchedule(schedule.Id, auditLevel);

        (await _storage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await _storage.Get(t => t.Id == pending.Id))[0].Status
            .ShouldBe(QueuedTaskStatus.Cancelled, "the cascade is the write, and the level only decides the trail");

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(expectedAuditsPerRow);
        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == pending.Id).ShouldBe(expectedAuditsPerRow);
    }

    [Theory]
    [InlineData(AuditLevel.Full, 1)]
    [InlineData(AuditLevel.Minimal, 0)]
    [InlineData(AuditLevel.None, 0)]
    public async Task RequeueTerminal_should_audit_the_requeue_only_at_the_level_that_asks_for_it(
        AuditLevel auditLevel, int expectedAudits)
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var child = NewOccurrence(schedule.Id, cursor);
        child.Status    = QueuedTaskStatus.Failed;
        child.Exception = "boom";
        await _storage.Persist(child);

        (await _storage.RequeueTerminal(child.Id, auditLevel)).ShouldBeTrue();

        var row = (await _storage.Get(t => t.Id == child.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Queued);
        row.Exception.ShouldBeNull();

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == child.Id).ShouldBe(expectedAudits);
    }

    [Theory]
    [InlineData(AuditLevel.Full, 1)]
    [InlineData(AuditLevel.Minimal, 0)]
    [InlineData(AuditLevel.None, 0)]
    public async Task TryRequeueStaleOccurrence_should_audit_the_requeue_only_at_the_level_that_asks_for_it(
        AuditLevel auditLevel, int expectedAudits)
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var child    = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(child);

        (await _storage.TryRequeueStaleOccurrence(child.Id, QueuedTaskStatus.WaitingQueue, auditLevel))
            .ShouldBeTrue();

        (await _storage.Get(t => t.Id == child.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == child.Id).ShouldBe(expectedAudits);
    }

    [Fact]
    public async Task TryAdvanceScheduleCursor_should_move_the_cursor_without_counting_a_run()
    {
        // Skipping a slot changes nothing but where the schedule points: no occurrence, no run, no audit.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);
        var skipTo   = FloorToMicroseconds(cursor.AddMinutes(30));

        (await _storage.TryAdvanceScheduleCursor(schedule.Id, 0, cursor, skipTo)).ShouldBeTrue();

        var parent = (await _storage.Get(t => t.Id == schedule.Id))[0];
        parent.NextRunUtc.ShouldBe(skipTo);
        (parent.CurrentRunCount ?? 0).ShouldBe(0, "nothing executed, so nothing may be counted against MaxRuns");
        parent.Status.ShouldBe(QueuedTaskStatus.Queued);

        _mockedDbContext.StatusAudit.Count(a => a.QueuedTaskId == schedule.Id)
                        .ShouldBe(0, "a skipped slot is not a status transition");
        _mockedDbContext.RunsAudit.Count(a => a.QueuedTaskId == schedule.Id).ShouldBe(0);
    }

    [Fact]
    public async Task TryAdvanceScheduleCursor_should_lose_when_the_cursor_already_moved()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);
        var moved    = FloorToMicroseconds(cursor.AddMinutes(1));

        (await _storage.TryAdvanceScheduleCursor(schedule.Id, 0, cursor, moved)).ShouldBeTrue();

        (await _storage.TryAdvanceScheduleCursor(schedule.Id, 0, cursor, FloorToMicroseconds(cursor.AddHours(1))))
            .ShouldBeFalse("the compare-and-swap is on the cursor the decision was computed from");

        (await _storage.Get(t => t.Id == schedule.Id))[0].NextRunUtc.ShouldBe(moved);
    }

    [Fact]
    public async Task TryAdvanceScheduleCursor_should_lose_on_a_rescheduled_or_cancelled_schedule()
    {
        var cursor    = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var versioned = await PersistSchedule(cursor, scheduleVersion: 3);

        (await _storage.TryAdvanceScheduleCursor(versioned.Id, 0, cursor, FloorToMicroseconds(cursor.AddHours(1))))
            .ShouldBeFalse("a schedule rescheduled under the caller is a lost race, not a stale write");

        var cancelled = await PersistSchedule(cursor, status: QueuedTaskStatus.Cancelled);

        (await _storage.TryAdvanceScheduleCursor(cancelled.Id, 0, cursor, FloorToMicroseconds(cursor.AddHours(1))))
            .ShouldBeFalse("and a cancelled schedule has no cursor left to move");
    }

    [Fact]
    public async Task RequeueTerminal_should_requeue_a_failed_row_keeping_its_identity_and_its_audit_trail()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var child    = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(child);

        // A real failed attempt, so there is a history worth preserving: the transitions of the run and the
        // run record itself. Keeping the id is only half the promise — an operator requeues an occurrence to
        // retry it, not to erase what happened the first time.
        await _storage.SetQueued(child.Id, AuditLevel.Full);
        await _storage.SetInProgress(child.Id, AuditLevel.Full);
        await _storage.SetStatus(child.Id, QueuedTaskStatus.Failed, new InvalidOperationException("boom"),
            AuditLevel.Full);

        // The run record is seeded rather than produced: only the recurring-advance operations write RunsAudit,
        // and this row is a one-shot occurrence. It stands for the history a requeued occurrence carries — the
        // reason RequeueTerminal resets the status in place instead of replacing the row.
        _mockedDbContext.RunsAudit.Add(new RunsAudit
        {
            QueuedTaskId    = child.Id,
            ExecutedAt      = FloorToMicroseconds(DateTimeOffset.UtcNow),
            ExecutionTimeMs = 12,
            Status          = QueuedTaskStatus.Failed,
            Exception       = "boom"
        });
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        var statusAuditsBefore = _mockedDbContext.StatusAudit
                                                 .Where(a => a.QueuedTaskId == child.Id).Select(a => a.Id)
                                                 .ToList();
        var runAuditsBefore = _mockedDbContext.RunsAudit
                                              .Where(a => a.QueuedTaskId == child.Id).Select(a => a.Id)
                                              .ToList();

        statusAuditsBefore.Count.ShouldBeGreaterThan(0, "the arrange above must really have written a trail");
        runAuditsBefore.ShouldHaveSingleItem();

        (await _storage.RequeueTerminal(child.Id, AuditLevel.Full)).ShouldBeTrue();

        var row = (await _storage.Get(t => t.Id == child.Id))[0];
        row.Id.ShouldBe(child.Id, "a requeue keeps the identity, so history and audits stay attached");
        row.Status.ShouldBe(QueuedTaskStatus.Queued);
        row.Exception.ShouldBeNull();

        var statusAuditsAfter = _mockedDbContext.StatusAudit
                                                .Where(a => a.QueuedTaskId == child.Id).ToList();
        var runAuditsAfter = _mockedDbContext.RunsAudit
                                             .Where(a => a.QueuedTaskId == child.Id).ToList();

        statusAuditsBefore.ShouldBeSubsetOf(statusAuditsAfter.Select(a => a.Id),
            "every transition recorded before the requeue is still there");
        statusAuditsAfter.Count.ShouldBe(statusAuditsBefore.Count + 1,
            "and the requeue appends its own Queued transition rather than rewriting the trail");
        statusAuditsAfter.ShouldContain(a => a.NewStatus == QueuedTaskStatus.Failed,
            "including the failure the requeue is undoing");

        runAuditsAfter.Select(a => a.Id).ShouldBe(runAuditsBefore,
            "the failed run itself is history: a requeue adds no run and removes none");
        runAuditsAfter[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        runAuditsAfter[0].Exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task RequeueTerminal_should_refuse_a_row_that_is_not_terminal()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        (await _storage.RequeueTerminal(schedule.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task TryRequeueStaleOccurrence_should_win_only_while_the_status_still_matches()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var child    = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(child);

        (await _storage.TryRequeueStaleOccurrence(child.Id, QueuedTaskStatus.InProgress, AuditLevel.Full))
            .ShouldBeFalse("the occurrence is WaitingQueue: whoever expected InProgress lost the race");

        (await _storage.TryRequeueStaleOccurrence(child.Id, QueuedTaskStatus.WaitingQueue, AuditLevel.Full))
            .ShouldBeTrue();
        (await _storage.Get(t => t.Id == child.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task UpdateSchedule_should_bump_the_version_only_from_the_expected_one()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, scheduleVersion: 2);
        var newCursor = FloorToMicroseconds(cursor.AddHours(1));

        (await _storage.UpdateSchedule(schedule.Id, 1, cursor, "{\"stale\":true}", "stale", newCursor, null, null,
             null))
            .ShouldBeFalse("two concurrent reschedules cannot both win");

        (await _storage.UpdateSchedule(schedule.Id, 2, cursor, "{\"fresh\":true}", "fresh", newCursor, 7, null,
             "{}"))
            .ShouldBeTrue();

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.ScheduleVersion.ShouldBe(3);
        row.RecurringTask.ShouldBe("{\"fresh\":true}");
        row.NextRunUtc.ShouldBe(newCursor);
        row.MaxRuns.ShouldBe(7);
        row.RuntimeInfo.ShouldBe("{}");
    }

    [Fact]
    public async Task UpdateSchedule_should_refuse_a_definition_decided_on_a_cursor_a_run_has_moved()
    {
        // The version alone is not the state a reschedule decides against: a completed run advances the
        // cursor AND the run counter without ever touching the version, so a reschedule that read the row
        // before that run — and sized its bounds on the run count it saw — must lose here. Version-only, it
        // committed a cursor computed from a budget the completion had already spent, and the series ran one
        // occurrence past MaxRuns.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var advanced = FloorToMicroseconds(cursor.AddHours(1));

        await _storage.UpdateCurrentRun(schedule.Id, 4, advanced, AuditLevel.Full);

        (await _storage.UpdateSchedule(schedule.Id, 0, cursor, "{\"stale\":true}", "stale",
             FloorToMicroseconds(cursor.AddMinutes(30)), 3, null, null))
            .ShouldBeFalse("the cursor the reschedule decided against is gone, and so is the run count with it");

        var untouched = (await _storage.Get(t => t.Id == schedule.Id))[0];
        untouched.ScheduleVersion.ShouldBe(0);
        untouched.NextRunUtc.ShouldBe(advanced);
        untouched.MaxRuns.ShouldBeNull();

        // The control: the SAME call, carrying the cursor the row actually stands at, goes through.
        (await _storage.UpdateSchedule(schedule.Id, 0, advanced, "{\"fresh\":true}", "fresh",
             FloorToMicroseconds(advanced.AddHours(1)), 3, null, null))
            .ShouldBeTrue();

        (await _storage.Get(t => t.Id == schedule.Id))[0].ScheduleVersion.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateSchedule_should_accept_a_null_cursor_expectation_on_a_series_that_ended()
    {
        // A finished series is a legitimate reschedule target — dispatching it again under its key is how a
        // schedule is restarted — and its cursor is null. The compare-and-swap has to read that as IS NULL:
        // an equality against a null parameter matches nothing in SQL and would refuse every one of them.
        var schedule = await PersistSchedule(null);

        var cursor = FloorToMicroseconds(DateTimeOffset.UtcNow.AddHours(1));

        (await _storage.UpdateSchedule(schedule.Id, 0, cursor, "{\"stale\":true}", "stale", cursor, null, null,
             null))
            .ShouldBeFalse("the row has no cursor, so an expectation of one is a lost race");

        (await _storage.UpdateSchedule(schedule.Id, 0, null, "{\"fresh\":true}", "fresh", cursor, null, null, null))
            .ShouldBeTrue();

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.ScheduleVersion.ShouldBe(1);
        row.NextRunUtc.ShouldBe(cursor);
    }

    [Fact]
    public async Task UpdateSchedule_should_refuse_a_schedule_that_was_cancelled_under_it()
    {
        // The one state neither half of the compare-and-swap answers for: a cancel writes the status and
        // leaves the version and the cursor exactly where they were, so a reschedule that read the row first
        // matches on both and writes a live definition and a fresh cursor over a series an operator ended —
        // then reports success. The row would satisfy no recovery predicate and the series would never run
        // again, while its caller believed it had.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        await _storage.SetStatus(schedule.Id, QueuedTaskStatus.Cancelled, null, AuditLevel.Full);

        var newCursor = FloorToMicroseconds(cursor.AddHours(1));

        (await _storage.UpdateSchedule(schedule.Id, 0, cursor, "{\"stale\":true}", "stale", newCursor, null, null,
             null))
            .ShouldBeFalse("a cancellation is terminal, and the version and the cursor still match");

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled);
        row.ScheduleVersion.ShouldBe(0);
        row.NextRunUtc.ShouldBe(cursor);
        row.RecurringTask.ShouldNotBe("{\"stale\":true}");
    }

    [Fact]
    public async Task UpdateSchedule_should_still_accept_a_schedule_that_is_running()
    {
        // The control, and the half S3 is about: InProgress is never a refusal. Only Cancelled is.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, status: QueuedTaskStatus.InProgress);

        (await _storage.UpdateSchedule(schedule.Id, 0, cursor, "{\"fresh\":true}", "fresh",
             FloorToMicroseconds(cursor.AddHours(1)), null, null, null))
            .ShouldBeTrue();

        (await _storage.Get(t => t.Id == schedule.Id))[0].ScheduleVersion.ShouldBe(1);
    }

    [Fact]
    public async Task TryHaltSchedule_should_refuse_a_halt_computed_on_a_stale_cursor()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        (await _storage.TryHaltSchedule(schedule.Id, 0, cursor.AddMinutes(-5), QueuedTaskStatus.Queued, "{}"))
            .ShouldBeFalse("a halt describing a state that no longer exists must not freeze a live schedule");
        (await _storage.Get(t => t.Id == schedule.Id))[0].RuntimeInfo.ShouldBeNull();

        (await _storage.TryHaltSchedule(schedule.Id, 0, cursor, QueuedTaskStatus.Queued,
             "{\"Halted\":{\"Reason\":\"cap\"}}")).ShouldBeTrue();
        (await _storage.Get(t => t.Id == schedule.Id))[0].RuntimeInfo.ShouldNotBeNull().ShouldContain("Halted");
    }

    /// <summary>
    /// F1/#37. SQLite keeps a <c>DateTimeOffset</c> as ISO-8601 TEXT with the offset written inside it, so
    /// there equality is representational: <c>10:00+02:00</c> and <c>08:00+00:00</c> are one instant and two
    /// different strings. The three cursor compare-and-swaps normalize their own operands to UTC, so a
    /// schedule row written through the public storage API at <c>+02:00</c> — which only a direct call can
    /// produce, the dispatch pipeline always hands UTC over — used to lose every one of them, for ever.
    /// <c>Persist</c> normalizes the row instead, which is the fix that holds on every provider.
    /// </summary>
    [Fact]
    public async Task The_cursor_compare_and_swaps_should_match_a_cursor_persisted_at_a_non_utc_offset()
    {
        var utcCursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var localCursor = utcCursor.ToOffset(TimeSpan.FromHours(2));

        // One schedule per operation: each of the three ends the state the next one would need.
        var toMaterialize = await PersistSchedule(localCursor);
        var toFinalize    = await PersistSchedule(localCursor);
        var toHalt        = await PersistSchedule(localCursor);

        (await _storage.MaterializeOccurrence(toMaterialize.Id, 0, utcCursor,
             NewOccurrence(toMaterialize.Id, utcCursor), FloorToMicroseconds(utcCursor.AddMinutes(5)),
             AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);

        (await _storage.TrySetRecurringSeriesCompleted(toFinalize.Id, utcCursor, QueuedTaskStatus.Queued, 0, 0,
             AuditLevel.Full)).ShouldBeTrue();

        // A halt leaves the cursor where it is, so this row still carries what Persist wrote.
        (await _storage.TryHaltSchedule(toHalt.Id, 0, utcCursor, QueuedTaskStatus.Queued,
             "{\"Halted\":{\"Reason\":\"cap\"}}")).ShouldBeTrue();

        (await _storage.Get(t => t.Id == toHalt.Id))[0].NextRunUtc.ShouldNotBeNull().Offset
            .ShouldBe(TimeSpan.Zero, "storage stores the instant, not the caller's offset");
    }

    /// <summary>
    /// The other half of F1/#37: <c>UpdateTask</c> is the public entry point that rewrites the cursor itself,
    /// so a cursor moved there at a non-zero offset must still be the one a compare-and-swap matches.
    /// </summary>
    [Fact]
    public async Task UpdateTask_should_normalize_a_cursor_written_at_a_non_utc_offset()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var moved    = FloorToMicroseconds(cursor.AddMinutes(5));

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.NextRunUtc = moved.ToOffset(TimeSpan.FromHours(2));
        await _storage.UpdateTask(row);

        (await _storage.TryHaltSchedule(schedule.Id, 0, moved, QueuedTaskStatus.Queued,
             "{\"Halted\":{\"Reason\":\"cap\"}}")).ShouldBeTrue();

        (await _storage.Get(t => t.Id == schedule.Id))[0].NextRunUtc.ShouldNotBeNull().Offset
            .ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task UpdateCurrentRun_with_a_stale_schedule_version_should_report_VersionMismatch()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, scheduleVersion: 5);
        var nextRun  = FloorToMicroseconds(cursor.AddHours(1));

        (await _storage.UpdateCurrentRun(schedule.Id, 12, nextRun, AuditLevel.Full, 4))
            .ShouldBe(ScheduleCasResult.VersionMismatch);
        (await _storage.Get(t => t.Id == schedule.Id))[0].NextRunUtc
            .ShouldBe(cursor, "a run that finished after a reschedule must not force its stale next run");

        (await _storage.UpdateCurrentRun(schedule.Id, 12, nextRun, AuditLevel.Full, 5))
            .ShouldBe(ScheduleCasResult.Applied);

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.NextRunUtc.ShouldBe(nextRun);
        row.CurrentRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteRecurringRun_with_a_stale_schedule_version_should_report_VersionMismatch()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, scheduleVersion: 2);
        var nextRun  = FloorToMicroseconds(cursor.AddHours(1));

        (await _storage.CompleteRecurringRun(schedule.Id, 8, nextRun, AuditLevel.Full, 1))
            .ShouldBe(ScheduleCasResult.VersionMismatch);
        (await _storage.Get(t => t.Id == schedule.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        (await _storage.CompleteRecurringRun(schedule.Id, 8, nextRun, AuditLevel.Full, 2))
            .ShouldBe(ScheduleCasResult.Applied);

        var row = (await _storage.Get(t => t.Id == schedule.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.NextRunUtc.ShouldBe(nextRun);
        row.CurrentRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task Remove_should_delete_a_schedule_together_with_its_occurrences()
    {
        // The self-referencing foreign key is Restrict, so a schedule with children can only be deleted by
        // deleting them in the same transaction — which is what keeps a Remove from ever leaving orphans.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var child    = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(child);

        await _storage.Remove(schedule.Id);

        (await _storage.Get(t => t.Id == schedule.Id)).ShouldBeEmpty();
        (await _storage.Get(t => t.Id == child.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Persist_should_reject_an_occurrence_without_its_slot()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        var slotless = NewOccurrence(schedule.Id, cursor);
        slotless.ScheduledExecutionUtc = null;

        await Should.ThrowAsync<Exception>(() => _storage.Persist(slotless));
    }

    [Fact]
    public async Task Persist_should_reject_a_second_row_for_the_same_slot()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);

        await _storage.Persist(NewOccurrence(schedule.Id, cursor));

        await Should.ThrowAsync<Exception>(() => _storage.Persist(NewOccurrence(schedule.Id, cursor)));
    }

    [Fact]
    public async Task Persist_should_reject_an_occurrence_whose_schedule_does_not_exist()
    {
        // The self-referencing foreign key, from the other side than Remove_should_delete_a_schedule_together
        // _with_its_occurrences: it is what stops an occurrence from existing without a schedule at all. An
        // orphan is a row nothing advances, nothing cancels and nothing prunes, because every occurrence query
        // — GetOccurrences, CountActiveOccurrences, CancelSchedule — starts from a parent id.
        var cursor = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));

        var orphan = NewOccurrence(GetGuidForProvider(), cursor);

        await Should.ThrowAsync<Exception>(() => _storage.Persist(orphan));
        (await _storage.Get(t => t.Id == orphan.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetOccurrences_and_CountActiveOccurrences_should_see_only_this_schedule()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor);
        var other    = await PersistSchedule(cursor);

        var active = NewOccurrence(schedule.Id, cursor);
        await _storage.Persist(active);

        var finished = NewOccurrence(schedule.Id, cursor.AddMinutes(1));
        await _storage.Persist(finished);
        await _storage.SetCompleted(finished.Id, 1, AuditLevel.Full);

        await _storage.Persist(NewOccurrence(other.Id, cursor));

        (await _storage.GetOccurrences(schedule.Id)).Length.ShouldBe(2);
        (await _storage.GetOccurrences(schedule.Id, nonTerminalOnly: true)).ShouldHaveSingleItem()
                                                                          .Id.ShouldBe(active.Id);
        (await _storage.CountActiveOccurrences(schedule.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task GetOccurrencesPage_should_return_one_page_newest_first_with_the_whole_total()
    {
        // The page is what keeps a schedule with a long retention behind it readable: the caller asks for a
        // hundred rows and the database orders, counts and slices, instead of handing back every occurrence
        // for someone else to sort in memory.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);
        var other    = await PersistSchedule(cursor);

        var slots = Enumerable.Range(0, 5).Select(i => cursor.AddMinutes(i)).ToArray();

        foreach (var slot in slots)
            await _storage.Persist(NewOccurrence(schedule.Id, slot));

        await _storage.Persist(NewOccurrence(other.Id, cursor));

        var first = await _storage.GetOccurrencesPage(schedule.Id, nonTerminalOnly: false, skip: 0, take: 2);

        first.TotalCount.ShouldBe(5, "the total is the whole series, never the size of the page");
        first.Occurrences.Select(o => o.ScheduledExecutionUtc)
             .ShouldBe([slots[4], slots[3]], "newest slot first, like the endpoint that reads it");

        var second = await _storage.GetOccurrencesPage(schedule.Id, nonTerminalOnly: false, skip: 2, take: 2);

        second.TotalCount.ShouldBe(5);
        second.Occurrences.Select(o => o.ScheduledExecutionUtc).ShouldBe([slots[2], slots[1]]);

        var past = await _storage.GetOccurrencesPage(schedule.Id, nonTerminalOnly: false, skip: 5, take: 2);

        past.Occurrences.ShouldBeEmpty("a page past the end is empty, not the last one again");
        past.TotalCount.ShouldBe(5);
    }

    [Fact]
    public async Task GetOccurrencesPage_should_count_and_page_only_what_the_filter_keeps()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);

        var pending  = NewOccurrence(schedule.Id, cursor);
        var finished = NewOccurrence(schedule.Id, cursor.AddMinutes(1));

        await _storage.Persist(pending);
        await _storage.Persist(finished);
        await _storage.SetCompleted(finished.Id, 1, AuditLevel.Full);

        var page = await _storage.GetOccurrencesPage(schedule.Id, nonTerminalOnly: true, skip: 0, take: 10);

        page.TotalCount.ShouldBe(1, "the total counts the filtered set, not the whole schedule");
        page.Occurrences.ShouldHaveSingleItem().Id.ShouldBe(pending.Id);
    }

    /// <summary>
    /// The page is a page all the way down to the database. Asserting the ROWS alone passes just as well on
    /// an implementation that reads the whole series and slices it in memory — which is the one thing this
    /// member exists to stop, and which is invisible until a schedule with a year of retention behind it is
    /// opened. So the command the provider sent is read back and made to carry the slice.
    /// </summary>
    [Fact]
    public async Task GetOccurrencesPage_should_let_the_database_order_and_slice_the_series()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);

        foreach (var slot in Enumerable.Range(0, 5).Select(i => cursor.AddMinutes(i)))
            await _storage.Persist(NewOccurrence(schedule.Id, slot));

        using var recorder = new SqlRecorder();

        // EF stops building command event data for a cache window whenever it last found nobody listening, so
        // the first call after this recorder subscribes can publish nothing at all. The call is therefore
        // repeated until the provider speaks — it answers the same thing every time, and what it SENDS is the
        // subject here. Waiting for any command instead would end on one another test published.
        string[] commands = [];
        string?  pageQuery = null;

        for (var attempt = 0; attempt < 40 && pageQuery is null; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(250);

            var page = await _storage.GetOccurrencesPage(schedule.Id, nonTerminalOnly: false, skip: 1, take: 2);
            page.Occurrences.Length.ShouldBe(2);
            page.TotalCount.ShouldBe(5);

            commands = recorder.Commands();

            // The slot ordering is what tells this query apart from every other read of the same table:
            // nothing else in the storage orders QueuedTasks by ScheduledExecutionUtc descending.
            pageQuery = commands.FirstOrDefault(
                c => c.Contains("ScheduledExecutionUtc", StringComparison.OrdinalIgnoreCase)
                     && c.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                     && c.Contains("DESC", StringComparison.OrdinalIgnoreCase));
        }

        pageQuery.ShouldNotBeNull(
            "the ordering belongs to the database, never to the caller's memory. Commands seen: "
            + string.Join(" | ", commands));

        // LIMIT/OFFSET on SQLite, PostgreSQL and MySQL; OFFSET … FETCH on SQL Server.
        (pageQuery.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
         || pageQuery.Contains("FETCH", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue("the slice belongs to the database too, so one page costs one page");

        commands.Any(c => c.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                          && c.Contains("QueuedTasks", StringComparison.Ordinal))
                .ShouldBeTrue("the total is counted in the store: counting it here would materialize the series");
    }

    /// <summary>
    /// The SQL the EF providers really sent, taken off the EF diagnostic source. It subscribes for the
    /// duration of one call, so what it holds is what that call cost.
    /// </summary>
    private sealed class SqlRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>,
                                       IDisposable
    {
        private readonly List<IDisposable>       _subscriptions = [];
        private readonly ConcurrentQueue<string> _commands      = new();
        private readonly IDisposable             _listeners;

        public SqlRecorder() => _listeners = DiagnosticListener.AllListeners.Subscribe(this);

        public string[] Commands() => _commands.Distinct(StringComparer.Ordinal).ToArray();

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name) return;

            lock (_subscriptions)
                _subscriptions.Add(listener.Subscribe(this));
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is CommandEventData data)
                _commands.Enqueue(data.Command.CommandText);
        }

        void IObserver<DiagnosticListener>.OnCompleted() { }
        void IObserver<DiagnosticListener>.OnError(Exception error) { }
        void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

        public void Dispose()
        {
            _listeners.Dispose();

            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                    subscription.Dispose();

                _subscriptions.Clear();
            }
        }
    }

    [Fact]
    public async Task GetLastRunStarts_should_answer_when_a_run_began_even_while_it_is_still_in_flight()
    {
        // No column says when a run STARTED: LastExecutionUtc is written on the terminal transition, so it
        // says when one ENDED, and a row that is still running has not written it at all. The InProgress
        // transition in the audit trail is the only trace of the moment, and it has to be READ — no storage
        // read populates the StatusAudits navigation, so a reader that walks it gets nothing here.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);

        var running   = NewOccurrence(schedule.Id, cursor);
        var finished  = NewOccurrence(schedule.Id, cursor.AddMinutes(1));
        var neverRan  = NewOccurrence(schedule.Id, cursor.AddMinutes(2));
        var unaudited = NewOccurrence(schedule.Id, cursor.AddMinutes(3));

        foreach (var row in new[] { running, finished, neverRan, unaudited })
            await _storage.Persist(row);

        await _storage.SetInProgress(running.Id, AuditLevel.Full);

        await _storage.SetInProgress(finished.Id, AuditLevel.Full);
        await _storage.SetCompleted(finished.Id, 25, AuditLevel.Full);

        // Below Full nothing records that transition, so this row has no recorded start at all.
        await _storage.SetInProgress(unaudited.Id, AuditLevel.Minimal);

        var starts = await _storage.GetLastRunStarts(
            [running.Id, finished.Id, neverRan.Id, unaudited.Id]);

        starts.ContainsKey(running.Id).ShouldBeTrue("a run in flight has a recorded start, and nothing else does");
        starts.ContainsKey(finished.Id).ShouldBeTrue();
        starts.ContainsKey(neverRan.Id).ShouldBeFalse("a row that never started has no start to report");
        starts.ContainsKey(unaudited.Id).ShouldBeFalse("nothing recorded the transition, so nothing is answered");

        var ended = (await _storage.Get(t => t.Id == finished.Id))[0].LastExecutionUtc;
        ended.ShouldNotBeNull();
        starts[finished.Id].ShouldBeLessThanOrEqualTo(ended!.Value, "a run begins before it ends");

        (await _storage.GetLastRunStarts([])).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLastRunStarts_should_answer_with_the_newest_run_of_a_row_that_ran_more_than_once()
    {
        // A requeued occurrence, and every recurring row: the start that answers for the row is the one of the
        // run it is in now, never the one of an attempt that already ended.
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule   = await PersistSchedule(cursor);
        var occurrence = NewOccurrence(schedule.Id, cursor);

        await _storage.Persist(occurrence);

        await _storage.SetInProgress(occurrence.Id, AuditLevel.Full);
        await _storage.SetStatus(occurrence.Id, QueuedTaskStatus.Failed, new InvalidOperationException("boom"),
            AuditLevel.Full);

        var firstAttempt = (await _storage.GetLastRunStarts([occurrence.Id]))[occurrence.Id];

        await Task.Delay(50);
        await _storage.SetInProgress(occurrence.Id, AuditLevel.Full);

        var secondAttempt = (await _storage.GetLastRunStarts([occurrence.Id]))[occurrence.Id];

        secondAttempt.ShouldBeGreaterThan(firstAttempt, "the second attempt is the run this row stands for");
    }

    [Fact]
    public async Task GetStatusAudits_should_answer_the_whole_transition_history_newest_first()
    {
        // The audit trail has to be READ. Nothing populates the StatusAudits navigation on a row a query
        // hands back, so a reader walking it answers an empty history for a row whose audit table holds
        // every transition it ever made — which is what the status-history endpoint used to do here.
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule   = await PersistSchedule(cursor);
        var occurrence = NewOccurrence(schedule.Id, cursor);

        await _storage.Persist(occurrence);
        await _storage.SetInProgress(occurrence.Id, AuditLevel.Full);
        await _storage.SetCompleted(occurrence.Id, 25, AuditLevel.Full);

        (await _storage.Get(t => t.Id == occurrence.Id))[0].StatusAudits
            .ShouldBeEmpty("the premise: the row a read hands back carries no audits at all");

        var audits = await _storage.GetStatusAudits(occurrence.Id);

        audits.Select(a => a.NewStatus)
              .ShouldBe([QueuedTaskStatus.Completed, QueuedTaskStatus.InProgress],
                  "newest transition first, like the endpoint that reads it");
        audits.ShouldAllBe(a => a.QueuedTaskId == occurrence.Id);

        (await _storage.GetStatusAudits(Guid.NewGuid())).ShouldBeEmpty("a row nobody stored has no history");
    }

    [Fact]
    public async Task GetStatusAudits_should_answer_only_for_the_row_it_was_asked_about()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule = await PersistSchedule(cursor);

        var asked = NewOccurrence(schedule.Id, cursor);
        var other = NewOccurrence(schedule.Id, cursor.AddMinutes(1));

        await _storage.Persist(asked);
        await _storage.Persist(other);

        await _storage.SetStatus(other.Id, QueuedTaskStatus.Failed, new InvalidOperationException("boom"),
            AuditLevel.Full);

        var audits = await _storage.GetStatusAudits(asked.Id);

        audits.ShouldAllBe(a => a.QueuedTaskId == asked.Id);
        audits.ShouldNotContain(a => a.NewStatus == QueuedTaskStatus.Failed);
    }

    [Fact]
    public async Task GetRunsAudits_should_answer_every_recorded_run_newest_first()
    {
        // A recurring row is the one that runs more than once, and its runs live in their own table for
        // exactly that reason: the row itself keeps only the last duration.
        var series = await PersistSchedule(FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10)));

        await _storage.UpdateCurrentRun(series.Id, 11, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full);
        await _storage.UpdateCurrentRun(series.Id, 22, DateTimeOffset.UtcNow.AddMinutes(2), AuditLevel.Full);

        (await _storage.Get(t => t.Id == series.Id))[0].RunsAudits
            .ShouldBeEmpty("the premise, again: the navigation is empty on the row a read hands back");

        var runs = await _storage.GetRunsAudits(series.Id);

        runs.Length.ShouldBe(2);
        runs.Select(r => r.ExecutionTimeMs).ShouldBe([22, 11], "newest run first");
        runs.ShouldAllBe(r => r.QueuedTaskId == series.Id);

        (await _storage.GetRunsAudits(Guid.NewGuid())).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStatusAuditsPage_should_page_the_trail_in_the_database_and_keep_the_total_whole()
    {
        // The paged half of the two reads above (#44). A long-lived recurring row holds one transition per
        // state per run, so the reader that shows twenty of them must never transfer the series to do it:
        // the count and the slice are the database's, over the (QueuedTaskId) index the table already has.
        var cursor     = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var schedule   = await PersistSchedule(cursor);
        var occurrence = NewOccurrence(schedule.Id, cursor);

        await _storage.Persist(occurrence);

        for (var i = 0; i < 10; i++)
        {
            await _storage.SetInProgress(occurrence.Id, AuditLevel.Full);
            await _storage.SetCompleted(occurrence.Id, 10 + i, AuditLevel.Full);
        }

        var walked = new List<long>();

        for (var skip = 0; skip < 20; skip += 5)
        {
            var page = await _storage.GetStatusAuditsPage(occurrence.Id, skip, 5);

            page.TotalCount.ShouldBe(20, "the total is the whole trail, whatever the page holds");
            page.Audits.Length.ShouldBe(5);
            walked.AddRange(page.Audits.Select(a => a.Id));
        }

        walked.Distinct().Count().ShouldBe(20, "no page repeated or dropped an entry");
        walked.ShouldBe((await _storage.GetStatusAudits(occurrence.Id)).Select(a => a.Id).ToList(),
            "newest first, exactly the order the unpaged read answers");

        var past = await _storage.GetStatusAuditsPage(occurrence.Id, 100, 5);

        past.Audits.ShouldBeEmpty();
        past.TotalCount.ShouldBe(20, "past the end is an empty page, not an empty trail");
    }

    [Fact]
    public async Task GetRunsAuditsPage_should_page_the_runs_and_answer_the_count_alone_for_an_empty_page()
    {
        var series = await PersistSchedule(FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10)));

        for (var i = 1; i <= 6; i++)
            await _storage.UpdateCurrentRun(series.Id, i * 10, DateTimeOffset.UtcNow.AddMinutes(i), AuditLevel.Full);

        var page = await _storage.GetRunsAuditsPage(series.Id, 2, 3);

        page.TotalCount.ShouldBe(6);
        page.Audits.Select(r => r.ExecutionTimeMs).ShouldBe([40d, 30d, 20d], "newest run first, from row three on");

        // A zero-row FETCH is a syntax error on some engines, not an empty result: take = 0 must never reach
        // one, and must still answer the count.
        var countOnly = await _storage.GetRunsAuditsPage(series.Id, 0, 0);

        countOnly.Audits.ShouldBeEmpty();
        countOnly.TotalCount.ShouldBe(6);

        (await _storage.GetRunsAuditsPage(Guid.NewGuid(), 0, 10)).TotalCount
            .ShouldBe(0, "a row nobody stored has no runs");
    }

    [Fact]
    public async Task CleanupTerminalOccurrences_should_prune_every_terminal_state_and_keep_the_schedule()
    {
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
        var schedule = await PersistSchedule(cursor);

        var completed = NewOccurrence(schedule.Id, cursor);
        var failed    = NewOccurrence(schedule.Id, cursor.AddMinutes(1));
        var cancelled = NewOccurrence(schedule.Id, cursor.AddMinutes(2));
        var pending   = NewOccurrence(schedule.Id, cursor.AddMinutes(3));

        foreach (var row in new[] { completed, failed, cancelled, pending })
        {
            row.CreatedAtUtc = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
            await _storage.Persist(row);
        }

        await _storage.SetCompleted(completed.Id, 1, AuditLevel.None);
        await _storage.SetStatus(failed.Id, QueuedTaskStatus.Failed, new InvalidOperationException("x"),
            AuditLevel.None);
        await _storage.SetCancelledByUser(cancelled.Id, AuditLevel.None);

        // The age gate is anchored on LastExecutionUtc, which a terminal transition stamps at that moment, so
        // the cutoff has to sit just past "now" for occurrences that finished during the test to qualify.
        var deleted = await ((EfCoreTaskStorage)_storage)
            .CleanupTerminalOccurrences(DateTimeOffset.UtcNow.AddMinutes(1), preserveTasksWithLogs: false,
                                        preserveTasksWithAudits: false);

        deleted.ShouldBe(3, "the ordinary completed-task purge would have kept the failed and cancelled ones");
        (await _storage.Get(t => t.Id == pending.Id)).ShouldHaveSingleItem();
        (await _storage.Get(t => t.Id == schedule.Id)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CleanupTerminalOccurrences_should_keep_an_occurrence_that_still_owns_execution_logs()
    {
        // R15: the occurrence window is usually far shorter than the log window, and deleting the row
        // cascades its TaskExecutionLog rows. The log passes run earlier in the same cycle, so a log still
        // there is one the log retention chose to keep — the same guard CleanupCompletedTasks already honours.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
        var schedule = await PersistSchedule(cursor);

        var withLogs = NewOccurrence(schedule.Id, cursor);
        var noLogs   = NewOccurrence(schedule.Id, cursor.AddMinutes(1));

        foreach (var row in new[] { withLogs, noLogs })
        {
            row.CreatedAtUtc = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
            await _storage.Persist(row);
            await _storage.SetCompleted(row.Id, 1, AuditLevel.None);
        }

        await _storage.SaveExecutionLogsAsync(withLogs.Id, [
            new TaskExecutionLog
            {
                TaskId         = withLogs.Id,
                TimestampUtc   = FloorToMicroseconds(DateTimeOffset.UtcNow),
                SequenceNumber = 1,
                Level          = "Information",
                Message        = "kept by the log retention window"
            }
        ], CancellationToken.None);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);

        var preserved = await ((EfCoreTaskStorage)_storage)
            .CleanupTerminalOccurrences(cutoff, preserveTasksWithLogs: true, preserveTasksWithAudits: false);

        preserved.ShouldBe(1, "only the occurrence with no surviving logs may be pruned");
        (await _storage.Get(t => t.Id == withLogs.Id)).ShouldHaveSingleItem();
        (await _storage.GetExecutionLogsAsync(withLogs.Id, CancellationToken.None)).Count.ShouldBe(1,
            "the cascade must not destroy a log 82 days before its own window expires");
        (await _storage.Get(t => t.Id == noLogs.Id)).ShouldBeEmpty();

        // With no log retention active the historic cascade-on-purge behaviour stands.
        var withGuardOff = await ((EfCoreTaskStorage)_storage)
            .CleanupTerminalOccurrences(cutoff, preserveTasksWithLogs: false, preserveTasksWithAudits: false);

        withGuardOff.ShouldBe(1);
        (await _storage.Get(t => t.Id == withLogs.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CleanupTerminalOccurrences_should_keep_an_occurrence_whose_audit_trail_a_window_still_holds()
    {
        // The audit half of the guard above, and the half the occurrence pass shipped without: deleting a row
        // cascades FK_StatusAudit_QueuedTasks and FK_RunsAudit_QueuedTasks, and the audit passes run earlier
        // in the same cycle — so an audit row still present is one a configured window chose to keep. The
        // occurrence window is typically 7 days against an error window of 90, so without this a failure
        // recorded on day 0 was destroyed on day 8, with only an occurrence count in the cleanup line.
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
        var schedule = await PersistSchedule(cursor);

        var audited   = NewOccurrence(schedule.Id, cursor);
        var unaudited = NewOccurrence(schedule.Id, cursor.AddMinutes(1));

        foreach (var row in new[] { audited, unaudited })
        {
            row.CreatedAtUtc = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-30));
            await _storage.Persist(row);
        }

        await _storage.SetStatus(audited.Id, QueuedTaskStatus.Failed,
            new InvalidOperationException("kept by the error window"), AuditLevel.Full);
        await _storage.SetCompleted(unaudited.Id, 1, AuditLevel.None);

        (await _storage.GetStatusAudits(audited.Id)).ShouldNotBeEmpty(
            "the premise: the row really carries the audit trail the window is holding");

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);

        var preserved = await ((EfCoreTaskStorage)_storage)
            .CleanupTerminalOccurrences(cutoff, preserveTasksWithLogs: false, preserveTasksWithAudits: true);

        preserved.ShouldBe(1, "only the occurrence with no surviving audit row may be pruned");
        (await _storage.Get(t => t.Id == audited.Id)).ShouldHaveSingleItem();
        (await _storage.GetStatusAudits(audited.Id)).ShouldNotBeEmpty(
            "the cascade must not destroy an audit row 82 days before its own window expires");
        (await _storage.Get(t => t.Id == unaudited.Id)).ShouldBeEmpty();

        // With no audit retention active the historic cascade-on-purge behaviour stands.
        var withGuardOff = await ((EfCoreTaskStorage)_storage)
            .CleanupTerminalOccurrences(cutoff, preserveTasksWithLogs: false, preserveTasksWithAudits: false);

        withGuardOff.ShouldBe(1);
        (await _storage.Get(t => t.Id == audited.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MaterializeOccurrence_should_persist_the_same_row_shape_on_every_provider()
    {
        // The three optimized providers hardcode the occurrence shape in their INSERT column list while the
        // EF base inserts the caller's entity: without one canonical contract the SAME call stored a
        // materially different row per backend (schedule version, run count, and every schedule-only field
        // the entity happened to carry).
        var cursor   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        var schedule = await PersistSchedule(cursor, scheduleVersion: 5);

        var occurrence = NewOccurrence(schedule.Id, cursor);
        occurrence.Status          = QueuedTaskStatus.Queued;
        occurrence.ScheduleVersion = 0;
        occurrence.CurrentRunCount = 7;
        occurrence.IsRecurring     = true;
        occurrence.TaskKey         = "a-key-that-belongs-to-the-schedule";
        occurrence.RecurringTask   = "{\"copied\":true}";
        occurrence.RecurringInfo   = "every minute";
        occurrence.MaxRuns         = 3;
        occurrence.RunUntil        = FloorToMicroseconds(cursor.AddDays(1));
        occurrence.NextRunUtc      = FloorToMicroseconds(cursor.AddMinutes(5));
        occurrence.Exception       = "stale";

        var outcome = await _storage.MaterializeOccurrence(schedule.Id, 5, cursor, occurrence,
            FloorToMicroseconds(cursor.AddMinutes(5)), AuditLevel.Full);

        outcome.ShouldBe(OccurrenceMaterializationOutcome.Created);

        var child = (await _storage.Get(t => t.Id == occurrence.Id)).ShouldHaveSingleItem();
        child.ScheduleVersion.ShouldBe(5, "the occurrence belongs to the version it was materialized against");
        child.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        (child.CurrentRunCount ?? 0).ShouldBe(0);
        child.IsRecurring.ShouldBeFalse();
        child.TaskKey.ShouldBeNull("a task key is unique per row and stays on the schedule");
        child.RecurringTask.ShouldBeNull();
        child.RecurringInfo.ShouldBeNull();
        child.MaxRuns.ShouldBeNull();
        child.RunUntil.ShouldBeNull();
        child.NextRunUtc.ShouldBeNull("a cursor on a child would make it look like a series to recovery");
        child.Exception.ShouldBeNull();
        child.LastExecutionUtc.ShouldBeNull();

        // What the caller legitimately supplies survives untouched.
        child.ParentTaskId.ShouldBe(schedule.Id);
        child.ScheduledExecutionUtc.ShouldBe(cursor);
        child.QueueName.ShouldBe("recurring");
        child.RuntimeInfo.ShouldBe("{\"SlotUtc\":\"probe\"}");
        child.AuditLevel.ShouldBe((int)AuditLevel.Full);
    }

    #endregion

    #region Legacy serialization round-trip (B4 — Newtonsoft -> STJ, no data loss / no encoding corruption)

    private static readonly JsonSerializerSettings LegacyJsonSettings = new() { TypeNameHandling = TypeNameHandling.None };

    [Fact]
    public async Task Legacy_emoji_payload_survives_db_round_trip_byte_for_byte()
    {
        // The DB column must preserve a legacy Newtonsoft payload carrying non-ASCII + 4-byte UTF-8 emoji
        // exactly (collation/encoding fidelity). STJ's ability to READ that exact JSON is pinned by the unit
        // parity tests; here we prove the relational provider does not mangle the stored bytes.
        const string emojiPayload = "Caffè è perché 日本語のテスト 🚀🔥✅";
        var legacyRequest = JsonConvert.SerializeObject(new { Text = emojiPayload }, LegacyJsonSettings);

        var id = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id           = id,
            Type         = "LegacyEmojiTask",
            Request      = legacyRequest,
            Handler      = "H",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.Request.ShouldBe(legacyRequest, "the legacy emoji payload must round-trip through the DB unchanged");
        row.Request.ShouldContain("🚀", Case.Sensitive);

        // P2-4: don't stop at DB byte fidelity — exercise the REAL recovery READ path (EverTaskJson) on the
        // bytes that came back from this provider, asserting the TYPED payload, not just the raw string.
        var recovered = EverTaskJson.Deserialize<LegacyPayloadProbeTask>(row.Request)!;
        recovered.Text.ShouldBe(emojiPayload,
            "the legacy emoji payload must deserialize back to the exact string via the real STJ read path");
    }

    [Fact]
    public async Task Legacy_recurring_DayInterval_OnDays_metadata_survives_db_round_trip()
    {
        // A recurring row's RecurringTask metadata (legacy Newtonsoft) carrying a DayInterval.OnDays schedule
        // must be persisted and read back unchanged on every relational provider, so recovery can re-read the
        // schedule under STJ (OnDays preservation itself is pinned by the unit interval-parity tests).
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(0, [DayOfWeek.Monday, DayOfWeek.Friday])
        };
        var legacyRecurring = JsonConvert.SerializeObject(recurring, LegacyJsonSettings);
        legacyRecurring.ShouldContain("OnDays");

        var id = GetGuidForProvider();
        await _storage.Persist(new QueuedTask
        {
            Id            = id,
            Type          = "LegacyRecurringTask",
            Request       = "{}",
            Handler       = "H",
            Status        = QueuedTaskStatus.Completed,
            IsRecurring   = true,
            RecurringTask = legacyRecurring,
            NextRunUtc    = DateTimeOffset.UtcNow.AddMinutes(30),
            CreatedAtUtc  = DateTimeOffset.UtcNow
        });

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.RecurringTask.ShouldBe(legacyRecurring,
            "the legacy recurring schedule metadata must round-trip through the DB unchanged");

        // P2-4: exercise the REAL recovery READ path on the bytes from this provider and assert the TYPED
        // schedule (OnDays) survived — not just the raw JSON string. This is exactly what recovery does.
        var recovered = EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!;
        recovered.DayInterval.ShouldNotBeNull();
        recovered.DayInterval!.OnDays.ShouldBe([DayOfWeek.Monday, DayOfWeek.Friday],
            "the legacy DayInterval.OnDays schedule must deserialize back typed via the real STJ read path");
    }

    #endregion

    #region Audit consistency across providers (M8 batch A)

    private async Task<Guid> SeedSimpleTask(
        QueuedTaskStatus status = QueuedTaskStatus.InProgress, DateTimeOffset? lastExecutionUtc = null)
    {
        var task = new QueuedTask
        {
            Id               = GetGuidForProvider(),
            Type             = "AuditConsistencyTask",
            Request          = "{}",
            Handler          = "H",
            CreatedAtUtc     = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status           = status,
            LastExecutionUtc = lastExecutionUtc
        };
        await _storage.Persist(task);
        return task.Id;
    }

    [Fact]
    public async Task Should_audit_servicestopped_consistently_across_providers()
    {
        // F19/L29: a ServiceStopped carrying an OperationCanceledException (expected shutdown) or a null
        // exception must NOT be audited at Minimal/ErrorsOnly — same rule as MemoryTaskStorage.
        var cancelled = await SeedSimpleTask();
        await _storage.SetCancelledByService(cancelled, new OperationCanceledException(), AuditLevel.Minimal);

        var nullEx = await SeedSimpleTask();
        await _storage.SetStatus(nullEx, QueuedTaskStatus.ServiceStopped, null, AuditLevel.ErrorsOnly);

        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == cancelled && s.NewStatus == QueuedTaskStatus.ServiceStopped)
            .ShouldBe(0);
        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == nullEx && s.NewStatus == QueuedTaskStatus.ServiceStopped)
            .ShouldBe(0);
    }

    [Fact]
    public async Task Should_audit_recovery_queued_transition_consistently()
    {
        // L43: the recovery Queued transition is audited at Full on every provider.
        var id = await SeedSimpleTask(QueuedTaskStatus.WaitingQueue);

        (await _storage.TrySetQueuedIfRecoverable(id, AuditLevel.Full)).ShouldBeTrue();

        _mockedDbContext.StatusAudit
            .Count(s => s.QueuedTaskId == id && s.NewStatus == QueuedTaskStatus.Queued)
            .ShouldBe(1);
    }

    [Fact]
    public async Task Should_stamp_executedat_consistently()
    {
        // L28: RunsAudit.ExecutedAt is stamped at the moment of the update, not the older LastExecutionUtc.
        var id = await SeedSimpleTask(QueuedTaskStatus.Completed, DateTimeOffset.UtcNow.AddHours(-1));

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await _storage.UpdateCurrentRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(5), AuditLevel.Full);

        var executedAt = _mockedDbContext.RunsAudit
            .Where(r => r.QueuedTaskId == id)
            .ToList()
            .Select(r => r.ExecutedAt)
            .ShouldHaveSingleItem();
        executedAt.ShouldBeGreaterThan(before);
    }

    #endregion

    #region Audit cleanup retention (M6 G5/G6)

    private QueuedTask CompletedTask(DateTimeOffset finishedAt, params TaskExecutionLog[] logs)
    {
        var id = GetGuidForProvider();
        foreach (var log in logs)
            log.TaskId = id;

        return new QueuedTask
        {
            Id               = id,
            CreatedAtUtc     = finishedAt,
            LastExecutionUtc = finishedAt,
            Type             = "CleanupTask",
            Request          = "{}",
            Handler          = "CleanupHandler",
            Status           = QueuedTaskStatus.Completed,
            ExecutionLogs    = [.. logs]
        };
    }

    [Fact]
    public async Task Should_not_delete_completed_task_within_retention_window()
    {
        // G5: a Completed non-recurring task with no audit rows must NOT be hard-deleted before it is
        // older than the retention window. Pre-fix the cleanup had no age gate and deleted it on the
        // very next cycle, losing the task record immediately.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 30,
            DeleteCompletedTasksAfterRetention = true
        };

        var recent = CompletedTask(now);                 // just finished → inside the 30-day window
        var aged   = CompletedTask(now.AddDays(-40));     // past the cutoff → legitimately deletable

        _mockedDbContext.QueuedTasks.AddRange(recent, aged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == recent.Id).ShouldBe(1, "recent task is within retention");
        _mockedDbContext.QueuedTasks.Count(x => x.Id == aged.Id).ShouldBe(0, "aged task past the cutoff is purged");
    }

    [Fact]
    public async Task Should_cascade_delete_execution_logs_when_purging_an_aged_task()
    {
        // Purging an aged-out completed task takes everything it owns with it, execution logs included —
        // that is what cleanup is for. The age gate (not a log guard) is what protects recent tasks.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 1,
            DeleteCompletedTasksAfterRetention = true
        };

        var logged = CompletedTask(now.AddDays(-10), new TaskExecutionLog
        {
            Id             = GetGuidForProvider(),
            TimestampUtc   = now.AddDays(-10),
            Level          = "Information",
            Message        = "execution log of an aged task",
            SequenceNumber = 0
        });

        _mockedDbContext.QueuedTasks.Add(logged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == logged.Id).ShouldBe(0, "aged task past the cutoff is purged");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == logged.Id).ShouldBe(0, "its logs cascade away with it");
    }

    [Fact]
    public async Task Should_not_delete_completed_tasks_when_no_retention_window_configured()
    {
        // G5 (core data-loss case): with the flag enabled but NO audit retention configured there is no
        // age cutoff, so an AuditLevel.None completed task (no audit rows) must be preserved — pre-fix it
        // was hard-deleted on the next cycle regardless of retention.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy { DeleteCompletedTasksAfterRetention = true };

        var task = CompletedTask(now.AddDays(-365)); // ancient, but no retention window means no cutoff

        _mockedDbContext.QueuedTasks.Add(task);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id).ShouldBe(1);
    }

    [Fact]
    public async Task Should_delete_completed_task_past_retention_window()
    {
        // Safety/regression: the legitimate cleanup must keep working — a Completed task with no audits
        // and no logs, older than the cutoff, is still purged after the fix.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 7,
            DeleteCompletedTasksAfterRetention = true
        };

        var aged = CompletedTask(now.AddDays(-30));

        _mockedDbContext.QueuedTasks.Add(aged);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == aged.Id).ShouldBe(0);
    }

    [Fact]
    public async Task Should_use_longest_retention_window_as_age_cutoff()
    {
        // The cutoff is the MAX of the configured retention windows, so a task is preserved while ANY of
        // its audit categories could still exist. Here success=5d but errors=30d → a 10-day-old task is
        // still within the (30-day) cutoff and must survive.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 5,
            RunsAuditRetentionDays             = 5,
            ErrorAuditRetentionDays            = 30,
            DeleteCompletedTasksAfterRetention = true
        };

        var task = CompletedTask(now.AddDays(-10));

        _mockedDbContext.QueuedTasks.Add(task);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id).ShouldBe(1);
    }

    #endregion

    #region Audit + execution-log retention (cross-provider; proves the SQLite client-side fix)

    private QueuedTask TaskWithStatusAudits(params StatusAudit[] audits)
    {
        var id = GetGuidForProvider();
        foreach (var a in audits)
            a.QueuedTaskId = id;

        return new QueuedTask
        {
            Id           = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Type         = "CleanupTask",
            Request      = "{}",
            Handler      = "CleanupHandler",
            Status       = QueuedTaskStatus.Completed,
            StatusAudits = [.. audits]
        };
    }

    private QueuedTask TaskWithRunsAudits(params RunsAudit[] audits)
    {
        var id = GetGuidForProvider();
        foreach (var a in audits)
            a.QueuedTaskId = id;

        return new QueuedTask
        {
            Id           = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Type         = "CleanupTask",
            Request      = "{}",
            Handler      = "CleanupHandler",
            Status       = QueuedTaskStatus.Completed,
            RunsAudits   = [.. audits]
        };
    }

    private static StatusAudit StatusAuditAt(DateTimeOffset at, string? exception = null) =>
        new() { UpdatedAtUtc = at, NewStatus = QueuedTaskStatus.Completed, Exception = exception };

    private static RunsAudit RunsAuditAt(DateTimeOffset at, string? exception = null) =>
        new() { ExecutedAt = at, Status = QueuedTaskStatus.Completed, Exception = exception };

    private TaskExecutionLog LogAt(DateTimeOffset at, int seq) =>
        new()
        {
            Id             = GetGuidForProvider(),
            TimestampUtc   = at,
            Level          = "Information",
            Message        = $"log {seq}",
            SequenceNumber = seq
        };

    /// <summary>
    /// Seeds tasks (with their audits/logs) and then clears the change tracker. The cleanup under test
    /// deletes rows via ExecuteDelete (which bypasses the tracker); detaching the seeded graph keeps the
    /// shared context consistent so the provider's tracker-based CleanUpDatabase doesn't try to
    /// cascade-delete rows that were already removed.
    /// </summary>
    private async Task PersistAndDetach(params QueuedTask[] tasks)
    {
        _mockedDbContext.QueuedTasks.AddRange(tasks);
        await _mockedDbContext.SaveChangesAsync(CancellationToken.None);
        if (_mockedDbContext is DbContext ef)
            ef.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Should_delete_status_audits_past_retention_and_keep_recent()
    {
        // SQLite-fix regression: the date comparison runs server-side on SQL Server / InMemory but cannot
        // be translated on SQLite, where the cleanup now resolves ids client-side. The same assertion runs
        // on every provider, so it proves the fix on a real SQLite database.
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithStatusAudits(StatusAuditAt(now.AddDays(-40)), StatusAuditAt(now.AddDays(-1)));

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 30 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.StatusAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(1, "the 40-day-old audit is past the 30-day window, the 1-day-old one survives");
        remaining[0].UpdatedAtUtc.ShouldBeGreaterThan(now.AddDays(-30));
    }

    [Fact]
    public async Task Should_keep_error_status_audits_longer_when_error_window_is_set()
    {
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithStatusAudits(
            StatusAuditAt(now.AddDays(-30)),                          // success, past 7d -> delete
            StatusAuditAt(now.AddDays(-30), exception: "boom"),       // error, within 90d -> keep
            StatusAuditAt(now.AddDays(-1)));                          // success, recent -> keep

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 7, ErrorAuditRetentionDays = 90 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.StatusAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(2);
        remaining.Count(x => !string.IsNullOrEmpty(x.Exception)).ShouldBe(1, "the error audit is kept for 90 days");
    }

    [Fact]
    public async Task Should_delete_runs_audits_past_retention_and_keep_recent()
    {
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithRunsAudits(RunsAuditAt(now.AddDays(-40)), RunsAuditAt(now.AddDays(-1)));

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { RunsAuditRetentionDays = 30 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.RunsAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(1);
        remaining[0].ExecutedAt.ShouldBeGreaterThan(now.AddDays(-30));
    }

    [Fact]
    public async Task Should_not_delete_audits_when_no_retention_configured()
    {
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithStatusAudits(StatusAuditAt(now.AddDays(-365)));

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy(); // no windows set
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == task.Id).ShouldBe(1);
    }

    [Fact]
    public async Task Should_delete_execution_logs_older_than_retention_window()
    {
        var now    = DateTimeOffset.UtcNow;
        var aged   = LogAt(now.AddDays(-10), 0);
        var recent = LogAt(now.AddDays(-1), 1);
        var task   = CompletedTask(now, aged, recent);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { ExecutionLogRetentionDays = 7 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.Id == aged.Id).ShouldBe(0, "the 10-day-old log is past the 7-day window");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.Id == recent.Id).ShouldBe(1, "the 1-day-old log survives");
    }

    [Fact]
    public async Task Should_cap_execution_logs_per_task_keeping_most_recent()
    {
        var now  = DateTimeOffset.UtcNow;
        var l0   = LogAt(now.AddMinutes(-50), 0); // oldest
        var l1   = LogAt(now.AddMinutes(-40), 1);
        var l2   = LogAt(now.AddMinutes(-30), 2);
        var l3   = LogAt(now.AddMinutes(-20), 3);
        var l4   = LogAt(now.AddMinutes(-10), 4); // newest
        var taskA = CompletedTask(now, l0, l1, l2, l3, l4);

        var bLog  = LogAt(now.AddMinutes(-5), 0);
        var taskB = CompletedTask(now, bLog);

        await PersistAndDetach(taskA, taskB);

        var policy = new AuditRetentionPolicy { MaxExecutionLogsPerTask = 2 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remainingA = _mockedDbContext.TaskExecutionLogs.Where(x => x.TaskId == taskA.Id).Select(x => x.Id).ToList();
        remainingA.Count.ShouldBe(2, "only the 2 most recent logs of task A are kept");
        remainingA.ShouldContain(l4.Id);
        remainingA.ShouldContain(l3.Id);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == taskB.Id).ShouldBe(1, "task B is under the cap and untouched");
    }

    [Fact]
    public async Task Should_apply_both_age_and_count_rules_to_execution_logs()
    {
        // age window = 7d, count cap = 2. Logs at -10 (aged), -5 (count-trimmed), -3 and -1 (kept).
        // Age alone would delete {-10}; count alone would delete {-10,-5}; the union deletes {-10,-5}.
        var now  = DateTimeOffset.UtcNow;
        var d10  = LogAt(now.AddDays(-10), 0);
        var d5   = LogAt(now.AddDays(-5), 1);
        var d3   = LogAt(now.AddDays(-3), 2);
        var d1   = LogAt(now.AddDays(-1), 3);
        var task = CompletedTask(now, d10, d5, d3, d1);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { ExecutionLogRetentionDays = 7, MaxExecutionLogsPerTask = 2 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.TaskExecutionLogs.Where(x => x.TaskId == task.Id).Select(x => x.Id).ToList();
        remaining.Count.ShouldBe(2);
        remaining.ShouldContain(d3.Id);
        remaining.ShouldContain(d1.Id);
    }

    [Fact]
    public async Task Should_not_delete_execution_logs_when_no_log_retention_configured()
    {
        var now  = DateTimeOffset.UtcNow;
        var old  = LogAt(now.AddDays(-365), 0);
        var task = CompletedTask(now, old);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 1 }; // audit window set, but no log retention
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.Id == old.Id).ShouldBe(1, "log retention is opt-in; null windows delete nothing");
    }

    [Fact]
    public async Task Should_keep_error_runs_audits_longer_when_error_window_is_set()
    {
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithRunsAudits(
            RunsAuditAt(now.AddDays(-30)),                          // success, past 7d -> delete
            RunsAuditAt(now.AddDays(-30), exception: "boom"),       // error, within 90d -> keep
            RunsAuditAt(now.AddDays(-1)));                          // success, recent -> keep

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { RunsAuditRetentionDays = 7, ErrorAuditRetentionDays = 90 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.RunsAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(2);
        remaining.Count(x => !string.IsNullOrEmpty(x.Exception)).ShouldBe(1, "the error runs-audit is kept for 90 days");
    }

    [Fact]
    public async Task Should_cap_execution_logs_across_multiple_tasks_and_batches()
    {
        // Exercises the multi-batch delete path (>100 ids per task) and the loop over several over-cap tasks.
        var now = DateTimeOffset.UtcNow;

        var aLogs = Enumerable.Range(0, 250).Select(i => LogAt(now.AddSeconds(i), i)).ToArray();
        var bLogs = Enumerable.Range(0, 120).Select(i => LogAt(now.AddSeconds(i), i)).ToArray();
        var taskA = CompletedTask(now, aLogs);
        var taskB = CompletedTask(now, bLogs);

        await PersistAndDetach(taskA, taskB);

        var policy = new AuditRetentionPolicy { MaxExecutionLogsPerTask = 5 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == taskA.Id).ShouldBe(5, "task A trimmed to the cap across batches");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == taskB.Id).ShouldBe(5, "task B trimmed to the cap across batches");
    }

    #endregion

    #region Retention knob validation — 0/negative = disabled (Cluster B)

    [Fact]
    public async Task Should_treat_zero_execution_log_retention_as_disabled()
    {
        // B: ExecutionLogRetentionDays = 0 makes the cutoff `now`, so every cycle deletes logs written
        // moments ago. 0 (and any <= 0) means "disabled": the pass must not run and nothing is deleted.
        var now    = DateTimeOffset.UtcNow;
        var recent = LogAt(now.AddMinutes(-1), 0);
        var task   = CompletedTask(now, recent);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { ExecutionLogRetentionDays = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.Id == recent.Id)
            .ShouldBe(1, "0 disables log-age retention; nothing is deleted");
    }

    [Fact]
    public async Task Should_treat_negative_execution_log_retention_as_disabled()
    {
        // B: ExecutionLogRetentionDays = -1 pushes the cutoff one day into the FUTURE, deleting even a
        // log just written. Negative is disabled, not a future cutoff.
        var now   = DateTimeOffset.UtcNow;
        var fresh = LogAt(now, 0);
        var task  = CompletedTask(now, fresh);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { ExecutionLogRetentionDays = -1 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.Id == fresh.Id)
            .ShouldBe(1, "negative retention is disabled, not a future cutoff");
    }

    [Fact]
    public async Task Should_treat_zero_max_execution_logs_per_task_as_disabled()
    {
        // B: MaxExecutionLogsPerTask = 0 slipped past the `< 0` guard and Skip(0) deleted EVERY log of
        // every task. 0 means "disabled": the cap does not run.
        var now  = DateTimeOffset.UtcNow;
        var l0   = LogAt(now.AddMinutes(-30), 0);
        var l1   = LogAt(now.AddMinutes(-20), 1);
        var l2   = LogAt(now.AddMinutes(-10), 2);
        var task = CompletedTask(now, l0, l1, l2);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { MaxExecutionLogsPerTask = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == task.Id)
            .ShouldBe(3, "0 disables the count cap; no logs are deleted");
    }

    [Fact]
    public async Task Should_treat_zero_status_audit_retention_as_disabled()
    {
        // B: StatusAuditRetentionDays = 0 makes the cutoff `now`, emptying the StatusAudit table every cycle.
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithStatusAudits(StatusAuditAt(now.AddMinutes(-1)), StatusAuditAt(now.AddDays(-1)));

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == task.Id)
            .ShouldBe(2, "0 disables status-audit retention; nothing is deleted");
    }

    [Fact]
    public async Task Should_treat_zero_runs_audit_retention_as_disabled()
    {
        // B (symmetry with status audits): RunsAuditRetentionDays = 0 must not empty the RunsAudit table.
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithRunsAudits(RunsAuditAt(now.AddMinutes(-1)), RunsAuditAt(now.AddDays(-1)));

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { RunsAuditRetentionDays = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.RunsAudit.Count(x => x.QueuedTaskId == task.Id)
            .ShouldBe(2, "0 disables runs-audit retention; nothing is deleted");
    }

    [Fact]
    public async Task Should_treat_zero_error_audit_retention_as_unset()
    {
        // B (error-cutoff path): ErrorAuditRetentionDays = 0 made the error cutoff `now`, deleting even
        // recent error audits. 0 means "no separate error window": errors fall back to the success window.
        var now  = DateTimeOffset.UtcNow;
        var task = TaskWithStatusAudits(StatusAuditAt(now.AddDays(-1), exception: "boom")); // recent error

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 7, ErrorAuditRetentionDays = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.StatusAudit.Count(x => x.QueuedTaskId == task.Id)
            .ShouldBe(1, "0 error window falls back to the 7-day success window; the 1-day-old error survives");
    }

    [Fact]
    public async Task Should_not_purge_completed_tasks_when_only_retention_window_is_zero()
    {
        // B: a single StatusAuditRetentionDays = 0 must not become a `now` cutoff that purges every aged
        // completed task. With 0 disabled there is no active window, so no completed task is purged.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 0,
            DeleteCompletedTasksAfterRetention = true
        };

        var aged = CompletedTask(now.AddDays(-365));

        await PersistAndDetach(aged);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == aged.Id)
            .ShouldBe(1, "0 disables the only retention window, so there is no age cutoff and nothing is purged");
    }

    [Fact]
    public async Task Should_treat_zero_occurrence_retention_as_disabled()
    {
        // B, for the occurrence window: 0 makes the cutoff `now`, so every cycle would prune an occurrence
        // that finished seconds ago — and, with a schedule running every minute, delete its whole history as
        // fast as it is written.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var fresh    = OccurrenceInState(schedule.Id, now.AddMinutes(-1), QueuedTaskStatus.Completed);

        await PersistAndDetach(schedule, fresh);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = 0 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == fresh.Id)
            .ShouldBe(1, "0 disables occurrence retention; nothing is pruned");
    }

    [Fact]
    public async Task Should_treat_negative_occurrence_retention_as_disabled()
    {
        // B: -1 pushes the cutoff a day into the FUTURE, which would take even an occurrence that has just
        // finished. Negative is disabled, not a future cutoff.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var fresh    = OccurrenceInState(schedule.Id, now, QueuedTaskStatus.Completed);

        await PersistAndDetach(schedule, fresh);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = -1 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == fresh.Id)
            .ShouldBe(1, "negative retention is disabled, not a future cutoff");
    }

    #endregion

    #region Occurrence retention — the knob's plumbing through the cleanup cycle (M16)

    [Fact]
    public async Task Should_prune_terminal_occurrences_past_the_configured_occurrence_window()
    {
        // The knob is what turns the pass on, and the window is what it prunes by: without both, a schedule
        // that has been running for a year keeps every slot it ever materialized.
        var now       = DateTimeOffset.UtcNow;
        var schedule  = DurableSchedule();
        var completed = OccurrenceInState(schedule.Id, now.AddDays(-30), QueuedTaskStatus.Completed);
        var failed    = OccurrenceInState(schedule.Id, now.AddDays(-29), QueuedTaskStatus.Failed);
        var cancelled = OccurrenceInState(schedule.Id, now.AddDays(-28), QueuedTaskStatus.Cancelled);
        var recent    = OccurrenceInState(schedule.Id, now.AddDays(-1), QueuedTaskStatus.Completed);
        var running   = OccurrenceInState(schedule.Id, now.AddDays(-27), QueuedTaskStatus.InProgress);

        await PersistAndDetach(schedule, completed, failed, cancelled, recent, running);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = 7 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var survivors = _mockedDbContext.QueuedTasks.Where(x => x.ParentTaskId == schedule.Id)
                                        .Select(x => x.Id).ToList();
        survivors.Count.ShouldBe(2);
        survivors.ShouldContain(recent.Id, "the 1-day-old occurrence is inside the 7-day window");
        survivors.ShouldContain(running.Id, "an occurrence still running is not terminal and is never pruned");

        _mockedDbContext.QueuedTasks.Count(x => x.Id == schedule.Id)
            .ShouldBe(1, "the schedule row is recurring: this pass never touches it");
    }

    [Fact]
    public async Task Should_not_prune_occurrences_when_no_occurrence_retention_is_configured()
    {
        // null is the default, and it must leave the pass out of the cycle entirely — even a cycle that is
        // otherwise doing real work. Occurrence retention is opt-in, like every other window here.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var ancient  = OccurrenceInState(schedule.Id, now.AddDays(-365), QueuedTaskStatus.Completed);

        await PersistAndDetach(schedule, ancient);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 1 }; // an active cycle, no occurrence window
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == ancient.Id)
            .ShouldBe(1, "a null window keeps occurrences forever, however old");
    }

    [Fact]
    public async Task Should_preserve_an_occurrence_whose_logs_the_log_window_kept()
    {
        // R15, through the policy rather than through the raw pass: the log-retention flag the cycle hands to
        // CleanupTerminalOccurrences is DERIVED from the policy, and the occurrence window is typically far
        // shorter than the log one. Deleting the row cascades its logs, so an occurrence that still owns logs
        // the log passes deliberately kept must survive its own window.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var withLogs = OccurrenceInState(schedule.Id, now.AddDays(-30), QueuedTaskStatus.Completed,
            LogAt(now.AddDays(-10), 0));
        var noLogs   = OccurrenceInState(schedule.Id, now.AddDays(-29), QueuedTaskStatus.Completed);

        await PersistAndDetach(schedule, withLogs, noLogs);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = 7, ExecutionLogRetentionDays = 90 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == withLogs.Id)
            .ShouldBe(1, "the occurrence still owns a log 80 days short of its own window");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == withLogs.Id).ShouldBe(1);
        _mockedDbContext.QueuedTasks.Count(x => x.Id == noLogs.Id)
            .ShouldBe(0, "an occurrence with nothing to protect is pruned by the same cycle");
    }

    [Fact]
    public async Task Should_preserve_an_occurrence_whose_logs_the_count_cap_kept()
    {
        // Same guard, reached through the other half of the flag: a cap alone, with no time window at all,
        // still counts as an active log retention.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var withLogs = OccurrenceInState(schedule.Id, now.AddDays(-30), QueuedTaskStatus.Completed,
            LogAt(now.AddDays(-10), 0), LogAt(now.AddDays(-9), 1));

        await PersistAndDetach(schedule, withLogs);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = 7, MaxExecutionLogsPerTask = 10 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == withLogs.Id)
            .ShouldBe(1, "both logs are under the cap, so the cap kept them and they protect the row");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == withLogs.Id).ShouldBe(2);
    }

    [Fact]
    public async Task Should_prune_an_occurrence_with_logs_when_no_log_retention_is_active()
    {
        // The complement that makes the two tests above about the FLAG and not about the rows: identical
        // seeding, a policy with no log retention, opposite outcome. With nothing configured to keep them,
        // the historic cascade-on-purge behaviour stands.
        var now      = DateTimeOffset.UtcNow;
        var schedule = DurableSchedule();
        var withLogs = OccurrenceInState(schedule.Id, now.AddDays(-30), QueuedTaskStatus.Completed,
            LogAt(now.AddDays(-10), 0));

        await PersistAndDetach(schedule, withLogs);

        var policy = new AuditRetentionPolicy { OccurrenceRetentionDays = 7 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == withLogs.Id)
            .ShouldBe(0, "no log retention is active, so no log is protecting the row");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == withLogs.Id)
            .ShouldBe(0, "the delete cascades to the logs it owned");
    }

    /// <summary>A durable schedule row: recurring, with a live cursor, and never pruned by the retention pass.</summary>
    private QueuedTask DurableSchedule() => new()
    {
        Id           = GetGuidForProvider(),
        CreatedAtUtc = FloorToMicroseconds(DateTimeOffset.UtcNow.AddDays(-365)),
        Type         = "DurableSchedule",
        Request      = "{}",
        Handler      = "DurableHandler",
        Status       = QueuedTaskStatus.Queued,
        IsRecurring  = true,
        NextRunUtc   = FloorToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(5)),
        QueueName    = "recurring"
    };

    /// <summary>
    /// One occurrence of <paramref name="scheduleId"/>, dated by <paramref name="finishedAt"/> — which is both
    /// its nominal slot and its <c>LastExecutionUtc</c>, the column the age gate reads. Two occurrences of the
    /// same schedule therefore need two different <paramref name="finishedAt"/> values, or the unique index on
    /// (parent, slot) refuses the second.
    /// </summary>
    private QueuedTask OccurrenceInState(Guid scheduleId, DateTimeOffset finishedAt, QueuedTaskStatus status,
                                         params TaskExecutionLog[] logs)
    {
        var id = GetGuidForProvider();
        foreach (var log in logs)
            log.TaskId = id;

        return new QueuedTask
        {
            Id                    = id,
            CreatedAtUtc          = FloorToMicroseconds(finishedAt),
            LastExecutionUtc      = FloorToMicroseconds(finishedAt),
            ScheduledExecutionUtc = FloorToMicroseconds(finishedAt),
            Type                  = "DurableSchedule",
            Request               = "{}",
            Handler               = "DurableHandler",
            Status                = status,
            ParentTaskId          = scheduleId,
            QueueName             = "recurring",
            ExecutionLogs         = [.. logs]
        };
    }

    #endregion

    #region Completed-task purge respects log retention (Cluster A, P0)

    [Fact]
    public async Task Should_preserve_completed_task_with_logs_inside_the_log_retention_window()
    {
        // P0 (Cluster A): the completed-task purge used the MAX of the AUDIT windows as its cutoff and
        // ignored the log windows, so a task aged past the (short) audit window was hard-deleted —
        // cascading away logs that the (longer) ExecutionLogRetentionDays window was meant to keep. With a
        // log window configured, a task that still has surviving logs must NOT be purged.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 7,
            ExecutionLogRetentionDays          = 90,
            DeleteCompletedTasksAfterRetention = true
        };

        // Completed 8 days ago (past the 7-day audit window), no audits, logs from -8d..-1d (all within 90d).
        var logs = new[] { LogAt(now.AddDays(-8), 0), LogAt(now.AddDays(-5), 1), LogAt(now.AddDays(-1), 2) };
        var task = CompletedTask(now.AddDays(-8), logs);

        await PersistAndDetach(task);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id)
            .ShouldBe(1, "the task still owns logs inside the 90-day window");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == task.Id)
            .ShouldBe(3, "all logs within the 90-day window survive");
    }

    [Fact]
    public async Task Should_preserve_completed_task_with_logs_protected_by_the_count_cap()
    {
        // P0 (Cluster A), count-cap variant: no time window need be set — if a count cap is configured and
        // the task still has logs the cap chose to keep, the task must not be purged (cascading them away).
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 7,
            MaxExecutionLogsPerTask            = 10,
            DeleteCompletedTasksAfterRetention = true
        };

        // 5 logs, under the cap of 10, so the cap deletes nothing → all 5 survive and must protect the task.
        var logs = Enumerable.Range(0, 5).Select(i => LogAt(now.AddDays(-8).AddMinutes(i), i)).ToArray();
        var task = CompletedTask(now.AddDays(-8), logs);

        await PersistAndDetach(task);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id)
            .ShouldBe(1, "the task still owns logs the count cap kept");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == task.Id)
            .ShouldBe(5, "logs under the cap survive");
    }

    [Fact]
    public async Task Should_purge_completed_task_after_its_logs_age_out_of_the_window()
    {
        // Complement to the P0 fix: once the log-age pass has removed all of a task's logs (they fell
        // outside ExecutionLogRetentionDays), the task has no surviving logs, so the purge proceeds and
        // removes the now-empty completed task. The guard preserves tasks with logs, it does not freeze them.
        var now    = DateTimeOffset.UtcNow;
        var policy = new AuditRetentionPolicy
        {
            StatusAuditRetentionDays           = 7,
            ExecutionLogRetentionDays          = 5,
            DeleteCompletedTasksAfterRetention = true
        };

        // Completed 30 days ago, single log also 30 days old → outside the 5-day window → deleted first,
        // leaving the task with no logs → purged.
        var task = CompletedTask(now.AddDays(-30), LogAt(now.AddDays(-30), 0));

        await PersistAndDetach(task);

        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        _mockedDbContext.QueuedTasks.Count(x => x.Id == task.Id)
            .ShouldBe(0, "no surviving logs and past the window → purged");
        _mockedDbContext.TaskExecutionLogs.Count(x => x.TaskId == task.Id).ShouldBe(0);
    }

    #endregion

    #region Count-cap total order — deterministic survivor on ties (Cluster C)

    [Fact]
    public async Task Should_keep_a_deterministic_survivor_when_capping_logs_with_identical_timestamp_and_sequence()
    {
        // Cluster C: with two logs sharing the exact (TimestampUtc, SequenceNumber), the count cap had no
        // total order, so the base (SQL OFFSET) and the SQLite (in-memory Skip) paths could keep DIFFERENT
        // rows. The Id tie-breaker (UUIDv7, the same key the read path orders by) makes the survivor
        // deterministic and equal to the log the reader ranks most-recent.
        var now = DateTimeOffset.UtcNow;
        var ts  = now.AddMinutes(-10);

        var a = new TaskExecutionLog { Id = GetGuidForProvider(), TimestampUtc = ts, Level = "Information", Message = "tie a", SequenceNumber = 7 };
        var b = new TaskExecutionLog { Id = GetGuidForProvider(), TimestampUtc = ts, Level = "Information", Message = "tie b", SequenceNumber = 7 };
        var task = CompletedTask(now, a, b);

        await PersistAndDetach(task);

        // The reader's "most recent" (tail of the GetExecutionLogsQuery order) is the survivor the cap must
        // keep. Deriving it from the real read path makes the assertion provider-native, not hard-coded.
        var readOrder = await _storage.GetExecutionLogsAsync(task.Id, CancellationToken.None);
        readOrder.Count.ShouldBe(2);
        var expectedSurvivor = readOrder[^1].Id;

        var policy = new AuditRetentionPolicy { MaxExecutionLogsPerTask = 1 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.TaskExecutionLogs.Where(x => x.TaskId == task.Id).Select(x => x.Id).ToList();
        remaining.Count.ShouldBe(1, "the count cap keeps exactly one");
        remaining[0].ShouldBe(expectedSurvivor, "the survivor is deterministic and matches the read order's most-recent log");
    }

    #endregion

    #region Batched age-based deletes — correct across many batches (Blocco 5)

    [Fact]
    public async Task Should_delete_status_audits_across_multiple_batches()
    {
        // Blocco 5 (perf/robustness): the base age-based deletes must batch, not run as a single unbounded
        // ExecuteDelete that can escalate to a table lock. Correctness gate: seeding far more rows than one
        // batch and asserting they are ALL deleted proves the batched loop iterates and stays correct.
        var now = DateTimeOffset.UtcNow;

        var audits = Enumerable.Range(0, 250).Select(_ => StatusAuditAt(now.AddDays(-40)))
            .Append(StatusAuditAt(now.AddDays(-1)))
            .ToArray();
        var task = TaskWithStatusAudits(audits);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { StatusAuditRetentionDays = 30 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.StatusAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(1, "all 250 aged audits are deleted across batches; the recent one survives");
        remaining[0].UpdatedAtUtc.ShouldBeGreaterThan(now.AddDays(-30));
    }

    [Fact]
    public async Task Should_delete_runs_audits_across_multiple_batches()
    {
        var now = DateTimeOffset.UtcNow;

        var audits = Enumerable.Range(0, 250).Select(_ => RunsAuditAt(now.AddDays(-40)))
            .Append(RunsAuditAt(now.AddDays(-1)))
            .ToArray();
        var task = TaskWithRunsAudits(audits);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { RunsAuditRetentionDays = 30 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.RunsAudit.Where(x => x.QueuedTaskId == task.Id).ToList();
        remaining.Count.ShouldBe(1, "all 250 aged runs audits are deleted across batches; the recent one survives");
        remaining[0].ExecutedAt.ShouldBeGreaterThan(now.AddDays(-30));
    }

    [Fact]
    public async Task Should_delete_execution_logs_by_age_across_multiple_batches()
    {
        var now = DateTimeOffset.UtcNow;

        var aged   = Enumerable.Range(0, 250).Select(i => LogAt(now.AddDays(-40), i)).ToArray();
        var recent = LogAt(now.AddDays(-1), 250);
        var task   = CompletedTask(now, [.. aged, recent]);

        await PersistAndDetach(task);

        var policy = new AuditRetentionPolicy { ExecutionLogRetentionDays = 30 };
        await AuditCleanupHostedService.RunCleanupAsync((EfCoreTaskStorage)_storage, policy, now, CancellationToken.None);

        var remaining = _mockedDbContext.TaskExecutionLogs.Where(x => x.TaskId == task.Id).ToList();
        remaining.Count.ShouldBe(1, "all 250 aged logs are deleted across batches; the recent one survives");
        remaining[0].Id.ShouldBe(recent.Id);
    }

    #endregion

    #region TaskKey / UpdateTask / Remove

    [Fact]
    public async Task Should_GetByTaskKey_return_matching_task_and_null_when_absent()
    {
        var taskKey = $"key-{Guid.NewGuid():N}";
        var task = NewTask(QueuedTaskStatus.Queued, DateTimeOffset.UtcNow, taskKey: taskKey);
        await _storage.Persist(task);

        var found = await _storage.GetByTaskKey(taskKey);
        found.ShouldNotBeNull();
        found!.Id.ShouldBe(task.Id);
        found.TaskKey.ShouldBe(taskKey);

        (await _storage.GetByTaskKey($"missing-{Guid.NewGuid():N}")).ShouldBeNull();
    }

    [Fact]
    public async Task Should_UpdateTask_persist_mutable_fields()
    {
        var task = NewTask(QueuedTaskStatus.Queued, DateTimeOffset.UtcNow);
        await _storage.Persist(task);

        var newSchedule = DateTimeOffset.UtcNow.AddDays(2);
        var newRunUntil = DateTimeOffset.UtcNow.AddDays(10);
        var newNextRun  = DateTimeOffset.UtcNow.AddHours(3);

        var updated = new QueuedTask
        {
            Id                    = task.Id,
            Type                  = "UpdatedType",
            Request               = "{\"x\":1}",
            Handler               = "UpdatedHandler",
            ScheduledExecutionUtc = newSchedule,
            IsRecurring           = true,
            RecurringTask         = "recurring-task",
            RecurringInfo         = "recurring-info",
            MaxRuns               = 5,
            RunUntil              = newRunUntil,
            NextRunUtc            = newNextRun,
            QueueName             = "queue-1",
            TaskKey               = "task-key-1"
        };

        await _storage.UpdateTask(updated);

        var result = await _storage.Get(x => x.Id == task.Id);
        result.Length.ShouldBe(1);
        var r = result[0];
        r.Type.ShouldBe("UpdatedType");
        r.Request.ShouldBe("{\"x\":1}");
        r.Handler.ShouldBe("UpdatedHandler");
        r.IsRecurring.ShouldBeTrue();
        r.RecurringTask.ShouldBe("recurring-task");
        r.RecurringInfo.ShouldBe("recurring-info");
        r.MaxRuns.ShouldBe(5);
        r.QueueName.ShouldBe("queue-1");
        r.TaskKey.ShouldBe("task-key-1");
        // DateTimeOffset round-trips with provider-specific precision (SQLite stores ISO-8601 text) -> tolerance.
        r.ScheduledExecutionUtc!.Value.ShouldBe(newSchedule, TimeSpan.FromSeconds(1));
        r.RunUntil!.Value.ShouldBe(newRunUntil, TimeSpan.FromSeconds(1));
        r.NextRunUtc!.Value.ShouldBe(newNextRun, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Should_UpdateTask_be_noop_when_task_absent()
    {
        var ghost = NewTask(QueuedTaskStatus.Queued, DateTimeOffset.UtcNow);
        // never persisted: the bulk UPDATE matches no row and must not throw
        await Should.NotThrowAsync(async () => await _storage.UpdateTask(ghost));
        (await _storage.Get(x => x.Id == ghost.Id)).Length.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Remove_delete_task()
    {
        var task = NewTask(QueuedTaskStatus.Completed, DateTimeOffset.UtcNow);
        await _storage.Persist(task);
        (await _storage.Get(x => x.Id == task.Id)).Length.ShouldBe(1);

        await _storage.Remove(task.Id);
        (await _storage.Get(x => x.Id == task.Id)).Length.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Remove_be_noop_when_task_absent()
    {
        await Should.NotThrowAsync(async () => await _storage.Remove(GetGuidForProvider()));
    }

    #endregion

    #region Statistics (CountByStatus / CountByQueueAndStatus)

    [Fact]
    public async Task Should_CountByStatusAsync_group_counts_by_status()
    {
        // Delta against the pre-existing counts: robust to any rows other tests may have left behind.
        var before = await ((ITaskStorageStatistics)_storage).CountByStatusAsync();

        var now = DateTimeOffset.UtcNow;
        await _storage.Persist(NewTask(QueuedTaskStatus.Pending, now));
        await _storage.Persist(NewTask(QueuedTaskStatus.Pending, now));
        await _storage.Persist(NewTask(QueuedTaskStatus.Failed, now));

        var after = await ((ITaskStorageStatistics)_storage).CountByStatusAsync();
        (after.GetValueOrDefault(QueuedTaskStatus.Pending) - before.GetValueOrDefault(QueuedTaskStatus.Pending))
            .ShouldBe(2);
        (after.GetValueOrDefault(QueuedTaskStatus.Failed) - before.GetValueOrDefault(QueuedTaskStatus.Failed))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Should_CountByStatusAsync_apply_createdAt_filter()
    {
        // Anchor far in the future so ONLY these rows satisfy the filter, regardless of leftovers.
        var future = DateTimeOffset.UtcNow.AddYears(50);
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, future));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, future.AddMinutes(1)));

        var counts = await ((ITaskStorageStatistics)_storage).CountByStatusAsync(future);
        counts.Values.Sum().ShouldBe(2);
        counts.GetValueOrDefault(QueuedTaskStatus.Queued).ShouldBe(2);
    }

    [Fact]
    public async Task Should_CountByStatusAsync_accept_non_utc_offset_filter()
    {
        // R4: the public statistics take a caller-supplied DateTimeOffset. Npgsql requires offset 0 for
        // timestamptz, so a +02:00 value (e.g. DateTimeOffset.Now) would throw at the DB boundary unless the
        // base normalizes it with .ToUniversalTime(). This must NOT throw and must count correctly on every
        // provider (no-op for SQL Server / SQLite which tolerate the offset).
        var futureUtc      = DateTimeOffset.UtcNow.AddYears(60);
        var futureWithOffset = futureUtc.ToOffset(TimeSpan.FromHours(2)); // same instant, offset +02:00

        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, futureUtc.AddMinutes(1)));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, futureUtc.AddMinutes(2)));

        IReadOnlyDictionary<QueuedTaskStatus, int> counts = null!;
        await Should.NotThrowAsync(async () =>
            counts = await ((ITaskStorageStatistics)_storage).CountByStatusAsync(futureWithOffset));

        counts.GetValueOrDefault(QueuedTaskStatus.Queued).ShouldBe(2,
            "a non-UTC offset filter must be normalized and count the same rows as its UTC equivalent");
    }

    [Fact]
    public async Task Should_CountByQueueAndStatusAsync_group_counts_by_queue_and_status()
    {
        var future = DateTimeOffset.UtcNow.AddYears(50);
        var queueA = $"queue-a-{Guid.NewGuid():N}";
        var queueB = $"queue-b-{Guid.NewGuid():N}";

        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, future, queueA));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, future, queueA));
        await _storage.Persist(NewTask(QueuedTaskStatus.InProgress, future, queueB));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, future, queueName: null)); // null queue -> ""

        var result = await ((ITaskStorageStatistics)_storage).CountByQueueAndStatusAsync(future);

        result[queueA][QueuedTaskStatus.Queued].ShouldBe(2);
        result[queueB][QueuedTaskStatus.InProgress].ShouldBe(1);
        result[""][QueuedTaskStatus.Queued].ShouldBe(1);
    }

    #endregion

    private QueuedTask NewTask(QueuedTaskStatus status, DateTimeOffset createdAt,
                               string? queueName = null, string? taskKey = null) => new()
    {
        Id           = GetGuidForProvider(),
        CreatedAtUtc = createdAt,
        Type         = "StatType",
        Request      = "{}",
        Handler      = "StatHandler",
        Status       = status,
        QueueName    = queueName,
        TaskKey      = taskKey
    };

    protected abstract Task CleanUpDatabase();
}
