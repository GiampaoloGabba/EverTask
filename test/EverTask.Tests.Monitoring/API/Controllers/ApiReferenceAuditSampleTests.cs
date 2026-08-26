using System.Reflection;
using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// The response samples of the two audit endpoints in <c>docs/monitoring-api-reference.md</c>, held against
/// the payload those endpoints really send.
/// </summary>
/// <remarks>
/// A sample is the only specification most consumers ever read, and one that names keys the API does not
/// send costs them the value: the file documented <c>taskId</c>, <c>oldStatus</c>, <c>changedAtUtc</c>,
/// <c>errorDetails</c>, <c>executionStartedUtc</c> and <c>executionCompletedUtc</c> against DTOs that carry
/// <c>queuedTaskId</c>, <c>updatedAtUtc</c>, <c>newStatus</c>, <c>executedAt</c>, <c>executionTimeMs</c> and
/// <c>exception</c>. Nothing failed while it said so, because nothing compared the two.
/// </remarks>
public class ApiReferenceAuditSampleTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    private static ApiReferenceDoc Doc => ApiReferenceDoc.Load();

    private ITaskDispatcher Dispatcher => Factory.Services.GetRequiredService<ITaskDispatcher>();

    [Fact]
    public async Task Should_document_the_status_audit_response_the_api_really_sends()
    {
        var taskId = await RunOneRecurringRunAsync();

        var live = await GetJsonAsync($"/evertask-monitoring/api/tasks/{taskId}/status-audit");

        ShouldDocumentTheSamePayload(
            Doc.JsonSampleUnder("### GET /tasks/{id}/status-audit"), live, typeof(StatusAuditDto));
    }

    [Fact]
    public async Task Should_document_the_runs_audit_response_the_api_really_sends()
    {
        var taskId = await RunOneRecurringRunAsync();

        var live = await GetJsonAsync($"/evertask-monitoring/api/tasks/{taskId}/runs-audit");

        ShouldDocumentTheSamePayload(
            Doc.JsonSampleUnder("### GET /tasks/{id}/runs-audit"), live, typeof(RunsAuditDto));
    }

    /// <summary>
    /// The sample names only keys the DTO has, shows every key the DTO always writes, hides nothing the live
    /// payload carries, and reads newest first — the order both endpoints answer in and the prose promises.
    /// </summary>
    private static void ShouldDocumentTheSamePayload(JsonElement sample, JsonElement live, Type dto)
    {
        sample.ValueKind.ShouldBe(JsonValueKind.Array, "the endpoint answers a list");
        live.ValueKind.ShouldBe(JsonValueKind.Array);

        var documented = sample.EnumerateArray().ToArray();
        var sent       = live.EnumerateArray().ToArray();

        documented.ShouldNotBeEmpty("a sample of an empty list documents nothing");
        sent.ShouldNotBeEmpty("the premise: the task really ran, so it has audits to answer with");

        var known    = KeysOf(dto, onlyAlwaysWritten: false);
        var written  = KeysOf(dto, onlyAlwaysWritten: true);
        var occurred = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in documented)
        {
            var keys = KeysOf(entry);
            occurred.UnionWith(keys);

            foreach (var key in keys)
                known.ShouldContain(key, $"'{key}' is documented, but {dto.Name} has no such property");

            foreach (var key in written)
                keys.ShouldContain(key, $"'{key}' is on every response {dto.Name} serializes, and the sample omits it");

            var id = entry.GetProperty("queuedTaskId").GetString();
            Guid.TryParse(id, out _).ShouldBeTrue($"'{id}' is documented as a task id and is not a Guid");
        }

        documented.Select(e => e.GetProperty("queuedTaskId").GetString())
                  .Distinct(StringComparer.Ordinal)
                  .Count()
                  .ShouldBe(1, "the audits of ONE task are what the endpoint answers");

        foreach (var entry in sent)
        {
            foreach (var key in KeysOf(entry))
                occurred.ShouldContain(key, $"the API sends '{key}' and no sample shows it");
        }

        ShouldReadNewestFirst(documented, "the sample");
        ShouldReadNewestFirst(sent, "the response");
    }

    /// <summary>
    /// Both audit tables are ordered on their identity, which grows with every transition, so the newest
    /// entry is the one with the highest id.
    /// </summary>
    private static void ShouldReadNewestFirst(JsonElement[] entries, string what)
    {
        var ids = entries.Select(e => e.GetProperty("id").GetInt64()).ToArray();

        ids.ShouldBe(ids.OrderByDescending(id => id).ToArray(), $"{what} is ordered newest first");
    }

    private static string[] KeysOf(JsonElement entry) =>
        entry.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>
    /// The DTO's properties as the API writes them: camelCase, and — when <paramref name="onlyAlwaysWritten"/>
    /// — only the ones that can never be null, since a null is omitted rather than written.
    /// </summary>
    private static HashSet<string> KeysOf(Type dto, bool onlyAlwaysWritten)
    {
        var nullability = new NullabilityInfoContext();

        var properties = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => !onlyAlwaysWritten
                                        || nullability.Create(p).ReadState != NullabilityState.Nullable);

        return properties.Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
                         .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// A recurring task allowed exactly one run: it is the shape that fills BOTH audit tables, since a run is
    /// written only where a schedule advances past it.
    /// </summary>
    private async Task<Guid> RunOneRecurringRunAsync(int timeoutMs = 30000)
    {
        var taskId = await Dispatcher.Dispatch(new SampleRecurringTask("api reference sample"),
            r => r.Schedule().Every(1).Seconds().MaxRuns(1));

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var runs = await Storage.GetRunsAudits(taskId);
            if (runs.Length > 0)
                return taskId;

            await Task.Delay(50);
        }

        throw new TimeoutException("the recurring task never completed its single run");
    }
}
