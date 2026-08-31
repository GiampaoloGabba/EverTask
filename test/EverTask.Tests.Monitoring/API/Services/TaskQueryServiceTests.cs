using System.Linq.Expressions;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.API.Services;

public class TaskQueryServiceTests
{
    private readonly Mock<ITaskStorage> _storageMock;
    private readonly TaskQueryService _service;

    public TaskQueryServiceTests()
    {
        _storageMock = new Mock<ITaskStorage>();

        // A storage never answers null here: a store with no recorded start answers an empty map. The tests
        // that are ABOUT the recorded start override this.
        _storageMock
            .Setup(s => s.GetLastRunStarts(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset>());

        // Same rule for the two audit trails: a row with no history answers an empty page, never null. The
        // tests that are ABOUT the trails override these.
        _storageMock
            .Setup(s => s.GetStatusAuditsPage(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditPage<StatusAudit>([], 0));

        _storageMock
            .Setup(s => s.GetRunsAuditsPage(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditPage<RunsAudit>([], 0));

        _service = new TaskQueryService(_storageMock.Object);
    }

    [Fact]
    public async Task Should_filter_and_paginate_tasks()
    {
        // Arrange
        var tasks = CreateSampleTasks(20);
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. tasks]);

        var filter     = new TaskFilter { Statuses   = [QueuedTaskStatus.Completed] };
        var pagination = new PaginationParams { Page = 1, PageSize = 5 };

        // Act
        var result = await _service.GetTasksAsync(filter, pagination);

        // Assert
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBeLessThanOrEqualTo(5);
        result.Page.ShouldBe(1);
        result.PageSize.ShouldBe(5);
    }

    [Fact]
    public async Task Should_return_task_detail_with_audits()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = CreateTaskWithAudits(taskId);
        _storageMock.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([task]);

        // Act
        var result = await _service.GetTaskDetailAsync(taskId);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(taskId);
        result.Type.ShouldNotBeNullOrEmpty();
        result.Handler.ShouldNotBeNullOrEmpty();
        result.Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_read_schedule_facts_without_resolving_named_calendars()
    {
        var row = CreateTaskWithAudits(Guid.NewGuid());
        row.IsRecurring = true;
        row.RecurringTask = JsonSerializer.Serialize(new RecurringTask
        {
            DayInterval = new DayInterval(1),
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions { Calendars = ["holidays"] }
        });
        _storageMock.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([row]);

        var detail = await _service.GetTaskDetailAsync(row.Id);

        detail.ShouldNotBeNull().TimeZoneId.ShouldBe("Europe/Rome");
        detail.OccurrenceMode.ShouldBe(OccurrenceMode.Inline);
    }

    [Fact]
    public async Task Should_read_the_audit_trail_from_the_storage_and_never_off_the_row()
    {
        // The navigation on the row a read hands back is empty on every relational provider — nothing
        // Includes it — so the endpoints that walked it answered an empty history for a task whose audit
        // tables hold every transition and every run it ever made.
        var taskId = Guid.NewGuid();
        var row    = CreateTaskWithAudits(taskId);

        row.StatusAudits.Clear();
        row.RunsAudits.Clear();

        _storageMock.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([row]);

        _storageMock.Setup(s => s.GetStatusAuditsPage(taskId, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditPage<StatusAudit>([
                new StatusAudit { Id = 3, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Completed },
                new StatusAudit { Id = 2, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.InProgress },
                new StatusAudit { Id = 1, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Queued }
            ], 3));

        _storageMock.Setup(s => s.GetRunsAuditsPage(taskId, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditPage<RunsAudit>([
                new RunsAudit { Id = 2, QueuedTaskId = taskId, ExecutionTimeMs = 22 },
                new RunsAudit { Id = 1, QueuedTaskId = taskId, ExecutionTimeMs = 11 }
            ], 2));

        (await _service.GetStatusAuditAsync(taskId)).Audits.Select(a => a.NewStatus)
            .ShouldBe([QueuedTaskStatus.Completed, QueuedTaskStatus.InProgress, QueuedTaskStatus.Queued]);

        (await _service.GetRunsAuditAsync(taskId)).Audits.Select(a => a.ExecutionTimeMs).ShouldBe([22d, 11d]);

        var detail = await _service.GetTaskDetailAsync(taskId);

        detail.ShouldNotBeNull();
        detail.StatusAudits.Count.ShouldBe(3, "the detail's two blocks read the same source as the two endpoints");
        detail.RunsAudits.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Should_answer_an_empty_audit_history_for_a_task_that_does_not_exist()
    {
        var taskId = Guid.NewGuid();

        _storageMock.Setup(s => s.GetStatusAuditsPage(taskId, It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new AuditPage<StatusAudit>([], 0));
        _storageMock.Setup(s => s.GetRunsAuditsPage(taskId, It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new AuditPage<RunsAudit>([], 0));

        var statusPage = await _service.GetStatusAuditAsync(taskId);
        var runsPage   = await _service.GetRunsAuditAsync(taskId);

        statusPage.Audits.ShouldBeEmpty();
        statusPage.TotalCount.ShouldBe(0);
        runsPage.Audits.ShouldBeEmpty();
        runsPage.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Should_handle_empty_results()
    {
        // Arrange
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var filter = new TaskFilter();
        var pagination = new PaginationParams { Page = 1, PageSize = 10 };

        // Act
        var result = await _service.GetTasksAsync(filter, pagination);

        // Assert
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(0);
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Should_return_null_when_task_not_found()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _storageMock.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        var result = await _service.GetTaskDetailAsync(taskId);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Should_ask_the_storage_for_one_page_of_occurrences_and_never_for_the_series()
    {
        // The paging belongs to the storage: a schedule with a year of retention behind it holds hundreds of
        // thousands of occurrence rows, and reading them all to return a hundred is the cost this endpoint
        // must not have, whatever the page size asked for.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 10, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 4_312));

        var result = await _service.GetOccurrencesAsync(scheduleId, nonTerminalOnly: false, skip: 10, take: 25);

        result.TotalCount.ShouldBe(4_312, "the total comes from the storage count, not from what was read");
        result.Occurrences.ShouldHaveSingleItem().Id.ShouldBe(row.Id);

        _storageMock.Verify(
            s => s.GetOccurrences(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the unpaged read is the one that materializes the whole series");
    }

    [Fact]
    public async Task Should_cap_storage_backed_pages_at_500_items()
    {
        var scheduleId = Guid.NewGuid();

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([], 0));

        var result = await _service.GetOccurrencesAsync(scheduleId, take: int.MaxValue);

        result.Take.ShouldBe(500);
        _storageMock.Verify(
            s => s.GetOccurrencesPage(scheduleId, false, 0, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_cap_execution_log_pages_at_500_items()
    {
        var taskId = Guid.NewGuid();

        _storageMock.Setup(s => s.GetExecutionLogsAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _service.GetExecutionLogsAsync(taskId, take: int.MaxValue);

        result.Take.ShouldBe(500);
    }

    [Fact]
    public async Task Should_derive_when_a_run_started_from_its_end_and_its_duration_when_the_row_has_no_audit()
    {
        // Nothing recorded the transition (any audit level below Full), so the start has to come from the two
        // columns every row has. It is never lastExecutionUtc itself: that one is stamped when the run ENDED.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);

        row.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        row.ExecutionTimeMs  = 90_000;

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        var result = await _service.GetOccurrencesAsync(scheduleId);

        var occurrence = result.Occurrences.ShouldHaveSingleItem();
        occurrence.StartedAtUtc.ShouldBe(row.LastExecutionUtc.Value.AddMinutes(-1.5));
    }

    [Fact]
    public async Task Should_report_the_recorded_start_of_a_run_that_is_still_in_flight()
    {
        // A row that is RUNNING has written no LastExecutionUtc — that column is stamped on the terminal
        // transition — so the only term a lateness badge can use is the recorded start, which the service
        // asks the storage for.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);
        var started    = DateTimeOffset.UtcNow.AddMinutes(-45);

        row.Status           = QueuedTaskStatus.InProgress;
        row.LastExecutionUtc = null;

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        _storageMock
            .Setup(s => s.GetLastRunStarts(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(row.Id)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [row.Id] = started });

        var result = await _service.GetOccurrencesAsync(scheduleId);

        result.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBe(started);
    }

    [Fact]
    public async Task Should_report_no_start_for_a_failure_that_measured_no_duration()
    {
        // The failure path writes LastExecutionUtc and leaves ExecutionTimeMs untouched, so counting the
        // duration back off the end returns the END as the start — the whole run reported as tardiness. With
        // nothing that recorded the transition either, the honest answer is no start at all.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);

        row.Status           = QueuedTaskStatus.Failed;
        row.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        row.ExecutionTimeMs  = 0;
        row.Exception        = "boom";

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        var result = await _service.GetOccurrencesAsync(scheduleId);

        result.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Should_report_no_start_for_a_completion_that_measured_no_duration()
    {
        // Two rows reach Completed without a handler ever running: the durable schedule finalized by the
        // materialization that spends its last run, and an inline series recovery finds past its bound. Both
        // stamp LastExecutionUtc and force ExecutionTimeMs to 0, so subtracting the duration reported the
        // finalization instant as the moment a run began — on a row that has no runs at all.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);

        row.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        row.ExecutionTimeMs  = 0;

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        var result = await _service.GetOccurrencesAsync(scheduleId);

        result.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBeNull(
            "0 is a duration nobody measured, not a run that took no time");
    }

    [Fact]
    public async Task Should_report_no_start_for_a_schedule_row_finalized_without_a_run()
    {
        // The durable schedule row itself: it is schedule-only, so it never sets InProgress, never runs a
        // handler and never writes a duration — but the last materialization finalizes it with the
        // finalization instant in LastExecutionUtc.
        var scheduleId = Guid.NewGuid();
        var finalized  = DateTimeOffset.UtcNow.AddMinutes(-1);

        var schedule = new QueuedTask
        {
            Id               = scheduleId,
            Type             = typeof(SampleTask).AssemblyQualifiedName!,
            Handler          = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request          = "{\"Message\":\"Test\"}",
            Status           = QueuedTaskStatus.Completed,
            QueueName        = "recurring",
            CreatedAtUtc     = DateTimeOffset.UtcNow.AddHours(-1),
            IsRecurring      = true,
            CurrentRunCount  = 4,
            MaxRuns          = 4,
            LastExecutionUtc = finalized,
            ExecutionTimeMs  = 0
        };

        _storageMock.Setup(s => s.Get(It.IsAny<Expression<Func<QueuedTask, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([schedule]);

        var detail = await _service.GetTaskDetailAsync(scheduleId);

        detail.ShouldNotBeNull();
        detail.LastExecutionUtc.ShouldBe(finalized, "the premise: the finalization stamped an end");
        detail.StartedAtUtc.ShouldBeNull("a schedule row runs no handler, so it has no run to report");
    }

    [Fact]
    public async Task Should_prefer_the_recorded_start_to_the_one_derived_from_the_columns()
    {
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);
        var started    = DateTimeOffset.UtcNow.AddMinutes(-7);

        row.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        row.ExecutionTimeMs  = 90_000;

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        _storageMock
            .Setup(s => s.GetLastRunStarts(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [row.Id] = started });

        var result = await _service.GetOccurrencesAsync(scheduleId);

        result.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBe(started,
            "the recorded transition is the run's own start; the columns only approximate it");
    }

    [Fact]
    public async Task Should_report_no_start_for_a_row_that_is_waiting_to_run_again()
    {
        // A requeued occurrence, and every recurring row between two runs: the row stands for a run that has
        // not started, and the previous attempt's start is not that run's. The storage is not even asked.
        var scheduleId = Guid.NewGuid();
        var row        = CreateOccurrenceRow(scheduleId);

        row.Status           = QueuedTaskStatus.Queued;
        row.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        row.ExecutionTimeMs  = 90_000;

        _storageMock
            .Setup(s => s.GetOccurrencesPage(scheduleId, false, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OccurrencePage([row], 1));

        var result = await _service.GetOccurrencesAsync(scheduleId);

        result.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBeNull();

        _storageMock.Verify(
            s => s.GetLastRunStarts(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(row.Id)),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a row waiting for its delivery has no run to ask about");
    }

    private static QueuedTask CreateOccurrenceRow(Guid scheduleId) => new()
    {
        Id                    = Guid.NewGuid(),
        Type                  = typeof(SampleTask).AssemblyQualifiedName!,
        Handler               = typeof(SampleTaskHandler).AssemblyQualifiedName!,
        Request               = "{\"Message\":\"Test\"}",
        Status                = QueuedTaskStatus.Completed,
        QueueName             = "recurring",
        CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-10),
        ParentTaskId          = scheduleId,
        ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
    };

    private static List<QueuedTask> CreateSampleTasks(int count)
    {
        var tasks = new List<QueuedTask>();
        for (var i = 0; i < count; i++)
        {
            tasks.Add(new QueuedTask
            {
                Id = Guid.NewGuid(),
                Type = typeof(SampleTask).AssemblyQualifiedName!,
                Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
                Request = "{\"Message\":\"Test\"}",
                Status = i % 2 == 0 ? QueuedTaskStatus.Completed : QueuedTaskStatus.Queued,
                QueueName = "default",
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-i),
                StatusAudits = new List<StatusAudit>()
            });
        }
        return tasks;
    }

    private static QueuedTask CreateTaskWithAudits(Guid taskId)
    {
        return new QueuedTask
        {
            Id = taskId,
            Type = typeof(SampleTask).AssemblyQualifiedName!,
            Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Test\"}",
            Status = QueuedTaskStatus.Completed,
            QueueName = "default",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            StatusAudits = new List<StatusAudit>
            {
                new() { Id = 1, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Queued, UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) },
                new() { Id = 2, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.InProgress, UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-31) },
                new() { Id = 3, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Completed, UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30) }
            },
            RunsAudits = new List<RunsAudit>
            {
                new() { Id = 1, QueuedTaskId = taskId, Status = QueuedTaskStatus.Completed, ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }
            }
        };
    }
}
