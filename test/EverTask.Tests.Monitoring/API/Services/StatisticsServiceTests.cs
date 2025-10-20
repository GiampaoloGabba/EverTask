using System.Linq.Expressions;
using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Statistics;
using EverTask.Monitor.Api.Services;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.API.Services;

public class StatisticsServiceTests
{
    private readonly Mock<ITaskStorage> _storageMock;
    private readonly StatisticsService _service;

    public StatisticsServiceTests()
    {
        _storageMock = new Mock<ITaskStorage>();
        _service = new StatisticsService(_storageMock.Object);
    }

    [Fact]
    public async Task Should_calculate_success_rate_by_period()
    {
        // Arrange
        var tasks = CreateTasksOverTime();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

        // Act
        var result = await _service.GetSuccessRateTrendAsync(TimePeriod.Last7Days);

        // Assert
        result.ShouldNotBeNull();
        result.Timestamps.ShouldNotBeNull();
        result.SuccessRates.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_aggregate_task_types()
    {
        // Arrange
        var tasks = CreateTasksWithDifferentTypes();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

        // Act
        var result = await _service.GetTaskTypeDistributionAsync(DateRange.Week);

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_calculate_avg_execution_times()
    {
        // Arrange
        var tasks = CreateCompletedTasksWithExecutionTimes();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

        // Act
        var result = await _service.GetExecutionTimesAsync(DateRange.Today);

        // Assert
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_get_queue_metrics()
    {
        // Arrange
        var tasks = CreateTasksInDifferentQueues();
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

        // Act
        var result = await _service.GetQueueMetricsAsync();

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBeGreaterThan(0);

        var defaultQueue = result.FirstOrDefault(q => q.QueueName == "default");
        defaultQueue.ShouldNotBeNull();
        defaultQueue.TotalTasks.ShouldBeGreaterThan(0);
    }

    private List<QueuedTask> CreateTasksOverTime()
    {
        var tasks = new List<QueuedTask>();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 14; i++)
        {
            // Mix of completed and failed tasks
            tasks.Add(CreateTaskAtTime(now.AddDays(-i), i % 3 == 0 ? QueuedTaskStatus.Failed : QueuedTaskStatus.Completed));
        }
        return tasks;
    }

    private List<QueuedTask> CreateTasksWithDifferentTypes()
    {
        return new List<QueuedTask>
        {
            CreateTaskOfType(typeof(SampleTask)),
            CreateTaskOfType(typeof(SampleTask)),
            CreateTaskOfType(typeof(SampleRecurringTask)),
            CreateTaskOfType(typeof(SampleFailingTask))
        };
    }

    private List<QueuedTask> CreateCompletedTasksWithExecutionTimes()
    {
        var tasks = new List<QueuedTask>();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(new QueuedTask
            {
                Id = Guid.NewGuid(),
                Type = typeof(SampleTask).AssemblyQualifiedName!,
                Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
                Request = "{\"Message\":\"Test\"}",
                Status = QueuedTaskStatus.Completed,
                QueueName = "default",
                CreatedAtUtc = now.AddHours(-i),
                LastExecutionUtc = now.AddHours(-i).AddMinutes(i + 1), // Different execution times
                StatusAudits = new List<StatusAudit>()
            });
        }
        return tasks;
    }

    private List<QueuedTask> CreateTasksInDifferentQueues()
    {
        return new List<QueuedTask>
        {
            CreateTaskInQueue("default", QueuedTaskStatus.Completed),
            CreateTaskInQueue("default", QueuedTaskStatus.Queued),
            CreateTaskInQueue("emails", QueuedTaskStatus.Completed),
            CreateTaskInQueue("processing", QueuedTaskStatus.InProgress)
        };
    }

    private QueuedTask CreateTaskAtTime(DateTimeOffset createdAt, QueuedTaskStatus status)
    {
        return new QueuedTask
        {
            Id = Guid.NewGuid(),
            Type = typeof(SampleTask).AssemblyQualifiedName!,
            Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Test\"}",
            Status = status,
            QueueName = "default",
            CreatedAtUtc = createdAt,
            LastExecutionUtc = status == QueuedTaskStatus.Completed || status == QueuedTaskStatus.Failed ? createdAt.AddMinutes(1) : null,
            StatusAudits = new List<StatusAudit>()
        };
    }

    private QueuedTask CreateTaskOfType(Type taskType)
    {
        var now = DateTimeOffset.UtcNow;
        return new QueuedTask
        {
            Id = Guid.NewGuid(),
            Type = taskType.AssemblyQualifiedName!,
            Handler = $"{taskType.Name}Handler",
            Request = "{\"Message\":\"Test\"}",
            Status = QueuedTaskStatus.Completed,
            QueueName = "default",
            CreatedAtUtc = now,
            StatusAudits = new List<StatusAudit>()
        };
    }

    private QueuedTask CreateTaskInQueue(string queueName, QueuedTaskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new QueuedTask
        {
            Id = Guid.NewGuid(),
            Type = typeof(SampleTask).AssemblyQualifiedName!,
            Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Test\"}",
            Status = status,
            QueueName = queueName,
            CreatedAtUtc = now,
            StatusAudits = new List<StatusAudit>()
        };
    }
}
