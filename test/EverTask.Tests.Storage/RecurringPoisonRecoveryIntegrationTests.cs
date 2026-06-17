using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Storage.Sqlite;
using EverTask.Storage.SqlServer;
using EverTask.Tests;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Respawn;
using Shouldly;
using Testcontainers.MsSql;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// B1 (P0-1 / P0-2) end-to-end on REAL relational storage: a recurring row that is POISONED during startup
/// recovery must be TERMINAL — Failed AND <see cref="QueuedTask.NextRunUtc"/> cleared — so that across a REAL
/// host restart on the SAME database it is neither resurrected/re-poisoned nor re-executed as a one-shot.
///
/// Driven through the real <c>WorkerService</c> recovery flow against a real DB (no mocks): the corrupt/missing
/// row is seeded, host 1 starts (recovery poisons), then host 2 starts on the SAME storage (a real restart) and
/// the OBSERVED outcome is asserted. The recurring handler must never run; a sentinel one-shot proves recovery
/// actually executed on the restart. These FAIL on the pre-B1 code (the poison path did not clear NextRunUtc /
/// the guard demoted a metadata-less recurring row to a one-shot).
/// </summary>
public abstract class RecurringPoisonRecoveryIntegrationTestsBase : IsolatedIntegrationTestBase
{
    protected readonly ResilienceTestState State = new();

    /// <summary>The recurring marker index pushed by <see cref="ResilienceRecurringTaskHandler"/>.</summary>
    private const int RecurringMarker = -1;

    protected abstract void ConfigureStorage(EverTaskServiceBuilder builder);

    protected virtual int RecoveryTimeoutMs => 15000;

    private Task<IHost> CreateHostAsync(bool startHost) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                ConfigureStorage(builder);
                builder.Services.AddSingleton(State);
            },
            startHost: startHost);

    private async Task SeedSentinelAndWaitRecoveryRanAsync(int sentinelIndex)
    {
        await CreateHostAsync(startHost: false);
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = new ResilienceCounterTask(sentinelIndex).GetType().AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new ResilienceCounterTask(sentinelIndex)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => State.ExecutedIndexes.Contains(sentinelIndex), timeoutMs: RecoveryTimeoutMs);
    }

    [Fact]
    public async Task Corrupt_recurring_metadata_is_poisoned_terminally_and_not_revived_across_restart()
    {
        await CreateHostAsync(startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceRecurringTask().GetType().AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = "{ this-is-corrupt-recurring-metadata",
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).First(t => t.Id == taskId),
            t => t.Status == QueuedTaskStatus.Failed,
            timeoutMs: RecoveryTimeoutMs);

        poisoned.NextRunUtc.ShouldBeNull(
            "a poisoned recurring row must have NextRunUtc cleared so it is not revived at the next restart (P0-1)");
        poisoned.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeFalse(
            "a terminally poisoned recurring row must no longer satisfy the recoverable predicate (P0-1)");
        State.ExecutedIndexes.ShouldNotContain(RecurringMarker);

        // Host 2 (real restart on the SAME database): a sentinel proves recovery ran; the corrupt row stays terminal.
        await SeedSentinelAndWaitRecoveryRanAsync(sentinelIndex: 7770);

        State.ExecutedIndexes.ShouldNotContain(RecurringMarker,
            "the poisoned recurring row must not be re-executed at the next restart (P0-1)");
        var afterRestart = (await Storage.GetAll()).First(t => t.Id == taskId);
        afterRestart.Status.ShouldBe(QueuedTaskStatus.Failed);
        afterRestart.NextRunUtc.ShouldBeNull();
    }

    /// <summary>
    /// B2: recurring metadata that DESERIALIZES but carries a corrupt SCHEDULE (a cron string that does not
    /// parse, an out-of-range OnDays/OnHours, a negative Interval) must be validated right after the recovery
    /// deserialize and routed to the SAME terminal poison as un-deserializable metadata — instead of throwing
    /// downstream at next-run (a per-restart bounded failure) or producing a wrong schedule. Depends on B1:
    /// the validation throw lands in the recurring poison path that clears NextRunUtc.
    /// </summary>
    private async Task AssertCorruptScheduleIsPoisonedTerminally(RecurringTask corruptSchedule)
    {
        await CreateHostAsync(startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceRecurringTask().GetType().AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(corruptSchedule),
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).First(t => t.Id == taskId),
            t => t.Status == QueuedTaskStatus.Failed,
            timeoutMs: RecoveryTimeoutMs);

        poisoned.NextRunUtc.ShouldBeNull(
            "a corrupt-schedule recurring row must be poisoned terminally on the first recovery (NextRunUtc cleared)");
        poisoned.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeFalse();
        State.ExecutedIndexes.ShouldNotContain(RecurringMarker,
            "a corrupt schedule must not be executed (right or wrong) before being poisoned");
    }

    [Fact]
    public Task Corrupt_cron_schedule_is_poisoned_terminally() =>
        AssertCorruptScheduleIsPoisonedTerminally(new RecurringTask
        {
            CronInterval = new CronInterval("this is not a cron")
        });

    [Fact]
    public Task Out_of_range_OnDays_schedule_is_poisoned_terminally() =>
        AssertCorruptScheduleIsPoisonedTerminally(new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { (DayOfWeek)99 })
        });

    [Fact]
    public Task Out_of_range_OnHours_schedule_is_poisoned_terminally() =>
        AssertCorruptScheduleIsPoisonedTerminally(new RecurringTask
        {
            HourInterval = new HourInterval(0, new[] { 99 })
        });

    [Fact]
    public async Task Missing_recurring_metadata_is_poisoned_not_run_as_one_shot()
    {
        await CreateHostAsync(startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceCounterTask(55).GetType().AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceCounterTask(55)),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = null, // missing metadata: cannot be reconstructed as a recurring schedule
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).First(t => t.Id == taskId),
            t => t.Status == QueuedTaskStatus.Failed,
            timeoutMs: RecoveryTimeoutMs);

        poisoned.NextRunUtc.ShouldBeNull(
            "a recurring row with missing metadata must be poisoned terminally, not demoted to a one-shot (P0-2)");
        State.ExecutedIndexes.ShouldNotContain(55,
            "a recurring row with missing metadata must NOT be silently executed as a one-shot (P0-2)");
    }
}

/// <summary>SQLite full-host (file-based, no Docker) variant of the B1 recovery-poison gate.</summary>
[Collection("DatabaseTests")]
public sealed class SqliteRecurringPoisonRecoveryTests : RecurringPoisonRecoveryIntegrationTestsBase, IDisposable
{
    private readonly string _dbFile          = $"PoisonRec_{Guid.NewGuid():N}.db";
    private readonly string _connectionString;

    public SqliteRecurringPoisonRecoveryTests() => _connectionString = $"Data Source={_dbFile}";

    protected override void ConfigureStorage(EverTaskServiceBuilder builder) =>
        builder.AddSqliteStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { /* best-effort cleanup */ }
    }
}

/// <summary>SQL Server full-host (Testcontainers) variant of the B1 recovery-poison gate.</summary>
[Collection("DatabaseTests")]
public sealed class SqlServerRecurringPoisonRecoveryTests : RecurringPoisonRecoveryIntegrationTestsBase, IAsyncLifetime
{
    private static MsSqlContainer? _sqlContainer;
    private static bool _containerInitialized;
    private static readonly object _lock = new();
    private static string _connectionString = "";
    private Respawner? _respawner;

    protected override int RecoveryTimeoutMs => 30000;

    protected override void ConfigureStorage(EverTaskServiceBuilder builder) =>
        builder.AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

    public async Task InitializeAsync()
    {
        lock (_lock)
        {
            if (!_containerInitialized)
            {
                _sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                _sqlContainer.StartAsync().GetAwaiter().GetResult();
                _connectionString = _sqlContainer.GetConnectionString();
                _containerInitialized = true;
            }
        }

        await CleanUpDatabase();
    }

    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private async Task CleanUpDatabase()
    {
        if (string.IsNullOrEmpty(_connectionString))
            return;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            TablesToIgnore = ["__EFMigrationsHistory"]
        });

        await _respawner.ResetAsync(connection);
    }
}
