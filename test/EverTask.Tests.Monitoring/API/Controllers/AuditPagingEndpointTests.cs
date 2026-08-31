using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// The paging of the two audit trails of the task detail (#44).
/// </summary>
/// <remarks>
/// Both reads answered the WHOLE history of a row, and the detail served both of them whole: a long-lived
/// recurring row accumulates one transition per state per run, so opening the detail of an old schedule
/// transferred its entire history to show the first twenty rows of it. The same asymmetry
/// <c>GetOccurrencesPage</c> had already resolved on the other side of the detail.
/// <para>
/// Nothing here asserts on the audit <c>id</c>: this suite runs over the in-memory store, which keeps the
/// audits on the row object and never assigns them an identity. What the ORDER is asserted against is the
/// full page from the same storage — the sequence the endpoint has always answered — so the assertion means
/// the same thing on every backend. The identity ordering itself is pinned where it exists, on a real
/// relational store, by <c>API/Services/RelationalAuditReadTests</c>.
/// </para>
/// </remarks>
public class AuditPagingEndpointTests : MonitoringTestBase
{
    private const string BasePath = "/evertask-monitoring/api";

    /// <summary>Twenty transitions on one row, as the issue asks for.</summary>
    private const int Transitions = 20;

    /// <summary>Six runs, so a page can start in the middle of the trail and end before it does.</summary>
    private const int Runs = 6;

    [Fact]
    public async Task Should_answer_one_page_of_the_status_trail_and_a_total_that_stays_whole()
    {
        var taskId = await SeedTransitionsAsync();

        var page = await GetAsync<StatusAuditsResponse>($"{BasePath}/tasks/{taskId}/status-audit?skip=0&take=5");

        page.Audits.Count.ShouldBe(5, "a page of five is five rows");
        page.TotalCount.ShouldBe(Transitions, "the total is the whole trail, not the page");
        page.Skip.ShouldBe(0);
        page.Take.ShouldBe(5);
    }

    [Fact]
    public async Task Should_keep_the_newest_first_order_across_the_pages_of_the_status_trail()
    {
        var taskId = await SeedTransitionsAsync();

        var walked = new List<string?>();

        for (var skip = 0; skip < Transitions; skip += 5)
        {
            var page = await GetAsync<StatusAuditsResponse>(
                $"{BasePath}/tasks/{taskId}/status-audit?skip={skip}&take=5");

            page.TotalCount.ShouldBe(Transitions);
            page.Audits.Count.ShouldBe(5);
            walked.AddRange(page.Audits.Select(a => a.Exception));
        }

        var whole = (await Storage.GetStatusAuditsPage(taskId, 0, int.MaxValue)).Audits
            .Select(a => a.Exception).ToList();

        walked.ShouldBe(whole,
            "the pages walked, in order, are exactly the trail a full page answers — nothing repeated, " +
            "nothing dropped, and still newest first");
    }

    [Fact]
    public async Task Should_answer_an_empty_page_past_the_end_of_the_trail()
    {
        var taskId = await SeedTransitionsAsync();

        var page = await GetAsync<StatusAuditsResponse>(
            $"{BasePath}/tasks/{taskId}/status-audit?skip={Transitions + 10}&take=5");

        page.Audits.ShouldBeEmpty();
        page.TotalCount.ShouldBe(Transitions, "the trail is still what it was; the reader is past its end");
    }

    [Fact]
    public async Task Should_clamp_a_negative_page_instead_of_handing_it_to_the_storage()
    {
        // These are query-string values, so they are whatever a caller typed, and they end up in an
        // OFFSET / FETCH clause where a negative one is a database error rather than an empty page.
        var taskId = await SeedTransitionsAsync();

        var negativeSkip = await GetAsync<StatusAuditsResponse>(
            $"{BasePath}/tasks/{taskId}/status-audit?skip=-5&take=5");

        negativeSkip.Skip.ShouldBe(0);
        negativeSkip.Audits.Count.ShouldBe(5);

        var negativeTake = await GetAsync<StatusAuditsResponse>(
            $"{BasePath}/tasks/{taskId}/status-audit?skip=0&take=-1");

        negativeTake.Take.ShouldBe(0);
        negativeTake.Audits.ShouldBeEmpty();
        negativeTake.TotalCount.ShouldBe(Transitions, "take = 0 asks for the count alone");
    }

    [Fact]
    public async Task Should_default_to_one_page_when_the_caller_names_none()
    {
        var taskId = await SeedTransitionsAsync();

        var page = await GetAsync<StatusAuditsResponse>($"{BasePath}/tasks/{taskId}/status-audit");

        page.Skip.ShouldBe(0);
        page.Take.ShouldBe(ITaskQueryService.DefaultAuditPageSize);
        page.Audits.Count.ShouldBe(Transitions, "twenty transitions fit in the default page");
    }

    [Fact]
    public async Task Should_page_the_runs_trail_the_same_way()
    {
        var taskId = await SeedRunsAsync();

        var page = await GetAsync<RunsAuditsResponse>($"{BasePath}/tasks/{taskId}/runs-audit?skip=2&take=3");

        page.TotalCount.ShouldBe(Runs);
        page.Skip.ShouldBe(2);
        page.Audits.Select(a => a.ExecutionTimeMs)
            .ShouldBe([40d, 30d, 20d], "newest run first, and the page really started at the third of them");

        var first = await GetAsync<RunsAuditsResponse>($"{BasePath}/tasks/{taskId}/runs-audit?skip=0&take=3");

        first.Audits.Select(a => a.ExecutionTimeMs).ShouldBe([60d, 50d, 40d]);
    }

    [Fact]
    public async Task Should_carry_the_first_page_and_the_totals_on_the_task_detail()
    {
        var taskId = await SeedTransitionsAsync();

        var detail = await GetAsync<TaskDetailDto>($"{BasePath}/tasks/{taskId}");

        detail.StatusAuditsTotalCount.ShouldBe(Transitions,
            "the block is a page now, so the total is what tells a consumer there is more");
        detail.StatusAudits.Count.ShouldBe(Math.Min(Transitions, ITaskQueryService.DefaultAuditPageSize));

        var endpoint = await GetAsync<StatusAuditsResponse>($"{BasePath}/tasks/{taskId}/status-audit");

        detail.StatusAudits.Select(a => a.Exception).ShouldBe(endpoint.Audits.Select(a => a.Exception),
            "the block and the endpoint read the same source, in the same order, page for page");
    }

    /// <summary>
    /// A row with twenty recorded transitions, written through the storage that records them. Each carries a
    /// distinct exception text, which is what makes one transition tellable from another on a store that
    /// assigns the audits no identity.
    /// </summary>
    private async Task<Guid> SeedTransitionsAsync()
    {
        var taskId = Guid.NewGuid();

        await Storage.Persist(NewRow(taskId));

        for (var i = 0; i < Transitions; i++)
        {
            await Storage.SetStatus(taskId, QueuedTaskStatus.Failed,
                new InvalidOperationException($"transition-{i:D2}"), AuditLevel.Full);
        }

        return taskId;
    }

    /// <summary>A recurring row with six recorded runs, each of a different measured duration.</summary>
    private async Task<Guid> SeedRunsAsync()
    {
        var taskId = Guid.NewGuid();
        var row    = NewRow(taskId);

        row.IsRecurring = true;
        row.NextRunUtc  = DateTimeOffset.UtcNow.AddMinutes(1);

        await Storage.Persist(row);

        for (var i = 1; i <= Runs; i++)
            await Storage.UpdateCurrentRun(taskId, i * 10, DateTimeOffset.UtcNow.AddMinutes(i), AuditLevel.Full);

        return taskId;
    }

    private static QueuedTask NewRow(Guid id) => new()
    {
        Id           = id,
        Type         = "EverTask.Tests.Monitoring.TestData.SampleTask",
        Request      = "{}",
        Handler      = "EverTask.Tests.Monitoring.TestData.SampleTaskHandler",
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
        Status       = QueuedTaskStatus.Queued
    };

    private async Task<T> GetAsync<T>(string url) where T : class
    {
        var response = await Client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await DeserializeResponseAsync<T>(response);

        payload.ShouldNotBeNull();
        return payload;
    }
}
