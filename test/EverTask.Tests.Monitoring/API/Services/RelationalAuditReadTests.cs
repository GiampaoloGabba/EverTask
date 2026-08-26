using EverTask.Storage.Sqlite;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.API.Services;

/// <summary>
/// The API services over a REAL relational store (EF Core + SQLite, migrations applied), because that is
/// where the defect lived: <see cref="QueuedTask.StatusAudits"/> and <see cref="QueuedTask.RunsAudits"/> are
/// populated by no storage read, so every reader that walked them answered the whole history over the
/// in-memory store — the one the rest of this suite runs on — and nothing at all over SQL Server, PostgreSQL,
/// MySQL and SQLite alike.
/// </summary>
public class RelationalAuditReadTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ITaskStorage    _storage;
    private readonly string          _databasePath;

    public RelationalAuditReadTests()
    {
        _databasePath = Path.Combine(AppContext.BaseDirectory, $"monitoring-audits-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SampleTask).Assembly))
                // Pooling off so the file is really closed when the provider is: a pooled connection would
                // hold it open and every test here would leave a database behind in the output directory.
                .AddSqliteStorage($"Data Source={_databasePath};Pooling=False",
                    opt => opt.AutoApplyMigrations = true);

        _services = services.BuildServiceProvider();
        _storage  = _services.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public async Task Should_answer_the_audit_endpoints_from_the_audit_tables_of_a_relational_store()
    {
        var run = NewRow(QueuedTaskStatus.WaitingQueue);
        await _storage.Persist(run);
        await _storage.SetInProgress(run.Id, AuditLevel.Full);
        await _storage.SetCompleted(run.Id, 120, AuditLevel.Full);

        // The premise, and the whole reason the reads exist: the row a query hands back carries neither
        // collection, on this store and on the other three relational ones.
        var row = (await _storage.Get(t => t.Id == run.Id))[0];
        row.StatusAudits.ShouldBeEmpty();
        row.RunsAudits.ShouldBeEmpty();

        var service = new TaskQueryService(_storage);

        var statusAudits = await service.GetStatusAuditAsync(run.Id);

        statusAudits.Select(a => a.NewStatus)
                    .ShouldBe([QueuedTaskStatus.Completed, QueuedTaskStatus.InProgress],
                        "the status-history tab answered [] here while the audit table held both rows");

        var detail = await service.GetTaskDetailAsync(run.Id);

        detail.ShouldNotBeNull();
        detail.StatusAudits.Count.ShouldBe(2, "the detail's block reads the same source as the endpoint");
        detail.StartedAtUtc.ShouldNotBeNull("the recorded start is read from that same trail");
    }

    [Fact]
    public async Task Should_answer_the_runs_endpoint_from_the_runs_table_of_a_relational_store()
    {
        var series = NewRow(QueuedTaskStatus.Queued);
        series.IsRecurring = true;
        series.NextRunUtc  = DateTimeOffset.UtcNow.AddMinutes(1);

        await _storage.Persist(series);
        await _storage.UpdateCurrentRun(series.Id, 11, DateTimeOffset.UtcNow.AddMinutes(2), AuditLevel.Full);
        await _storage.UpdateCurrentRun(series.Id, 22, DateTimeOffset.UtcNow.AddMinutes(3), AuditLevel.Full);

        (await _storage.Get(t => t.Id == series.Id))[0].RunsAudits.ShouldBeEmpty();

        var runs = await new TaskQueryService(_storage).GetRunsAuditAsync(series.Id);

        runs.Select(r => r.ExecutionTimeMs).ShouldBe([22d, 11d], "newest run first, from the runs table");
    }

    [Fact]
    public async Task Should_report_an_average_execution_time_on_a_relational_store()
    {
        var fast = NewRow(QueuedTaskStatus.WaitingQueue);
        var slow = NewRow(QueuedTaskStatus.WaitingQueue);

        foreach (var row in new[] { fast, slow })
            await _storage.Persist(row);

        await _storage.SetCompleted(fast.Id, 100, AuditLevel.Full);
        await _storage.SetCompleted(slow.Id, 300, AuditLevel.Full);

        var overview = await new DashboardService(_storage).GetOverviewAsync(DateRange.All);

        overview.AvgExecutionTimeMs.ShouldBe(200,
            "the overview tile read the same dead navigation and answered 0.0 on every relational store");
    }

    private static QueuedTask NewRow(QueuedTaskStatus status) => new()
    {
        Id           = Guid.NewGuid(),
        Type         = typeof(SampleTask).AssemblyQualifiedName!,
        Handler      = typeof(SampleTaskHandler).AssemblyQualifiedName!,
        Request      = "{\"Message\":\"Test\"}",
        Status       = status,
        QueueName    = "default",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        AuditLevel   = (int)AuditLevel.Full
    };

    public void Dispose()
    {
        _services.Dispose();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A file the process still holds is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }
}
