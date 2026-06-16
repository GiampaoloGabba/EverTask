using EverTask.Tests.TestHelpers;
using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Coverage for MemoryTaskStorage's own implementation of the ITaskStorage / ITaskStorageStatistics
/// surface that the EF Core suite (EfCoreTaskStorageTestsBase) cannot reach: Memory is the no-persistence
/// provider (for tests / dev), it does NOT inherit EfCoreTaskStorage, so GetByTaskKey / UpdateTask / Remove
/// / CountByStatusAsync / CountByQueueAndStatusAsync are a separate implementation that needs its own tests.
/// Mirrors the cross-provider EF Core tests so the in-memory provider stays at parity.
/// </summary>
public class MemoryTaskStorageTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private static QueuedTask NewTask(QueuedTaskStatus status, DateTimeOffset createdAt,
                                      string? queueName = null, string? taskKey = null) => new()
    {
        Id           = TestGuidGenerator.New(),
        CreatedAtUtc = createdAt,
        Type         = "StatType",
        Request      = "{}",
        Handler      = "StatHandler",
        Status       = status,
        QueueName    = queueName,
        TaskKey      = taskKey
    };

    [Fact]
    public async Task Should_GetByTaskKey_return_matching_task_and_null_when_absent()
    {
        var taskKey = $"key-{Guid.NewGuid():N}";
        var task = NewTask(QueuedTaskStatus.Queued, DateTimeOffset.UtcNow, taskKey: taskKey);
        await _storage.Persist(task);

        var found = await _storage.GetByTaskKey(taskKey);
        found.ShouldNotBeNull();
        found!.Id.ShouldBe(task.Id);

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

        await _storage.UpdateTask(new QueuedTask
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
        });

        var r = (await _storage.Get(x => x.Id == task.Id))[0];
        r.Type.ShouldBe("UpdatedType");
        r.Request.ShouldBe("{\"x\":1}");
        r.Handler.ShouldBe("UpdatedHandler");
        r.IsRecurring.ShouldBeTrue();
        r.RecurringTask.ShouldBe("recurring-task");
        r.RecurringInfo.ShouldBe("recurring-info");
        r.MaxRuns.ShouldBe(5);
        r.QueueName.ShouldBe("queue-1");
        r.TaskKey.ShouldBe("task-key-1");
        // Memory keeps the exact instances, so DateTimeOffset round-trips without precision loss.
        r.ScheduledExecutionUtc.ShouldBe(newSchedule);
        r.RunUntil.ShouldBe(newRunUntil);
        r.NextRunUtc.ShouldBe(newNextRun);
    }

    [Fact]
    public async Task Should_UpdateTask_be_noop_when_task_absent()
    {
        var ghost = NewTask(QueuedTaskStatus.Queued, DateTimeOffset.UtcNow);
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
        await Should.NotThrowAsync(async () => await _storage.Remove(TestGuidGenerator.New()));
    }

    [Fact]
    public async Task Should_CountByStatusAsync_group_and_filter()
    {
        var now = DateTimeOffset.UtcNow;
        await _storage.Persist(NewTask(QueuedTaskStatus.Pending, now));
        await _storage.Persist(NewTask(QueuedTaskStatus.Pending, now));
        await _storage.Persist(NewTask(QueuedTaskStatus.Failed, now));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, now.AddDays(-2))); // excluded by the filter below

        var all = await _storage.CountByStatusAsync();
        all[QueuedTaskStatus.Pending].ShouldBe(2);
        all[QueuedTaskStatus.Failed].ShouldBe(1);
        all[QueuedTaskStatus.Queued].ShouldBe(1);

        var filtered = await _storage.CountByStatusAsync(now.AddDays(-1));
        filtered[QueuedTaskStatus.Pending].ShouldBe(2);
        filtered[QueuedTaskStatus.Failed].ShouldBe(1);
        filtered.ContainsKey(QueuedTaskStatus.Queued).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_CountByQueueAndStatusAsync_group_by_queue_and_status()
    {
        var now = DateTimeOffset.UtcNow;
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, now, "queue-a"));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, now, "queue-a"));
        await _storage.Persist(NewTask(QueuedTaskStatus.InProgress, now, "queue-b"));
        await _storage.Persist(NewTask(QueuedTaskStatus.Queued, now, queueName: null)); // null -> ""

        var result = await _storage.CountByQueueAndStatusAsync();
        result["queue-a"][QueuedTaskStatus.Queued].ShouldBe(2);
        result["queue-b"][QueuedTaskStatus.InProgress].ShouldBe(1);
        result[""][QueuedTaskStatus.Queued].ShouldBe(1);
    }

    // CurrentRunCount saturates at int.MaxValue instead of overflowing (parity with the EF Core providers).
    [Theory]
    [InlineData(AuditLevel.None)]
    [InlineData(AuditLevel.Full)]
    public async Task UpdateCurrentRun_saturates_run_counter_at_int_max(AuditLevel auditLevel)
    {
        var id = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id = id, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, MaxRuns = null, CurrentRunCount = int.MaxValue
        });

        await Should.NotThrowAsync(() => _storage.UpdateCurrentRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(1), auditLevel));

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.CurrentRunCount.ShouldBe(int.MaxValue, "the run counter must saturate at int.MaxValue, never wrap");
    }

    [Fact]
    public async Task CompleteRecurringRun_saturates_run_counter_at_int_max()
    {
        var id = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id = id, Type = "T", Request = "{}", Handler = "H",
            CreatedAtUtc = DateTimeOffset.UtcNow, Status = QueuedTaskStatus.InProgress,
            IsRecurring = true, MaxRuns = null, CurrentRunCount = int.MaxValue
        });

        await Should.NotThrowAsync(() => _storage.CompleteRecurringRun(id, 10.0, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full));

        var row = (await _storage.Get(x => x.Id == id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.CurrentRunCount.ShouldBe(int.MaxValue, "the run counter must saturate at int.MaxValue, never wrap");
    }
}
