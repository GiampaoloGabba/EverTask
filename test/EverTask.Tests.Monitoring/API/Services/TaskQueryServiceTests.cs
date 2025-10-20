using System.Linq.Expressions;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Monitor.Api.Services;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.API.Services;

public class TaskQueryServiceTests
{
    private readonly Mock<ITaskStorage> _storageMock;
    private readonly TaskQueryService _service;

    public TaskQueryServiceTests()
    {
        _storageMock = new Mock<ITaskStorage>();
        _service = new TaskQueryService(_storageMock.Object);
    }

    [Fact]
    public async Task Should_filter_and_paginate_tasks()
    {
        // Arrange
        var tasks = CreateSampleTasks(20);
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks.ToArray());

        var filter = new TaskFilter { Statuses = new List<QueuedTaskStatus> { QueuedTaskStatus.Completed } };
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
            .ReturnsAsync(new[] { task });

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
    public async Task Should_handle_empty_results()
    {
        // Arrange
        _storageMock.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QueuedTask>());

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
            .ReturnsAsync(Array.Empty<QueuedTask>());

        // Act
        var result = await _service.GetTaskDetailAsync(taskId);

        // Assert
        result.ShouldBeNull();
    }

    private List<QueuedTask> CreateSampleTasks(int count)
    {
        var tasks = new List<QueuedTask>();
        for (int i = 0; i < count; i++)
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

    private QueuedTask CreateTaskWithAudits(Guid taskId)
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
