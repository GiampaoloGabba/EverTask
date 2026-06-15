using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// End-to-end integration test: runs the real <see cref="AuditCleanupHostedService"/> against a real
/// SQLite database and verifies that one cleanup cycle trims aged execution logs AND aged status audits.
/// SQLite is the provider that cannot translate DateTimeOffset comparisons server-side, so this is the
/// end-to-end proof that the client-side cleanup path works through the hosted service wiring.
/// </summary>
public sealed class AuditCleanupHostedServiceIntegrationTests : IDisposable
{
    private readonly string _dbFile = $"AuditCleanupE2E_{Guid.NewGuid():N}.db";
    private readonly ServiceProvider _provider;
    private readonly ITaskStoreDbContext _ctx;

    public AuditCleanupHostedServiceIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(AuditCleanupHostedServiceIntegrationTests).Assembly))
                .AddSqliteStorage($"Data Source={_dbFile}", opt => opt.AutoApplyMigrations = true);

        services.Configure<AuditCleanupOptions>(o =>
        {
            o.RetentionPolicy = new AuditRetentionPolicy
            {
                StatusAuditRetentionDays  = 7,
                ExecutionLogRetentionDays = 7
            };
            o.InitialDelay    = TimeSpan.Zero;                 // run the first cycle immediately
            o.CleanupInterval = TimeSpan.FromMilliseconds(100);
        });
        services.AddSingleton<AuditCleanupHostedService>();

        _provider = services.BuildServiceProvider();
        _ctx      = _provider.GetRequiredService<ITaskStoreDbContext>();
    }

    [Fact]
    public async Task Should_trim_aged_logs_and_audits_through_the_hosted_service()
    {
        var now = DateTimeOffset.UtcNow;

        var agedLog   = new TaskExecutionLog { Id = Guid.NewGuid(), TimestampUtc = now.AddDays(-30), Level = "Information", Message = "aged",   SequenceNumber = 0 };
        var recentLog = new TaskExecutionLog { Id = Guid.NewGuid(), TimestampUtc = now.AddDays(-1),  Level = "Information", Message = "recent", SequenceNumber = 1 };

        var task = new QueuedTask
        {
            Id            = Guid.NewGuid(),
            CreatedAtUtc  = now,
            Type          = "CleanupTask",
            Request       = "{}",
            Handler       = "CleanupHandler",
            Status        = QueuedTaskStatus.Completed,
            ExecutionLogs = new List<TaskExecutionLog> { agedLog, recentLog },
            StatusAudits  = new List<StatusAudit>
            {
                new() { UpdatedAtUtc = now.AddDays(-30), NewStatus = QueuedTaskStatus.Completed },
                new() { UpdatedAtUtc = now.AddDays(-1),  NewStatus = QueuedTaskStatus.Completed }
            }
        };
        agedLog.TaskId = recentLog.TaskId = task.Id;

        _ctx.QueuedTasks.Add(task);
        await _ctx.SaveChangesAsync(CancellationToken.None);

        var service = _provider.GetRequiredService<AuditCleanupHostedService>();
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        try
        {
            // Poll until the cleanup cycle has removed the aged log (bounded so the test can never hang).
            var agedGone = false;
            for (var i = 0; i < 100 && !agedGone; i++)
            {
                await Task.Delay(100, cts.Token);
                agedGone = _ctx.TaskExecutionLogs.Count(x => x.Id == agedLog.Id) == 0;
            }

            agedGone.ShouldBeTrue("the hosted service should have trimmed the 30-day-old execution log");
            _ctx.TaskExecutionLogs.Count(x => x.Id == recentLog.Id).ShouldBe(1, "the recent log survives");
            _ctx.StatusAudit.Count(x => x.QueuedTaskId == task.Id).ShouldBe(1, "only the recent status audit survives");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        try { _provider.Dispose(); } catch { /* ignore */ }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { /* ignore */ }
    }
}
