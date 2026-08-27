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

        statusAudits.Audits.Select(a => a.NewStatus)
                    .ShouldBe([QueuedTaskStatus.Completed, QueuedTaskStatus.InProgress],
                        "the status-history tab answered [] here while the audit table held both rows");
        statusAudits.TotalCount.ShouldBe(2);

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

        runs.Audits.Select(r => r.ExecutionTimeMs).ShouldBe([22d, 11d], "newest run first, from the runs table");
        runs.TotalCount.ShouldBe(2);
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

    [Fact]
    public async Task Should_page_the_status_trail_in_the_database_of_a_relational_store()
    {
        // The whole point of the paged read (#44): the count and the slice are the DATABASE's, over the
        // (QueuedTaskId) index the audit table already has, so a row with a year of transitions behind it is
        // never transferred whole to show twenty of them.
        var row = NewRow(QueuedTaskStatus.WaitingQueue);
        await _storage.Persist(row);

        for (var i = 0; i < 10; i++)
        {
            await _storage.SetInProgress(row.Id, AuditLevel.Full);
            await _storage.SetCompleted(row.Id, 10 + i, AuditLevel.Full);
        }

        var walked = new List<long>();

        for (var skip = 0; skip < 20; skip += 5)
        {
            var page = await _storage.GetStatusAuditsPage(row.Id, skip, 5);

            page.TotalCount.ShouldBe(20, "the total is the trail, not the page");
            page.Audits.Length.ShouldBe(5);
            walked.AddRange(page.Audits.Select(a => a.Id));
        }

        walked.ShouldBe(walked.OrderByDescending(id => id).ToList(), "newest first across the pages");
        walked.Distinct().Count().ShouldBe(20, "no page repeated or dropped an entry");

        var whole = await _storage.GetStatusAuditsPage(row.Id, 0, int.MaxValue);
        walked.ShouldBe(whole.Audits.Select(a => a.Id).ToList(),
            "the paged read walks exactly the order a full page answers");
    }

    [Fact]
    public async Task Should_page_the_runs_trail_in_the_database_of_a_relational_store()
    {
        var series = NewRow(QueuedTaskStatus.Queued);
        series.IsRecurring = true;
        series.NextRunUtc  = DateTimeOffset.UtcNow.AddMinutes(1);

        await _storage.Persist(series);

        for (var i = 1; i <= 6; i++)
            await _storage.UpdateCurrentRun(series.Id, i * 10, DateTimeOffset.UtcNow.AddMinutes(i), AuditLevel.Full);

        var page = await _storage.GetRunsAuditsPage(series.Id, 2, 3);

        page.TotalCount.ShouldBe(6);
        page.Audits.Select(r => r.ExecutionTimeMs).ShouldBe([40d, 30d, 20d], "newest run first, from row three on");

        var countOnly = await _storage.GetRunsAuditsPage(series.Id, 0, 0);

        countOnly.Audits.ShouldBeEmpty();
        countOnly.TotalCount.ShouldBe(6, "take = 0 asks for the count alone, and never as a zero-row FETCH");
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
