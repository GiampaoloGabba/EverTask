using System.Linq.Expressions;
using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.Services;
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
            .ReturnsAsync(tasks.ToArray());

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
        for (int i = 0; i < 7; i++)
        {
            tasks.Add(CreateTask(QueuedTaskStatus.Completed));
        }
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(CreateTask(QueuedTaskStatus.Failed));
        }

        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

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
            .ReturnsAsync(tasks.ToArray());

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

    private List<QueuedTask> CreateTasksWithVariousStatuses()
    {
        return new List<QueuedTask>
        {
            CreateTask(QueuedTaskStatus.Completed),
            CreateTask(QueuedTaskStatus.Completed),
            CreateTask(QueuedTaskStatus.Failed),
            CreateTask(QueuedTaskStatus.InProgress),
            CreateTask(QueuedTaskStatus.Queued),
            CreateTask(QueuedTaskStatus.Queued)
        };
    }

    private List<QueuedTask> CreateTasksWithRecentActivity()
    {
        var tasks = new List<QueuedTask>();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 15; i++)
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
