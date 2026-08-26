using System.Reflection;
using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// The shape of the JSON itself, read as text rather than through a deserializer.
/// </summary>
/// <remarks>
/// Deserializing hides the one thing that matters here: the API omits a null instead of writing it
/// (<c>MonitoringJsonResultFilter</c> sets <c>JsonIgnoreCondition.WhenWritingNull</c>), so a key that is
/// ABSENT and a key that is present holding <c>null</c> both come back as a null property — while to a
/// consumer they are the difference between "this task belongs to no schedule" and "this task belongs to
/// schedule version 0". The dashboard's TypeScript is the first consumer, and it declares those fields as
/// keys of its own, so the two halves are asserted together: what the wire carries, and what
/// <c>UI/src/types/*.types.ts</c> says the wire carries.
/// </remarks>
public class JsonContractTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    private ITaskDispatcher Dispatcher => Factory.Services.GetRequiredService<ITaskDispatcher>();

    /// <summary>The fields that are present together on a row belonging to a schedule, and absent together otherwise.</summary>
    private static readonly string[] ScheduleFields =
        ["parentTaskId", "occurrenceMode", "misfirePolicy", "timeZoneId", "scheduleVersion", "nominalSlotUtc"];

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Should_leave_the_schedule_fields_out_of_a_task_that_belongs_to_no_schedule()
    {
        var taskId = await Dispatcher.Dispatch(new SampleTask("plain"));

        var detail = await GetJsonAsync($"/evertask-monitoring/api/tasks/{taskId}");

        foreach (var field in ScheduleFields)
            detail.TryGetProperty(field, out _).ShouldBeFalse(
                $"'{field}' is null on this row, and the API omits a null instead of writing it — a consumer " +
                "tells a schedule row apart by the presence of these keys");

        detail.TryGetProperty("occurrence", out _).ShouldBeFalse();
        detail.TryGetProperty("halt", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_leave_the_schedule_fields_out_of_the_list_rows_too()
    {
        var taskId = await Dispatcher.Dispatch(new SampleTask("plain in a list"));

        var page = await GetJsonAsync("/evertask-monitoring/api/tasks?pageSize=200");
        var row = page.GetProperty("items")
                      .EnumerateArray()
                      .FirstOrDefault(i => i.GetProperty("id").GetString() == taskId.ToString());

        row.ValueKind.ShouldBe(JsonValueKind.Object, "the task just dispatched is on the first page");

        foreach (var field in ScheduleFields)
            row.TryGetProperty(field, out _).ShouldBeFalse($"'{field}' is omitted on a list row too");
    }

    [Fact]
    public async Task Should_write_the_schedule_fields_on_a_schedule_and_on_its_occurrence()
    {
        // A real durable schedule with a real occurrence under it: the keys are written by the same
        // serializer that omitted them above, so the two tests together say the presence really discriminates.
        var scheduleId = await Dispatcher.Dispatch(new SampleTask("durable"), r =>
        {
            var schedule = r.Schedule().Every(1).Seconds().WithDurableOccurrences();
            schedule.MaxRuns(1);
        });

        var occurrenceId = await WaitForFirstOccurrenceAsync(scheduleId);

        var schedule = await GetJsonAsync($"/evertask-monitoring/api/tasks/{scheduleId}");
        schedule.GetProperty("occurrenceMode").GetString().ShouldBe("Durable");
        schedule.GetProperty("misfirePolicy").GetString().ShouldBe("Skip");
        schedule.GetProperty("scheduleVersion").GetInt32().ShouldBe(0,
            "a schedule nobody has rescheduled is at version 0 — written, not omitted");
        schedule.TryGetProperty("parentTaskId", out _).ShouldBeFalse("a schedule is nobody's occurrence");

        var occurrence = await GetJsonAsync($"/evertask-monitoring/api/tasks/{occurrenceId}");
        occurrence.GetProperty("parentTaskId").GetString().ShouldBe(scheduleId.ToString());
        occurrence.GetProperty("nominalSlotUtc").ValueKind.ShouldBe(JsonValueKind.String);
        occurrence.GetProperty("scheduleVersion").GetInt32().ShouldBe(0);
        occurrence.TryGetProperty("occurrence", out _).ShouldBeTrue();
    }

    private async Task<Guid> WaitForFirstOccurrenceAsync(Guid scheduleId, int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await Storage.Get(t => t.ParentTaskId == scheduleId);
            if (rows.Length > 0)
                return rows[0].Id;

            await Task.Delay(50);
        }

        throw new TimeoutException("the durable schedule never materialized its first occurrence");
    }

    /// <summary>
    /// The DTOs the dashboard does not read, and therefore does not mirror.
    /// </summary>
    /// <remarks>
    /// Named one by one so that "no interface" is a DECISION and not a silence: a DTO absent from this list
    /// and absent from the TypeScript is drift, and an entry here that gains an interface is stale. The
    /// guardian used to filter the DTO set by "has an interface", so the largest possible drift — a mirror
    /// that does not exist at all — reported zero.
    /// </remarks>
    private static readonly Dictionary<string, string> NotMirroredByTheDashboard = new(StringComparer.Ordinal)
    {
        // The dashboard never calls the three POST /management routes: its only management key is
        // RuntimeConfig.managementEnabled, and the operations belong to the application, behind its own
        // authorization (decisions §3.8).
        ["ManagementActionDto"] = "the dashboard never calls the management routes",

        // Request bodies the dashboard builds inline ({ token }), which is the one direction the browser owns:
        // "the API can omit this key" says nothing about a body the API only ever reads.
        ["MagicLinkLoginRequest"]  = "a request body the dashboard builds inline",
        ["TokenValidationRequest"] = "a request body the dashboard builds inline"
    };

    /// <summary>
    /// The dashboard's TypeScript mirrors the DTOs, and a field the API can omit has to be an OPTIONAL key
    /// there: declared required, it promises a value the wire never sends and nothing on the way in restores
    /// it. This is the assertion that fails when a nullable field is added to a DTO and mirrored as a
    /// required key — the drift that no runtime test can see, because absent and null read the same in JS —
    /// and when a DTO has no mirror at all.
    /// </summary>
    [Fact]
    public void Should_mirror_every_omittable_field_as_an_optional_key_in_the_dashboard_types()
    {
        var types = TypeScriptTypes.Load();
        var nullability = new NullabilityInfoContext();
        var drift = new List<string>();
        var mirrored = 0;

        var dtos = typeof(TaskListDto).Assembly
                                      .GetExportedTypes()
                                      .Where(t => t is { IsClass: true, IsAbstract: false }
                                                  && t.Namespace?.StartsWith("EverTask.Monitor.Api.DTOs",
                                                      StringComparison.Ordinal) == true)
                                      .OrderBy(t => t.Name, StringComparer.Ordinal)
                                      .ToArray();

        dtos.ShouldNotBeEmpty("the reflection has to find the DTOs before it can compare them to anything");

        foreach (var dto in dtos)
        {
            if (!types.Declares(dto.Name))
            {
                if (!NotMirroredByTheDashboard.ContainsKey(dto.Name))
                {
                    drift.Add($"{dto.Name} has no interface in the dashboard types: mirror it, or say here " +
                              "why the dashboard does not read it");
                }

                continue;
            }

            mirrored++;

            foreach (var property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (nullability.Create(property).ReadState != NullabilityState.Nullable)
                    continue;

                var key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

                switch (types.StateOf(dto.Name, key))
                {
                    case TypeScriptTypes.KeyState.Missing:
                        drift.Add($"{dto.Name}.{key} is nullable on the wire but the TypeScript has no such key");
                        break;
                    case TypeScriptTypes.KeyState.Required:
                        drift.Add($"{dto.Name}.{key} can be omitted by the API but the TypeScript declares it required");
                        break;
                }
            }
        }

        // The exemption list is checked from the other side too, or it becomes the place a mirrored DTO hides.
        foreach (var exempt in NotMirroredByTheDashboard)
        {
            if (types.Declares(exempt.Key))
                drift.Add($"{exempt.Key} IS mirrored now, so '{exempt.Value}' is stale: drop it from the list");

            if (dtos.All(t => t.Name != exempt.Key))
                drift.Add($"{exempt.Key} is no longer a DTO of this API: drop it from the list");
        }

        drift.ShouldBeEmpty(string.Join(Environment.NewLine, drift));

        mirrored.ShouldBeGreaterThan(dtos.Length / 2,
            "and the comparison really ran: a parser that matched nothing would report no drift either");
    }
}
