using EverTask.Monitor.Api.Controllers;
using EverTask.Monitor.Api.DTOs.RateLimits;
using EverTask.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Tests.Monitoring.API;

/// <summary>
/// Rate limiting observability in Monitor.Api: ThrottledTasks counters sourced from the limiter
/// introspection, the /api/rate-limits snapshot endpoint, the per-task throttledUntil overlay
/// and the DashboardService PendingCount fix (Queued was omitted).
/// </summary>
public class RateLimitMonitoringTests
{
    private sealed class FakeRateLimiterIntrospection : IRateLimiterIntrospection
    {
        public int ParkedTaskCount { get; set; }
        public int MaxParkedTasks { get; set; } = 5000;
        public int TrackedKeyCount { get; set; }
        public long FailOpenCount { get; set; }
        public List<RateLimitKeySnapshot> Snapshot { get; set; } = [];
        public Dictionary<Guid, DateTimeOffset> ThrottledUntil { get; set; } = new();

        public IReadOnlyList<RateLimitKeySnapshot> GetParkedSnapshot() => Snapshot;

        public DateTimeOffset? GetThrottledUntil(Guid taskId) =>
            ThrottledUntil.TryGetValue(taskId, out var slot) ? slot : null;
    }

    private static QueuedTask CreateTask(QueuedTaskStatus status, string? queueName = "default") => new()
    {
        Id           = Guid.NewGuid(),
        Type         = "EverTask.Tests.Monitoring.TestData.SampleTask, EverTask.Tests.Monitoring",
        Request      = "{}",
        Handler      = "handler",
        Status       = status,
        QueueName    = queueName,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    // ---------------------------------------------------------------- DashboardService

    [Fact]
    public async Task Should_count_queued_tasks_as_pending_in_queue_summaries()
    {
        // PendingCount fix: Queued was previously omitted from the dashboard bucketing
        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[]
               {
                   CreateTask(QueuedTaskStatus.WaitingQueue),
                   CreateTask(QueuedTaskStatus.Pending),
                   CreateTask(QueuedTaskStatus.Queued),
                   CreateTask(QueuedTaskStatus.InProgress),
                   CreateTask(QueuedTaskStatus.Completed)
               });

        var service  = new DashboardService(storage.Object);
        var overview = await service.GetOverviewAsync(DateRange.All);

        var summary = overview.QueueSummaries.Single();
        summary.PendingCount.ShouldBe(3, "WaitingQueue + Pending + Queued all count as pending");
        summary.InProgressCount.ShouldBe(1);
        summary.CompletedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Should_source_throttled_tasks_from_limiter_introspection()
    {
        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { CreateTask(QueuedTaskStatus.Queued), CreateTask(QueuedTaskStatus.Queued) });

        var introspection = new FakeRateLimiterIntrospection
        {
            ParkedTaskCount = 2,
            Snapshot =
            [
                new RateLimitKeySnapshot("default", "tenant-A", 2, DateTimeOffset.UtcNow.AddSeconds(5))
            ]
        };

        var service  = new DashboardService(storage.Object, introspection);
        var overview = await service.GetOverviewAsync(DateRange.All);

        overview.ThrottledTasks.ShouldBe(2, "ThrottledTasks comes from the limiter snapshot, not storage");
        overview.QueueSummaries.Single().ThrottledCount.ShouldBe(2);
    }

    [Fact]
    public async Task Should_report_zero_throttled_tasks_without_introspection()
    {
        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { CreateTask(QueuedTaskStatus.Queued) });

        var overview = await new DashboardService(storage.Object).GetOverviewAsync(DateRange.All);

        overview.ThrottledTasks.ShouldBe(0);
        overview.QueueSummaries.Single().ThrottledCount.ShouldBe(0);
    }

    // ---------------------------------------------------------------- TaskQueryService overlay

    [Fact]
    public async Task Should_overlay_throttled_until_on_parked_tasks()
    {
        var parkedTask = CreateTask(QueuedTaskStatus.Queued);
        var plainTask  = CreateTask(QueuedTaskStatus.Queued);
        var slot       = DateTimeOffset.UtcNow.AddSeconds(7);

        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.GetAll(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { parkedTask, plainTask });

        var introspection = new FakeRateLimiterIntrospection
        {
            ThrottledUntil = { [parkedTask.Id] = slot }
        };

        var service = new TaskQueryService(storage.Object, introspection);
        var page    = await service.GetTasksAsync(new TaskFilter(), new PaginationParams());

        page.Items.Single(t => t.Id == parkedTask.Id).ThrottledUntil.ShouldBe(slot);
        page.Items.Single(t => t.Id == plainTask.Id).ThrottledUntil.ShouldBeNull();
    }

    [Fact]
    public async Task Should_overlay_throttled_until_on_task_detail()
    {
        var parkedTask = CreateTask(QueuedTaskStatus.Queued);
        var slot       = DateTimeOffset.UtcNow.AddSeconds(7);

        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.Get(It.IsAny<System.Linq.Expressions.Expression<Func<QueuedTask, bool>>>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { parkedTask });

        var introspection = new FakeRateLimiterIntrospection { ThrottledUntil = { [parkedTask.Id] = slot } };

        var detail = await new TaskQueryService(storage.Object, introspection).GetTaskDetailAsync(parkedTask.Id);

        detail.ShouldNotBeNull();
        detail.ThrottledUntil.ShouldBe(slot);
    }

    // ---------------------------------------------------------------- /api/rate-limits

    [Fact]
    public void Should_return_rate_limits_snapshot()
    {
        var nextSlot = DateTimeOffset.UtcNow.AddSeconds(12);
        var introspection = new FakeRateLimiterIntrospection
        {
            ParkedTaskCount = 3,
            MaxParkedTasks  = 5000,
            TrackedKeyCount = 42,
            FailOpenCount   = 7,
            Snapshot =
            [
                new RateLimitKeySnapshot("default", "tenant-A", 2, nextSlot),
                new RateLimitKeySnapshot("exports", "tenant-B", 1, nextSlot.AddSeconds(5))
            ]
        };

        var result = new RateLimitsController(introspection).GetRateLimits();

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<RateLimitsDto>();
        dto.Enabled.ShouldBeTrue();
        dto.ThrottledTasks.ShouldBe(3);
        dto.MaxParkedTasks.ShouldBe(5000);
        dto.TrackedKeys.ShouldBe(42);
        dto.FailOpenCount.ShouldBe(7);
        dto.Keys.Count.ShouldBe(2);
        dto.Keys[0].ParkedCount.ShouldBe(2, "buckets are ordered by parked count descending");
        dto.Keys[0].Key.ShouldBe("tenant-A");
        dto.Keys[0].NextSlotUtc.ShouldBe(nextSlot);
    }

    [Fact]
    public void Should_return_disabled_snapshot_without_introspection()
    {
        var result = new RateLimitsController().GetRateLimits();

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<RateLimitsDto>();
        dto.Enabled.ShouldBeFalse();
        dto.ThrottledTasks.ShouldBe(0);
        dto.Keys.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- ITaskStorageStatistics

    [Fact]
    public async Task Should_use_storage_statistics_for_failed_count_when_available()
    {
        var storage = new MemoryTaskStorage(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);
        await storage.Persist(CreateTask(QueuedTaskStatus.Failed));
        await storage.Persist(CreateTask(QueuedTaskStatus.Failed));
        await storage.Persist(CreateTask(QueuedTaskStatus.Completed));

        // MemoryTaskStorage implements ITaskStorageStatistics: counts come from the set-based path
        var counts = await new TaskQueryService(storage).GetTaskCountsAsync();

        counts.Failed.ShouldBe(2);
        counts.All.ShouldBe(3);

        var statistics = (ITaskStorageStatistics)storage;
        var byStatus   = await statistics.CountByStatusAsync();
        byStatus[QueuedTaskStatus.Failed].ShouldBe(2);
        byStatus[QueuedTaskStatus.Completed].ShouldBe(1);

        var byQueue = await statistics.CountByQueueAndStatusAsync();
        byQueue["default"][QueuedTaskStatus.Failed].ShouldBe(2);
    }
}
