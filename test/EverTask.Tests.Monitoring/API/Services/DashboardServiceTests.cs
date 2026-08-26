using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.API.Services;

public class DashboardServiceTests
{
    private readonly Mock<ITaskStorage> _storageMock;
    private readonly DashboardService _service;

    public DashboardServiceTests()
    {
        _storageMock = new Mock<ITaskStorage>();
        _service = new DashboardService(_storageMock.Object);
    }

    [Fact]
    public async Task Should_calculate_overview_statistics()
    {
        // Arrange
        var tasks = CreateTasksWithVariousStatuses();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. tasks]);

        // Act
        var result = await _service.GetOverviewAsync(DateRange.Today);

        // Assert
        result.ShouldNotBeNull();
        result.TotalTasksToday.ShouldBeGreaterThanOrEqualTo(0);
        result.FailedCount.ShouldBe(tasks.Count(t => t.Status == QueuedTaskStatus.Failed));
        result.StatusDistribution.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_calculate_success_rate()
    {
        // Arrange - 7 completed, 3 failed = 70% success rate
        var tasks = new List<QueuedTask>();
        for (var i = 0; i < 7; i++)
        {
            tasks.Add(CreateTask(QueuedTaskStatus.Completed));
        }
        for (var i = 0; i < 3; i++)
        {
            tasks.Add(CreateTask(QueuedTaskStatus.Failed));
        }

        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. tasks]);

        // Act
        var result = await _service.GetOverviewAsync(DateRange.Today);

        // Assert
        result.SuccessRate.ShouldBeGreaterThanOrEqualTo(0);
        result.SuccessRate.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Should_get_recent_activity()
    {
        // Arrange
        var tasks = CreateTasksWithRecentActivity();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. tasks]);

        // Act
        var result = await _service.GetRecentActivityAsync(10);

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBeLessThanOrEqualTo(10);
        // Verify most recent tasks come first
        if (result.Count > 1)
        {
            result[0].Timestamp.ShouldBeGreaterThanOrEqualTo(result[1].Timestamp);
        }
    }

    [Fact]
    public async Task Should_average_the_durations_the_runs_measured_on_rows_that_carry_no_audit()
    {
        // Every relational read hands back rows whose StatusAudits navigation is empty — nothing populates
        // it — so an average derived from that trail answered 0.0 on all four providers. The measured
        // duration is a column, and it is on every row a completion wrote.
        var fast = CreateTask(QueuedTaskStatus.Completed);
        var slow = CreateTask(QueuedTaskStatus.Completed);

        fast.ExecutionTimeMs = 100;
        slow.ExecutionTimeMs = 300;

        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>())).ReturnsAsync([fast, slow]);

        (await _service.GetOverviewAsync(DateRange.Today)).AvgExecutionTimeMs.ShouldBe(200);
    }

    [Fact]
    public async Task Should_leave_out_of_the_average_the_runs_nobody_measured()
    {
        // A failure stamps the end and leaves the duration at 0, and so does a series finalized WITHOUT a
        // run — the recovery of a recurring series past its bound. Counting those zeros as durations halves
        // the mean of a store that has them.
        var measured  = CreateTask(QueuedTaskStatus.Completed);
        var failed    = CreateTask(QueuedTaskStatus.Failed);
        var finalized = CreateTask(QueuedTaskStatus.Completed);

        measured.ExecutionTimeMs  = 100;
        failed.ExecutionTimeMs    = 0;
        finalized.ExecutionTimeMs = 0;

        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([measured, failed, finalized]);

        (await _service.GetOverviewAsync(DateRange.Today)).AvgExecutionTimeMs.ShouldBe(100);

        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>())).ReturnsAsync([failed, finalized]);

        (await _service.GetOverviewAsync(DateRange.Today)).AvgExecutionTimeMs
            .ShouldBe(0, "nothing was measured, and no number is invented for it");
    }

    private List<QueuedTask> CreateTasksWithVariousStatuses()
    {
        return
        [
            CreateTask(QueuedTaskStatus.Completed),
            CreateTask(QueuedTaskStatus.Completed),
            CreateTask(QueuedTaskStatus.Failed),
            CreateTask(QueuedTaskStatus.InProgress),
            CreateTask(QueuedTaskStatus.Queued),
            CreateTask(QueuedTaskStatus.Queued)
        ];
    }

    private List<QueuedTask> CreateTasksWithRecentActivity()
    {
        var tasks = new List<QueuedTask>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 15; i++)
        {
            tasks.Add(new QueuedTask
            {
                Id = Guid.NewGuid(),
                Type = typeof(SampleTask).AssemblyQualifiedName!,
                Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
                Request = "{\"Message\":\"Test\"}",
                Status = QueuedTaskStatus.Completed,
                QueueName = "default",
                CreatedAtUtc = now.AddMinutes(-i),
                StatusAudits = new List<StatusAudit>()
            });
        }
        return tasks;
    }

    private QueuedTask CreateTask(QueuedTaskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new QueuedTask
        {
            Id = Guid.NewGuid(),
            Type = typeof(SampleTask).AssemblyQualifiedName!,
            Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Test\"}",
            Status = status,
            QueueName = "default",
            CreatedAtUtc = now,
            LastExecutionUtc = status == QueuedTaskStatus.Completed || status == QueuedTaskStatus.Failed ? now.AddMinutes(1) : null,
            StatusAudits = new List<StatusAudit>()
        };
    }
}
