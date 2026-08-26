using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// The requests <c>docs/monitoring-api-reference.md</c> tells a consumer to send, sent.
/// </summary>
/// <remarks>
/// A query parameter the endpoint does not bind is not rejected — it is ignored, and the caller gets an
/// unfiltered page believing it is filtered. The file documented <c>status=Completed</c> in the example
/// request and in all four language examples, against an endpoint that binds <c>statuses</c>.
/// </remarks>
public class ApiReferenceRequestSampleTests : MonitoringTestBase
{
    // Worker off: the seeded rows keep the statuses they were seeded with, so a filter has something to
    // leave out and this asserts a real difference rather than an empty store.
    private static ApiReferenceDoc Doc => ApiReferenceDoc.Load();

    /// <summary>Every parameter the task list binds, as a caller writes it.</summary>
    private static HashSet<string> BoundParameters =>
        typeof(TaskFilter).GetProperties()
                          .Concat(typeof(PaginationParams).GetProperties())
                          .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Should_only_document_query_parameters_the_task_list_binds()
    {
        var bound = BoundParameters;
        var drift = Doc.TaskQueryParameterNames()
                       .Where(p => !bound.Contains(p.Name))
                       .Select(p => $"line {p.Line}: '{p.Name}' is not bound by the task list, so the " +
                                    "documented call is answered as if it had not been passed")
                       .ToArray();

        drift.ShouldBeEmpty(string.Join(Environment.NewLine, drift));
    }

    [Fact]
    public async Task Should_answer_the_documented_example_request_with_the_filter_it_documents()
    {
        var request = Doc.ExampleRequestUnder("### GET /tasks");
        request.ShouldStartWith("GET ");

        var completed = (await Storage.Get(t => t.Status == QueuedTaskStatus.Completed)).Length;
        var all       = (await Storage.GetAll()).Length;

        completed.ShouldBeGreaterThan(0);
        all.ShouldBeGreaterThan(completed, "the premise: the store also holds rows the filter must leave out");

        var response = await Client.GetAsync(request["GET ".Length..]);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await DeserializeResponseAsync<TasksPagedResponse>(response);

        page.ShouldNotBeNull();
        page.TotalCount.ShouldBe(completed, "the documented request asks for the completed tasks only");
        page.Items.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);
    }

    /// <summary>
    /// Every JSON sample in the file parses, and every identifier in one is really an identifier: a sample is
    /// copied into a test fixture as it stands, and one of the documented task ids had a group too many.
    /// </summary>
    [Fact]
    public void Should_document_json_samples_that_parse_and_carry_real_identifiers()
    {
        var malformed = new List<string>();

        foreach (var (line, sample) in Doc.JsonSamples())
            CollectMalformedIdentifiers(sample, line, malformed);

        malformed.ShouldBeEmpty(string.Join(Environment.NewLine, malformed));
    }

    private static void CollectMalformedIdentifiers(JsonElement element, int line, List<string> malformed)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    CollectMalformedIdentifiers(property.Value, line, malformed);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectMalformedIdentifiers(item, line, malformed);
                break;

            case JsonValueKind.String:
                var value = element.GetString()!;

                // Guid-shaped: hexadecimal and hyphens, long enough that nothing else in the file looks
                // like one. A timestamp carries a 'T' and colons, a zone id a slash.
                if (value.Length >= 20 && value.All(c => Uri.IsHexDigit(c) || c == '-') && !Guid.TryParse(value, out _))
                    malformed.Add($"the JSON sample at line {line} documents '{value}' as an id, and it is not a Guid");
                break;
        }
    }
}
