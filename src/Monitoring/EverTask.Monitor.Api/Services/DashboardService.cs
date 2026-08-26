using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.RateLimiting;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for dashboard overview data.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly ITaskStorage _storage;
    private readonly IRateLimiterIntrospection? _rateLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardService"/> class.
    /// </summary>
    /// <param name="storage">The task storage.</param>
    /// <param name="rateLimiter">
    /// Optional rate-limiter introspection (absent when EverTask runs without rate limiting or
    /// in standalone API mode): sources the ThrottledTasks counters.
    /// </param>
    public DashboardService(ITaskStorage storage, IRateLimiterIntrospection? rateLimiter = null)
    {
        _storage     = storage;
        _rateLimiter = rateLimiter;
    }

    /// <inheritdoc />
    public async Task<OverviewDto> GetOverviewAsync(DateRange range, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct).ConfigureAwait(false);
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

        // Average execution time (actual execution duration, not including queue time), from the duration the
        // worker measured around each run. Deriving it from StatusAudits instead read a navigation no storage
        // read populates, so the tile answered 0.0 on every relational store.
        var avgExecutionTimeMs = TaskRunTiming.AverageMeasuredDurationMs(filteredTasks);

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
            queueSummaries,
            _rateLimiter?.ParkedTaskCount ?? 0
        )
        {
            // Over the WHOLE store, never the selected range: a backlog is what is owed right now, and a
            // downtime that materialized its occurrences yesterday is exactly the case the tile exists for.
            CatchUpBacklog = SummarizeCatchUpBacklog(allTasks, now)
        };
    }

    /// <inheritdoc />
    public async Task<List<RecentActivityDto>> GetRecentActivityAsync(int limit = 50, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct).ConfigureAwait(false);

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

    /// <summary>
    /// The occurrences of every durable schedule, by row state, plus how far behind the oldest one that has
    /// not started already is.
    /// </summary>
    /// <remarks>
    /// Counted from the rows themselves: an occurrence IS a task row, so the backlog is a fact the store
    /// holds rather than a figure to estimate. Slots a schedule dropped never became rows and are therefore
    /// absent here by construction — they are reported when they happen, by the OccurrenceSkipped event.
    /// </remarks>
    private static CatchUpBacklogDto SummarizeCatchUpBacklog(IEnumerable<QueuedTask> allTasks, DateTimeOffset now)
    {
        var pending    = 0;
        var active     = 0;
        var failed     = 0;
        var skipped    = 0;
        var completed  = 0;
        var halted     = 0;
        DateTimeOffset? oldestPendingSlot = null;

        foreach (var task in allTasks)
        {
            if (task.ParentTaskId == null)
            {
                // A halt lives on the SCHEDULE row, in the same column an occurrence uses for its own
                // metadata, and it only counts while the series is still one that could run: a cancelled or
                // finished schedule keeps the marker it was written with, and nothing ever clears it.
                if (TaskScheduleFacts.HasStandingHalt(task))
                    halted++;

                continue;
            }

            switch (task.Status)
            {
                case QueuedTaskStatus.InProgress:
                    active++;
                    break;
                case QueuedTaskStatus.Failed:
                    failed++;
                    break;
                case QueuedTaskStatus.Cancelled:
                    skipped++;
                    break;
                case QueuedTaskStatus.Completed:
                    completed++;
                    break;
                default:
                    pending++;
                    if (task.ScheduledExecutionUtc is { } slot &&
                        (oldestPendingSlot == null || slot < oldestPendingSlot))
                    {
                        oldestPendingSlot = slot;
                    }

                    break;
            }
        }

        // A slot still in the future is not lateness: a durable schedule materializes ahead of its own time
        // whenever its budget allows, and reporting that as a negative lag would read as time travel.
        var lag = oldestPendingSlot is { } oldest && oldest < now ? (now - oldest).TotalSeconds : 0d;

        return new CatchUpBacklogDto(pending, active, failed, skipped, completed, oldestPendingSlot,
            Math.Round(lag, 2), halted);
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

    private List<QueueSummaryDto> GenerateQueueSummaries(List<QueuedTask> tasks)
    {
        // Throttled (parked) tasks per queue from the limiter snapshot: storage cannot
        // distinguish a parked task from a queued one (single-node, in-memory view)
        var throttledByQueue = (_rateLimiter?.GetParkedSnapshot() ?? [])
                               .GroupBy(s => s.QueueName)
                               .ToDictionary(g => g.Key, g => g.Sum(s => s.ParkedCount));

        return tasks
            .GroupBy(t => t.QueueName)
            .Select(g => new QueueSummaryDto(
                g.Key,
                // Shared bucketing: Queued counts as pending too (it was previously omitted)
                g.Count(t => TaskStatusBuckets.IsPending(t.Status)),
                g.Count(t => t.Status == QueuedTaskStatus.InProgress),
                g.Count(t => t.Status == QueuedTaskStatus.Completed),
                g.Count(t => t.Status == QueuedTaskStatus.Failed),
                throttledByQueue.GetValueOrDefault(g.Key ?? "default")
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
