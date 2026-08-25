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

    /// <summary>A host of its own — its own scheduler, worker queues and materializer — on the shared database.</summary>
    private async Task<IHost> StartHostAsync(bool start = true)
    {
        var host = new HostBuilder()
                   .ConfigureServices(services =>
                   {
                       services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));

                       services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(DurableProbeTask).Assembly))
                               .AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);

                       services.AddSingleton(_recorder);
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
        var seedHost = await StartHostAsync(start: false);
        var storage  = seedHost.Services.GetRequiredService<ITaskStorage>();

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-6);
        var scheduleId = await SeedScheduleAsync(storage, DurableEveryMinute(maxPending: 10), cursor);

        // Both hosts recover the same schedule at the same time and both start materializing it.
        await Task.WhenAll(seedHost.StartAsync(), StartHostAsync());

        await TaskWaitHelper.WaitForConditionAsync(
            () => storage.Get(t => t.ParentTaskId == scheduleId).GetAwaiter().GetResult().Length >= 5, 40000);

        await Task.Delay(3000);

        var slots = (await storage.Get(t => t.ParentTaskId == scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .ToList();

        slots.ShouldNotBeEmpty();
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
