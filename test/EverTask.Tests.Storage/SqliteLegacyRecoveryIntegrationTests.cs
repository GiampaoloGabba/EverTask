using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Storage.Sqlite;
using EverTask.Tests;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// B4: full-host legacy→STJ recovery against a REAL SQLite database (no Docker), the SQLite counterpart of
/// <see cref="SqlServerRecoveryIntegrationTests"/>. Rows written by the LEGACY producer (Newtonsoft 13.x, the
/// exact on-disk format before the migration) — a 4-byte emoji payload and a recurring schedule — are recovered
/// by the actual startup flow and EXECUTED on their correct payload/schedule. Proves no data loss / no encoding
/// corruption on an upgraded SQLite database, observed through the handler (not a re-read of the seed bytes).
/// </summary>
[Collection("DatabaseTests")]
public sealed class SqliteLegacyRecoveryIntegrationTests : IsolatedIntegrationTestBase, IDisposable
{
    private static readonly JsonSerializerSettings Legacy = new() { TypeNameHandling = TypeNameHandling.None };
    private static string LegacyJson(object value) => JsonConvert.SerializeObject(value, Legacy);

    private const string EmojiPayload = "Caffè è perché 日本語のテスト 🚀🔥✅";

    private readonly string _dbFile = $"LegacyRec_{Guid.NewGuid():N}.db";
    private readonly string _connectionString;
    private readonly ResilienceTestState _state = new();

    public SqliteLegacyRecoveryIntegrationTests() => _connectionString = $"Data Source={_dbFile}";

    private Task<IHost> CreateSqliteHostAsync(bool startHost) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddSqliteStorage(_connectionString, opt => opt.AutoApplyMigrations = true);
                builder.Services.AddSingleton(_state);
            },
            startHost: startHost);

    [Fact]
    public async Task Recovers_and_executes_legacy_emoji_payload_against_real_sqlite()
    {
        await CreateSqliteHostAsync(startHost: false);

        var legacyTask = new LegacyPayloadProbeTask(EmojiPayload);
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = legacyTask.GetType().AssemblyQualifiedName!,
            Request      = LegacyJson(legacyTask), // legacy Newtonsoft format on disk
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _state.CapturedPayloads.Contains(EmojiPayload),
            timeoutMs: 15000);

        _state.CapturedPayloads.ShouldContain(EmojiPayload,
            "the 4-byte emoji payload must survive the real-SQLite legacy→STJ recovery and reach the handler");
    }

    [Fact]
    public async Task Recovers_and_executes_legacy_recurring_row_against_real_sqlite()
    {
        await CreateSqliteHostAsync(startHost: false);

        var legacySchedule = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var taskId         = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new LegacyPayloadProbeTask(EmojiPayload).GetType().AssemblyQualifiedName!,
            Request         = LegacyJson(new LegacyPayloadProbeTask(EmojiPayload)),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = LegacyJson(legacySchedule),
            NextRunUtc      = DateTimeOffset.UtcNow.AddSeconds(-1), // already due → recovery revives and fires
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _state.CapturedPayloads.Contains(EmojiPayload),
            timeoutMs: 15000);

        var revived = (await Storage.GetAll()).First(t => t.Id == taskId);
        revived.IsRecurring.ShouldBeTrue();
        revived.Status.ShouldNotBe(QueuedTaskStatus.Failed,
            "a legacy recurring row must be revived and executed against real SQLite, never poisoned");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { /* best-effort cleanup */ }
    }
}
