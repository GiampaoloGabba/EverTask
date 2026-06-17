using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
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
    public async Task Recovers_legacy_recurring_row_preserving_DayInterval_OnDays_schedule()
    {
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        var original = new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Friday })
            {
                OnTimes = new[] { new TimeOnly(9, 0) }
            }
        };
        var taskId = Guid.NewGuid();

        // Recurring row between runs (Completed + future NextRunUtc) → revived by recovery.
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new LegacyPayloadProbeTask("ignored").GetType().AssemblyQualifiedName!,
            Request         = LegacyJson(new LegacyPayloadProbeTask("ignored")),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = LegacyJson(original),
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(30),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        // The recurring row is revived (still recoverable, not poisoned) after startup recovery.
        var revived = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).FirstOrDefault(t => t.Id == taskId),
            t => t is { IsRecurring: true } && t.Status != QueuedTaskStatus.Failed,
            timeoutMs: 10000);

        revived.ShouldNotBeNull();

        // No schedule loss: the persisted (legacy) recurring metadata still deserializes under STJ with the
        // OnDays constraint intact and the SAME next occurrence as before the migration.
        var restored = EverTaskJson.Deserialize<RecurringTask>(revived!.RecurringTask!)!;
        restored.DayInterval.ShouldNotBeNull();
        restored.DayInterval!.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Friday });

        var anchor = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        restored.DayInterval.GetNextOccurrence(anchor)
            .ShouldBe(original.DayInterval!.GetNextOccurrence(anchor));
    }
}
