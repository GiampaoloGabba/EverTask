using EverTask.Abstractions;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Storage.SqlServer;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Respawn;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// Two hosts, one database, one durable schedule: what the unique index guarantees across processes, and what
/// EverTask deliberately does not (P8/D5).
/// </summary>
/// <remarks>
/// MATERIALIZATION is idempotent between hosts — the unique index on (schedule, slot) is a database
/// guarantee, so two materializers racing the same backlog produce one row per slot and nothing else.
/// EXECUTION is not: EverTask 4.0 contracts a single ACTIVE host, and the second test here pins that limit
/// explicitly rather than leaving it to be discovered. It is the test the distributed-execution-lease epic
/// will invert.
/// </remarks>
[Collection("DatabaseTests")]
public sealed class SqlServerDurableOccurrencesMultiHostTests : IAsyncLifetime
{
    private string _connectionString = "";

    private Respawner? _respawner;
    private readonly DurableOccurrenceRecorder _recorder = new();
    private readonly List<IHost> _hosts = [];

    public async Task InitializeAsync()
    {
        _connectionString = await SqlServerTestContainer.GetConnectionStringAsync();
        await CleanUpDatabase();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
                // A host that did not stop in time is disposed anyway: the test is over.
            }

            host.Dispose();
        }
    }

    private async Task CleanUpDatabase()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            TablesToIgnore = ["__EFMigrationsHistory"]
        });

        await _respawner.ResetAsync(connection);
    }

    /// <summary>
    /// What one host did to the shared schedule: how many times ITS materializer worked the row, and how many
    /// times it asked the database to create an occurrence.
    /// </summary>
    /// <remarks>
    /// Without it "two hosts" is only a premise. One row per slot is what the unique index guarantees on its
    /// own, so the assertion holds trivially when a single host writes every row, and the test read the same
    /// whether the second one had recovered the schedule or never seen it. Every materializer run reads the
    /// schedule's live occurrences before it plans, so counting that call is counting runs — and it is a
    /// storage call, which is what makes it per-host: each host builds its own.
    /// </remarks>
    private sealed class HostWork
    {
        private int _runs;
        private int _materializations;

        public int Runs             => Volatile.Read(ref _runs);
        public int Materializations => Volatile.Read(ref _materializations);

        public void RecordRun()             => Interlocked.Increment(ref _runs);
        public void RecordMaterialization() => Interlocked.Increment(ref _materializations);
    }

    /// <summary>A host of its own — its own scheduler, worker queues and materializer — on the shared database.</summary>
    private async Task<IHost> StartHostAsync(bool start = true, HostWork? work = null)
    {
        var host = new HostBuilder()
                   .ConfigureServices(services =>
                   {
                       services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));

                       services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(DurableProbeTask).Assembly))
                               .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

                       services.AddSingleton(_recorder);

                       if (work == null)
                           return;

                       // The real SQL Server storage, wrapped so this host's own calls are counted. Registered
                       // after AddSqlServerStorage, whose TryAddSingleton then loses the resolve to this one.
                       services.AddSingleton<ITaskStorage>(sp =>
                       {
                           var storage =
                               new FaultInjectingTaskStorage(
                                   ActivatorUtilities.CreateInstance<SqlServerTaskStorage>(sp));

                           storage.RunBefore(nameof(ITaskStorage.GetOccurrences), work.RecordRun);
                           storage.RunBefore(nameof(ITaskStorage.MaterializeOccurrence), work.RecordMaterialization);

                           return storage;
                       });
                   })
                   .Build();

        _hosts.Add(host);

        if (start)
            await host.StartAsync();

        return host;
    }

    private static RecurringTask DurableEveryMinute(int maxPending) => new()
    {
        MinuteInterval = new MinuteInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromHours(1),
            MaxOccurrences        = 50,
            OverflowPolicy        = CatchUpOverflowPolicy.Halt,
            MaxPendingOccurrences = maxPending
        }
    };

    private static async Task<Guid> SeedScheduleAsync(ITaskStorage storage, RecurringTask definition,
                                                      DateTimeOffset cursor)
    {
        var id = Guid.NewGuid();

        await storage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new DurableProbeTask("multi-host")),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(definition),
            RecurringInfo   = definition.ToString(),
            NextRunUtc      = cursor,
            CurrentRunCount = 0,
            QueueName       = "recurring",
            AuditLevel      = (int)AuditLevel.Full,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddHours(-2)
        });

        return id;
    }

    [Fact]
    public async Task Two_hosts_replaying_the_same_backlog_create_one_row_per_slot()
    {
        var firstWork  = new HostWork();
        var secondWork = new HostWork();

        var seedHost = await StartHostAsync(start: false, firstWork);
        var storage  = seedHost.Services.GetRequiredService<ITaskStorage>();

        // Forty minutes of backlog inside a one-hour window: enough that the replay is still going when the
        // second host arrives, instead of a handful of slots one host can drain before the other has finished
        // recovering.
        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(storage, DurableEveryMinute(maxPending: 10), cursor);

        // Both hosts recover the same schedule at the same time and both start materializing it.
        await Task.WhenAll(seedHost.StartAsync(), StartHostAsync(start: true, secondWork));

        await TaskWaitHelper.WaitForConditionAsync(
            () => storage.Get(t => t.ParentTaskId == scheduleId).GetAwaiter().GetResult().Length >= 5, 40000);

        await Task.Delay(3000);

        var slots = (await storage.Get(t => t.ParentTaskId == scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .ToList();

        slots.ShouldNotBeEmpty();

        // The premise, and the half the assertion below cannot state: BOTH hosts really worked this schedule.
        // One row per slot is trivially true when only one host ever writes a row, so without this the test
        // read identically whether the second host had recovered the schedule or never seen it.
        firstWork.Runs.ShouldBeGreaterThan(0, "the first host's materializer worked the schedule");
        secondWork.Runs.ShouldBeGreaterThan(0,
            "and so did the second one: a second host that recovers no durable schedule must fail this test, " +
            "not pass it silently");

        (firstWork.Materializations + secondWork.Materializations).ShouldBeGreaterThanOrEqualTo(slots.Count,
            "every row cost at least one materialization, and a slot two hosts raced for cost two");

        slots.Distinct().Count().ShouldBe(slots.Count,
            "the unique index on (schedule, slot) is what makes materialization idempotent between hosts");

        var schedule = (await storage.Get(t => t.Id == scheduleId))[0];
        schedule.CurrentRunCount.ShouldBe(slots.Count,
            "and the cursor advanced exactly once per row, however many materializers raced for it");
    }

    [Fact]
    public async Task Without_an_execution_lease_two_hosts_both_deliver_the_same_occurrence()
    {
        // The single-active-host contract of 4.0, pinned as the LIMIT it is. Materialization is safe across
        // hosts; delivery is not, because nothing claims an occurrence for one process. The distributed
        // execution lease is a separate epic, and this assertion is what it will invert.
        _recorder.Hold = TimeSpan.FromSeconds(4);

        var first   = await StartHostAsync(start: false);
        var storage = first.Services.GetRequiredService<ITaskStorage>();

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-1);
        var scheduleId = await SeedScheduleAsync(storage, DurableEveryMinute(maxPending: 1), cursor);

        await first.StartAsync();

        // Wait until exactly one occurrence exists and is being executed by the first host: only then is the
        // second host's recovery guaranteed to find a live, non-terminal occurrence.
        await TaskWaitHelper.WaitForConditionAsync(
            () => storage.Get(t => t.ParentTaskId == scheduleId).GetAwaiter().GetResult()
                         .Any(o => o.Status == QueuedTaskStatus.InProgress), 40000);

        var occurrenceId = (await storage.Get(t => t.ParentTaskId == scheduleId))
                           .Single(o => o.Status == QueuedTaskStatus.InProgress).Id;

        await StartHostAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => _recorder.Snapshot().Count(e => e.TaskId == occurrenceId) >= 2, 40000);

        _recorder.Snapshot().Count(e => e.TaskId == occurrenceId).ShouldBeGreaterThanOrEqualTo(2,
            "EverTask 4.0 contracts a single ACTIVE host: a second one recovers and re-runs an occurrence " +
            "that is already executing elsewhere. Run one active host until the execution lease exists.");
    }
}
