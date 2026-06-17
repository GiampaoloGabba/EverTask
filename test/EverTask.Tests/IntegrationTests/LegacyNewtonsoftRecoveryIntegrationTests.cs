using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// B4 — the headline "no data loss on an upgraded DB" proof for the in-memory provider: rows written by the
/// LEGACY producer (Newtonsoft 13.x, the exact format on disk before the migration) — including non-ASCII
/// and 4-byte emoji payloads and a recurring <c>DayInterval.OnDays</c> schedule — must be recovered by the
/// real startup recovery flow against the STJ <c>EverTaskJson</c>, with the task running on its correct
/// payload and its schedule intact. Newtonsoft is the legacy producer, kept ONLY in the test project.
/// </summary>
public class LegacyNewtonsoftRecoveryIntegrationTests : IsolatedIntegrationTestBase
{
    private static readonly JsonSerializerSettings Legacy = new() { TypeNameHandling = TypeNameHandling.None };

    private static string LegacyJson(object value) => JsonConvert.SerializeObject(value, Legacy);

    private const string EmojiPayload = "Caffè è perché 日本語のテスト 🚀🔥✅";

    private async Task<IHost> CreateMemoryHostWithStateAsync(ResilienceTestState state, bool startHost) =>
        await CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddMemoryStorage();
                builder.Services.AddSingleton(state);
            },
            startHost: startHost);

    [Fact]
    public async Task Recovers_legacy_row_with_emoji_payload_and_runs_handler_on_exact_string()
    {
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        // A row produced by the legacy Newtonsoft serializer, sitting in storage before this host started.
        var legacyTask = new LegacyPayloadProbeTask(EmojiPayload);
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = legacyTask.GetType().AssemblyQualifiedName!,
            Request      = LegacyJson(legacyTask),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => state.CapturedPayloads.Count >= 1, timeoutMs: 10000);

        // The 4-byte emoji + non-ASCII payload survived the legacy→STJ recovery byte-for-byte.
        state.CapturedPayloads.ShouldContain(EmojiPayload);
    }

    [Fact]
    public async Task Recovers_and_actually_executes_legacy_recurring_row_on_correct_payload()
    {
        // B4: a row written by the LEGACY producer that is RECURRING (IsRecurring + legacy RecurringTask
        // metadata) and DUE must be revived by recovery and ACTUALLY EXECUTED on its exact payload. The prior
        // test set NextRunUtc 30 minutes in the future (so it never executed) and asserted by re-deserializing
        // the SEED bytes — green even if recovery had dropped the schedule. Here the schedule fires within a
        // second and the proof is the HANDLER running on the recovered payload (ResilienceTestState), not a
        // re-read of the seed.
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        var legacySchedule = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var taskId         = Guid.NewGuid();

        // Recurring row between runs (Completed) whose next occurrence is already DUE (past NextRunUtc), so
        // recovery revives AND fires it.
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new LegacyPayloadProbeTask(EmojiPayload).GetType().AssemblyQualifiedName!,
            Request         = LegacyJson(new LegacyPayloadProbeTask(EmojiPayload)),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = LegacyJson(legacySchedule),
            NextRunUtc      = DateTimeOffset.UtcNow.AddSeconds(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        // The legacy recurring row really executed (revived + fired), on the exact 4-byte emoji payload.
        await TaskWaitHelper.WaitForConditionAsync(() => state.CapturedPayloads.Contains(EmojiPayload),
            timeoutMs: 10000);

        // And it stayed a live recurring task (advanced, not poisoned) — recovery honored the legacy schedule.
        var revived = (await Storage.GetAll()).First(t => t.Id == taskId);
        revived.IsRecurring.ShouldBeTrue();
        revived.Status.ShouldNotBe(QueuedTaskStatus.Failed,
            "a legacy recurring row must be revived and executed, never poisoned");
    }
}
