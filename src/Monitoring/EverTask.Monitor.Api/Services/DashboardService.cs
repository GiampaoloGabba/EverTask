using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for dashboard overview data.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly ITaskStorage _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardService"/> class.
    /// </summary>
    public DashboardService(ITaskStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<OverviewDto> GetOverviewAsync(DateRange range, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);
        var now = DateTimeOffset.UtcNow;

        // Convert DateRange to filter dates
        var todayStart = now.Date;
        var weekStart = now.AddDays(-7);
        var monthStart = now.AddMonths(-1);

        // Calculate statistics
        var totalTasksToday = allTasks.Count(t => t.CreatedAtUtc >= todayStart);
        var totalTasksWeek = allTasks.Count(t => t.CreatedAtUtc >= weekStart);

        // Apply range filter for other stats
        var filteredTasks = range switch
        {
            DateRange.Today => allTasks.Where(t => t.CreatedAtUtc >= todayStart).ToList(),
            DateRange.Week => allTasks.Where(t => t.CreatedAtUtc >= weekStart).ToList(),
            DateRange.Month => allTasks.Where(t => t.CreatedAtUtc >= monthStart).ToList(),
            DateRange.All => allTasks.ToList(),
            _ => allTasks.Where(t => t.CreatedAtUtc >= todayStart).ToList()
        };

        // Success rate calculation
        var completedCount = filteredTasks.Count(t => t.Status == QueuedTaskStatus.Completed);
        var failedCount = filteredTasks.Count(t => t.Status == QueuedTaskStatus.Failed);
        var totalFinished = completedCount + failedCount;
        var successRate = totalFinished > 0 ? (decimal)completedCount / totalFinished * 100 : 0m;

        // Average execution time (for completed tasks)
        var completedTasks = filteredTasks.Where(t => t.Status == QueuedTaskStatus.Completed && t.LastExecutionUtc.HasValue).ToList();
        var avgExecutionTimeMs = completedTasks.Any()
            ? completedTasks.Average(t => (t.LastExecutionUtc!.Value - t.CreatedAtUtc).TotalMilliseconds)
            : 0.0;

        // Status distribution
        var statusDistribution = filteredTasks
            .GroupBy(t => t.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        // Tasks over time (last 24 hours, hourly breakdown)
        var last24Hours = now.AddHours(-24);
        var tasksLast24h = allTasks.Where(t => t.CreatedAtUtc >= last24Hours).ToList();
        var tasksOverTime = GenerateTasksOverTime(tasksLast24h, last24Hours, now);

        // Queue summaries
        var queueSummaries = GenerateQueueSummaries(filteredTasks);

        return new OverviewDto(
            totalTasksToday,
            totalTasksWeek,
            Math.Round(successRate, 2),
            failedCount,
            Math.Round(avgExecutionTimeMs, 2),
            statusDistribution,
            tasksOverTime,
            queueSummaries
        );
    }

    /// <inheritdoc />
    public async Task<List<RecentActivityDto>> GetRecentActivityAsync(int limit = 50, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);

        return allTasks
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(limit)
            .Select(t => new RecentActivityDto(
                t.Id,
                GetShortTypeName(t.Type),
                t.Status,
                t.LastExecutionUtc ?? t.CreatedAtUtc,
                GenerateActivityMessage(t)
            ))
            .ToList();
    }

    private static List<TasksOverTimeDto> GenerateTasksOverTime(List<QueuedTask> tasks, DateTimeOffset start, DateTimeOffset end)
    {
        var result = new List<TasksOverTimeDto>();
        var current = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, start.Offset);

        while (current <= end)
        {
            var hourEnd = current.AddHours(1);
            var hourTasks = tasks.Where(t => t.CreatedAtUtc >= current && t.CreatedAtUtc < hourEnd).ToList();

            var completed = hourTasks.Count(t => t.Status == QueuedTaskStatus.Completed);
            var failed = hourTasks.Count(t => t.Status == QueuedTaskStatus.Failed);
            var total = hourTasks.Count;

            result.Add(new TasksOverTimeDto(current, completed, failed, total));
            current = hourEnd;
        }

        return result;
    }

    private static List<QueueSummaryDto> GenerateQueueSummaries(List<QueuedTask> tasks)
    {
        return tasks
            .GroupBy(t => t.QueueName)
            .Select(g => new QueueSummaryDto(
                g.Key,
                g.Count(t => t.Status == QueuedTaskStatus.WaitingQueue || t.Status == QueuedTaskStatus.Pending),
                g.Count(t => t.Status == QueuedTaskStatus.InProgress),
                g.Count(t => t.Status == QueuedTaskStatus.Completed),
                g.Count(t => t.Status == QueuedTaskStatus.Failed)
            ))
            .OrderByDescending(q => q.PendingCount + q.InProgressCount + q.CompletedCount + q.FailedCount)
            .ToList();
    }

    private static string GenerateActivityMessage(QueuedTask task)
    {
        var typeName = GetShortTypeName(task.Type);

        return task.Status switch
        {
            QueuedTaskStatus.Completed => $"Task {typeName} completed successfully",
            QueuedTaskStatus.Failed => $"Task {typeName} failed" + (task.Exception != null ? $": {GetExceptionMessage(task.Exception)}" : ""),
            QueuedTaskStatus.InProgress => $"Task {typeName} is running",
            QueuedTaskStatus.Queued => $"Task {typeName} queued for execution",
            QueuedTaskStatus.WaitingQueue => $"Task {typeName} waiting in queue",
            QueuedTaskStatus.Pending => $"Task {typeName} is pending",
            QueuedTaskStatus.Cancelled => $"Task {typeName} was cancelled",
            QueuedTaskStatus.ServiceStopped => $"Task {typeName} stopped due to service shutdown",
            _ => $"Task {typeName} status: {task.Status}"
        };
    }

    private static string GetShortTypeName(string fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
            return "Unknown";

        var type = Type.GetType(fullTypeName);
        if (type != null)
            return type.Name;

        var commaIndex = fullTypeName.IndexOf(',');
        var typeName = commaIndex > 0 ? fullTypeName[..commaIndex] : fullTypeName;
        var lastDotIndex = typeName.LastIndexOf('.');
        return lastDotIndex > 0 ? typeName[(lastDotIndex + 1)..] : typeName;
    }

    private static string GetExceptionMessage(string exceptionJson)
    {
        // Exception is stored as JSON string, try to extract the message
        try
        {
            // Simple extraction - just get first line if multi-line
            var firstLine = exceptionJson.Split('\n', '\r').FirstOrDefault()?.Trim();
            return firstLine ?? exceptionJson;
        }
        catch
        {
            return exceptionJson;
        }
    }
}
