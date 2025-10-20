using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// Seeds test data into storage for monitoring API tests
/// </summary>
public class TestDataSeeder
{
    private readonly ITaskStorage _storage;

    public TestDataSeeder(ITaskStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Seed storage with realistic test data
    /// </summary>
    public async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tasks = new List<QueuedTask>();

        // Completed tasks from today
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.Completed,
                createdAt: now.AddHours(-i),
                lastExecutionUtc: now.AddHours(-i).AddMinutes(2),
                queueName: i % 2 == 0 ? "default" : "emails"
            ));
        }

        // Failed tasks from today
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.Failed,
                createdAt: now.AddHours(-i - 1),
                lastExecutionUtc: now.AddHours(-i - 1).AddMinutes(1),
                queueName: "default",
                exception: "Test exception message"
            ));
        }

        // In-progress tasks
        for (int i = 0; i < 2; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.InProgress,
                createdAt: now.AddMinutes(-5),
                queueName: "processing"
            ));
        }

        // Queued tasks
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.Queued,
                createdAt: now.AddMinutes(-i),
                queueName: "default"
            ));
        }

        // Completed tasks from last week
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.Completed,
                createdAt: now.AddDays(-i % 7).AddHours(-i),
                lastExecutionUtc: now.AddDays(-i % 7).AddHours(-i).AddMinutes(1),
                queueName: i % 3 == 0 ? "emails" : "default"
            ));
        }

        // Failed tasks from last week
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(CreateTask(
                status: QueuedTaskStatus.Failed,
                createdAt: now.AddDays(-i).AddHours(-3),
                lastExecutionUtc: now.AddDays(-i).AddHours(-3).AddMinutes(1),
                queueName: "default",
                exception: "Failed task exception"
            ));
        }

        // Recurring task
        tasks.Add(CreateRecurringTask(
            status: QueuedTaskStatus.Completed,
            createdAt: now.AddDays(-30),
            nextRunUtc: now.AddMinutes(5),
            currentRunCount: 100
        ));

        // Persist all tasks
        foreach (var task in tasks)
        {
            await _storage.Persist(task);
        }
    }

    private QueuedTask CreateTask(
        QueuedTaskStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? lastExecutionUtc = null,
        string queueName = "default",
        string? exception = null)
    {
        var taskId = Guid.NewGuid();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = typeof(SampleTask).AssemblyQualifiedName!,
            Handler = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Test task\"}",
            Status = status,
            QueueName = queueName,
            CreatedAtUtc = createdAt,
            LastExecutionUtc = lastExecutionUtc,
            Exception = exception,
            StatusAudits = new List<StatusAudit>
            {
                new() { Id = 1, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Queued, UpdatedAtUtc = createdAt }
            }
        };

        if (status == QueuedTaskStatus.InProgress || status == QueuedTaskStatus.Completed || status == QueuedTaskStatus.Failed)
        {
            task.StatusAudits.Add(new StatusAudit
            {
                Id = 2,
                QueuedTaskId = taskId,
                NewStatus = QueuedTaskStatus.InProgress,
                UpdatedAtUtc = createdAt.AddSeconds(5)
            });
        }

        if (status == QueuedTaskStatus.Completed || status == QueuedTaskStatus.Failed)
        {
            task.StatusAudits.Add(new StatusAudit
            {
                Id = 3,
                QueuedTaskId = taskId,
                NewStatus = status,
                UpdatedAtUtc = lastExecutionUtc ?? createdAt.AddMinutes(1)
            });

            task.RunsAudits = new List<RunsAudit>
            {
                new()
                {
                    Id = 1,
                    QueuedTaskId = taskId,
                    Status = status,
                    ExecutedAt = lastExecutionUtc ?? createdAt.AddMinutes(1),
                    Exception = exception
                }
            };
        }

        return task;
    }

    private QueuedTask CreateRecurringTask(
        QueuedTaskStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset nextRunUtc,
        int currentRunCount)
    {
        var taskId = Guid.NewGuid();
        return new QueuedTask
        {
            Id = taskId,
            Type = typeof(SampleRecurringTask).AssemblyQualifiedName!,
            Handler = typeof(SampleRecurringTaskHandler).AssemblyQualifiedName!,
            Request = "{\"Message\":\"Recurring task\"}",
            Status = status,
            QueueName = "recurring",
            CreatedAtUtc = createdAt,
            IsRecurring = true,
            RecurringInfo = "Every 5 minutes",
            NextRunUtc = nextRunUtc,
            CurrentRunCount = currentRunCount,
            MaxRuns = null,
            RunUntil = null,
            StatusAudits = new List<StatusAudit>
            {
                new() { Id = 1, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.Queued, UpdatedAtUtc = createdAt },
                new() { Id = 2, QueuedTaskId = taskId, NewStatus = QueuedTaskStatus.InProgress, UpdatedAtUtc = createdAt.AddSeconds(5) },
                new() { Id = 3, QueuedTaskId = taskId, NewStatus = status, UpdatedAtUtc = createdAt.AddMinutes(1) }
            },
            RunsAudits = Enumerable.Range(0, Math.Min(currentRunCount, 10)).Select(i => new RunsAudit
            {
                Id = i + 1,
                QueuedTaskId = taskId,
                Status = QueuedTaskStatus.Completed,
                ExecutedAt = createdAt.AddMinutes(i * 5 + 1)
            }).ToList()
        };
    }
}
