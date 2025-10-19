using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Queues;
using EverTask.Monitor.Api.DTOs.Statistics;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for advanced statistics and analytics.
/// </summary>
public class StatisticsService : IStatisticsService
{
    private readonly ITaskStorage _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsService"/> class.
    /// </summary>
    public StatisticsService(ITaskStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<SuccessRateTrendDto> GetSuccessRateTrendAsync(TimePeriod period, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);
        var now = DateTimeOffset.UtcNow;

        // Convert TimePeriod to date range and interval
        var (startDate, intervalDays) = period switch
        {
            TimePeriod.Last7Days => (now.AddDays(-7), 1),    // Daily buckets
            TimePeriod.Last30Days => (now.AddDays(-30), 1),   // Daily buckets
            TimePeriod.Last90Days => (now.AddDays(-90), 7),   // Weekly buckets
            _ => (now.AddDays(-7), 1)
        };

        var filteredTasks = allTasks.Where(t => t.CreatedAtUtc >= startDate).ToList();

        var timestamps = new List<DateTimeOffset>();
        var successRates = new List<decimal>();

        var current = startDate;
        while (current <= now)
        {
            var bucketEnd = current.AddDays(intervalDays);
            var bucketTasks = filteredTasks.Where(t => t.CreatedAtUtc >= current && t.CreatedAtUtc < bucketEnd).ToList();

            var completed = bucketTasks.Count(t => t.Status == QueuedTaskStatus.Completed);
            var failed = bucketTasks.Count(t => t.Status == QueuedTaskStatus.Failed);
            var total = completed + failed;

            var successRate = total > 0 ? (decimal)completed / total * 100 : 0m;

            timestamps.Add(current);
            successRates.Add(Math.Round(successRate, 2));

            current = bucketEnd;
        }

        return new SuccessRateTrendDto(timestamps, successRates);
    }

    /// <inheritdoc />
    public async Task<List<QueueMetricsDto>> GetQueueMetricsAsync(CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);

        return allTasks
            .GroupBy(t => t.QueueName)
            .Select(g =>
            {
                var queueTasks = g.ToList();
                var totalTasks = queueTasks.Count;
                var pendingTasks = queueTasks.Count(t => t.Status == QueuedTaskStatus.WaitingQueue || t.Status == QueuedTaskStatus.Pending);
                var inProgressTasks = queueTasks.Count(t => t.Status == QueuedTaskStatus.InProgress);
                var completedTasks = queueTasks.Count(t => t.Status == QueuedTaskStatus.Completed);
                var failedTasks = queueTasks.Count(t => t.Status == QueuedTaskStatus.Failed);

                // Calculate average execution time for completed tasks
                var completedWithExecTime = queueTasks
                    .Where(t => t.Status == QueuedTaskStatus.Completed && t.LastExecutionUtc.HasValue)
                    .ToList();

                var avgExecutionTimeMs = completedWithExecTime.Any()
                    ? completedWithExecTime.Average(t => (t.LastExecutionUtc!.Value - t.CreatedAtUtc).TotalMilliseconds)
                    : 0.0;

                // Calculate success rate
                var totalFinished = completedTasks + failedTasks;
                var successRate = totalFinished > 0 ? (decimal)completedTasks / totalFinished * 100 : 0m;

                return new QueueMetricsDto(
                    g.Key,
                    totalTasks,
                    pendingTasks,
                    inProgressTasks,
                    completedTasks,
                    failedTasks,
                    Math.Round(avgExecutionTimeMs, 2),
                    Math.Round(successRate, 2)
                );
            })
            .OrderByDescending(q => q.TotalTasks)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, int>> GetTaskTypeDistributionAsync(DateRange range, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);
        var now = DateTimeOffset.UtcNow;

        // Convert DateRange to filter
        var filteredTasks = range switch
        {
            DateRange.Today => allTasks.Where(t => t.CreatedAtUtc >= now.Date),
            DateRange.Week => allTasks.Where(t => t.CreatedAtUtc >= now.AddDays(-7)),
            DateRange.Month => allTasks.Where(t => t.CreatedAtUtc >= now.AddMonths(-1)),
            DateRange.All => allTasks,
            _ => allTasks.Where(t => t.CreatedAtUtc >= now.Date)
        };

        return filteredTasks
            .GroupBy(t => GetShortTypeName(t.Type))
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public async Task<List<ExecutionTimeDto>> GetExecutionTimesAsync(DateRange range, CancellationToken ct = default)
    {
        var allTasks = await _storage.GetAll(ct);
        var now = DateTimeOffset.UtcNow;

        // Convert DateRange to filter and interval
        var (startDate, intervalHours) = range switch
        {
            DateRange.Today => (now.Date, 1),              // Hourly buckets
            DateRange.Week => (now.AddDays(-7), 24),       // Daily buckets
            DateRange.Month => (now.AddMonths(-1), 24),    // Daily buckets
            DateRange.All => (allTasks.Any() ? allTasks.Min(t => t.CreatedAtUtc) : now.AddMonths(-1), 24 * 7), // Weekly buckets
            _ => (now.Date, 1)
        };

        var completedTasks = allTasks
            .Where(t => t.Status == QueuedTaskStatus.Completed &&
                       t.LastExecutionUtc.HasValue &&
                       t.CreatedAtUtc >= startDate)
            .ToList();

        var result = new List<ExecutionTimeDto>();
        var current = startDate;

        while (current <= now)
        {
            var bucketEnd = current.AddHours(intervalHours);
            var bucketTasks = completedTasks
                .Where(t => t.CreatedAtUtc >= current && t.CreatedAtUtc < bucketEnd)
                .ToList();

            var avgExecutionTimeMs = bucketTasks.Any()
                ? bucketTasks.Average(t => (t.LastExecutionUtc!.Value - t.CreatedAtUtc).TotalMilliseconds)
                : 0.0;

            result.Add(new ExecutionTimeDto(current, Math.Round(avgExecutionTimeMs, 2)));
            current = bucketEnd;
        }

        return result;
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
}
