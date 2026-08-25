using EverTask.Abstractions;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Sqlite;
using EverTask.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// The catch-up as it really happens: a schedule whose cursor is left behind by a downtime, brought back by
/// the actual startup recovery against a REAL SQLite database. Every row here goes through the real schema —
/// the self-referencing foreign key, the check constraint and the unique index on (schedule, slot) — so a
/// replay that invented a slot, or replayed one twice, would fail on the database and not on a mock.
/// </summary>
/// <remarks>
/// The downtime is SEEDED, because that is the only honest way to have one: the rows are what a process that
/// stopped would have left behind. The restart is a second host on the same file.
/// </remarks>
[Collection("DatabaseTests")]
public sealed class CatchUpRecoveryIntegrationTests : IsolatedIntegrationTestBase, IDisposable
{
    private readonly string _dbFile = $"CatchUp_{Guid.NewGuid():N}.db";
    private readonly string _connectionString;
    private readonly DurableOccurrenceRecorder _recorder = new();

    public CatchUpRecoveryIntegrationTests() => _connectionString = $"Data Source={_dbFile}";

    private Task<IHost> CreateSqliteHostAsync(bool startHost,
                                              Action<EverTaskServiceConfiguration>? configure = null) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddSqliteStorage(_connectionString, opt => opt.AutoApplyMigrations = true);
                builder.Services.AddSingleton(_recorder);
            },
            startHost: startHost,
            configureEverTask: configure);

    private static RecurringTask Every(int minutes, MisfireSettings misfire) => new()
    {
        MinuteInterval = new MinuteInterval(minutes),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    /// <summary>
    /// An HOURLY grid, which is what lets a test on a real clock name the slots it expects. On a one-minute
    /// grid the boundary of the age window sits half a minute from a slot, so a test that takes a moment
    /// longer than usual replays one slot more — which is why the assertions below are exact and the ones on
    /// a minute grid could only ever be approximate.
    /// </summary>
    private static RecurringTask Hourly(MisfireSettings misfire) => new()
    {
        HourInterval   = new HourInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    private static MisfireSettings CatchUp(TimeSpan maxAge, int maxOccurrences, int maxPending = 1) => new()
    {
        Policy                = MisfirePolicy.CatchUp,
        MaxAge                = maxAge,
        MaxOccurrences        = maxOccurrences,
        OverflowPolicy        = CatchUpOverflowPolicy.Halt,
        MaxPendingOccurrences = maxPending
    };

    private async Task<Guid> SeedScheduleAsync(RecurringTask definition, DateTimeOffset cursor)
    {
        var id = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new DurableProbeTask("downtime")),
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

    private Task<QueuedTask[]> OccurrencesOfAsync(Guid scheduleId) =>
        Storage.Get(t => t.ParentTaskId == scheduleId);

    [Fact]
    public async Task Replays_exactly_the_slots_inside_the_window_oldest_first()
    {
        // One consumer: the scheduler hands overdue occurrences over in slot order, so with a single consumer
        // the order they RUN in is the order they were owed in — which is the half of "oldest first" that the
        // rows alone cannot show.
        await CreateSqliteHostAsync(startHost: false, configure: cfg => cfg.SetMaxDegreeOfParallelism(1));

        // Five hours of downtime against a two-and-a-half-hour window: the three most recent slots are owed
        // and the three before them are dropped — the one ordinary way a durable schedule loses work. Every
        // boundary sits half an hour from a slot, so the answer is exact and stays exact while the test runs.
        var cursor = DateTimeOffset.UtcNow.AddHours(-5);
        var scheduleId = await SeedScheduleAsync(
            Hourly(CatchUp(TimeSpan.FromMinutes(150), 50, maxPending: 10)), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 30000);
        await Task.Delay(1000);

        var expected = new[] { cursor.AddHours(3), cursor.AddHours(4), cursor.AddHours(5) };

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .ToArray();

        slots.ShouldBe(expected, "exactly the slots inside the window, and no slot invented outside it");

        _recorder.Snapshot().Select(e => e.SlotUtc).ShouldBe(expected.Cast<DateTimeOffset?>(),
            "and they run oldest first, each occurrence standing for its own slot");
    }

    [Fact]
    public async Task A_fire_once_downtime_leaves_one_occurrence_carrying_the_whole_range()
    {
        await CreateSqliteHostAsync(startHost: false);

        // Six hours of downtime on an hourly grid: seven slots were owed, and FireOnce owes exactly one row.
        var cursor = DateTimeOffset.UtcNow.AddHours(-6);
        var scheduleId = await SeedScheduleAsync(
            Hourly(new MisfireSettings { Policy = MisfirePolicy.FireOnce }), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 30000);
        await Task.Delay(1000);

        var occurrence = (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem();
        occurrence.ScheduledExecutionUtc.ShouldBe(cursor.AddHours(6),
            "the one row stands for the most recent slot that was owed");

        var execution = _recorder.Snapshot().ShouldHaveSingleItem();
        execution.Misfire.ShouldNotBeNull();
        execution.Misfire!.Kind.ShouldBe(MisfireKind.FireOnce);
        execution.Misfire.MissedFromUtc.ShouldBe(cursor);
        execution.Misfire.MissedThroughUtc.ShouldBe(cursor.AddHours(6));
        execution.Misfire.MissedCount.ShouldBe(7, "the handler is told how many slots this delivery stands for");
    }

    [Fact]
    public async Task A_durable_skip_after_a_downtime_behaves_exactly_as_it_does_today()
    {
        await CreateSqliteHostAsync(startHost: false);

        // Durable rows, legacy semantics: a slot that is no longer the current one is dropped and the cursor
        // moves to the next future occurrence. Nothing is replayed and nothing is executed.
        var cursor = DateTimeOffset.UtcNow.AddHours(-6);
        var scheduleId = await SeedScheduleAsync(
            Hourly(new MisfireSettings { Policy = MisfirePolicy.Skip }), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => Storage.Get(t => t.Id == scheduleId),
            rows => rows[0].NextRunUtc != cursor, 30000);

        var schedule = (await Storage.Get(t => t.Id == scheduleId))[0];

        schedule.NextRunUtc.ShouldBe(cursor.AddHours(7),
            "Skip jumps the cursor to the first slot still in the future, exactly as the inline path does");
        schedule.CurrentRunCount.ShouldBe(0, "and a skipped slot spends no run");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("a dropped slot never becomes a row");
        _recorder.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Many_schedules_replaying_at_once_all_drain_under_one_global_budget()
    {
        // The startup shape the global budget exists for: several durable schedules all owing a backlog the
        // moment the host comes back. The budget bounds how many of them are inside a materialization run at
        // a time; what must NOT happen is that bounding it starves any of them.
        await CreateSqliteHostAsync(startHost: false,
            configure: cfg => cfg.SetMaterializationConcurrency(2).SetChannelOptions(100));

        var cursor    = DateTimeOffset.UtcNow.AddHours(-3);
        var schedules = new List<Guid>();

        for (var i = 0; i < 6; i++)
        {
            schedules.Add(await SeedScheduleAsync(
                Hourly(CatchUp(TimeSpan.FromDays(1), 50, maxPending: 10)), cursor));
        }

        await Host!.StartAsync();

        foreach (var scheduleId in schedules)
        {
            await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId),
                rows => rows.Length >= 4, 60000);

            (await OccurrencesOfAsync(scheduleId))
                .Select(o => o.ScheduledExecutionUtc!.Value)
                .OrderBy(s => s)
                .Take(4)
                .ShouldBe([cursor, cursor.AddHours(1), cursor.AddHours(2), cursor.AddHours(3)],
                    "a bounded budget delays a schedule, it never drops one");
        }
    }

    [Fact]
    public async Task Pruning_the_occurrences_of_a_replay_does_not_make_the_schedule_recreate_them()
    {
        // Retention deletes ROWS; what drives materialization is the CURSOR. If the two were confused — if a
        // run decided what to create by looking at which occurrences exist — pruning a replay would replay it
        // again, for ever, one retention cycle at a time.
        await CreateSqliteHostAsync(startHost: false);

        var cursor = DateTimeOffset.UtcNow.AddHours(-3);
        var scheduleId = await SeedScheduleAsync(
            Hourly(CatchUp(TimeSpan.FromDays(1), 50, maxPending: 10)), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId), rows => rows.Length >= 4, 30000);
        await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId),
            rows => rows.Length >= 4 && rows.All(o => o.Status == QueuedTaskStatus.Completed), 30000);

        var replayed = (await OccurrencesOfAsync(scheduleId))
                       .Select(o => o.ScheduledExecutionUtc!.Value)
                       .OrderBy(s => s)
                       .ToArray();

        var cursorAfterReplay = (await Storage.Get(t => t.Id == scheduleId))[0].NextRunUtc;
        cursorAfterReplay.ShouldNotBeNull();

        var deleted = await ((EfCoreTaskStorage)Storage)
            .CleanupTerminalOccurrences(DateTimeOffset.UtcNow.AddMinutes(1), preserveTasksWithLogs: false);

        deleted.ShouldBe(replayed.Length, "the premise: every replayed occurrence really was pruned");
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();

        var schedule = (await Storage.Get(t => t.Id == scheduleId))[0];
        schedule.NextRunUtc.ShouldBe(cursorAfterReplay, "the schedule row is untouched by the pruning");
        schedule.Status.ShouldNotBe(QueuedTaskStatus.Cancelled);

        // The run that would recreate them, if anything other than the cursor decided what is owed.
        await Host.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        (await OccurrencesOfAsync(scheduleId))
            .Select(o => o.ScheduledExecutionUtc!.Value)
            .ShouldNotContain(slot => replayed.Contains(slot),
                "a pruned slot is a slot the cursor has already passed: it must never come back");
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_a_replay_creates_no_duplicate_slot()
    {
        await CreateSqliteHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scheduleId = await SeedScheduleAsync(Every(1, CatchUp(TimeSpan.FromHours(1), 50)), cursor);

        await Host!.StartAsync();
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 30000);

        var beforeRestart = (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc).ToList();
        beforeRestart.ShouldNotBeEmpty();

        // Same database, new process: the second host recovers the schedule and continues the replay.
        await CreateSqliteHostAsync(startHost: true);
        await Task.Delay(3000);

        var after = (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc).ToList();

        after.Count.ShouldBeGreaterThanOrEqualTo(beforeRestart.Count);
        after.Distinct().Count().ShouldBe(after.Count,
            "a restart mid-replay must not materialize a slot the first host had already created");
    }

    [Fact]
    public async Task A_series_whose_end_elapsed_during_the_downtime_replays_its_slots_and_then_finalizes()
    {
        await CreateSqliteHostAsync(startHost: false);

        // X3's grouped temporal term, seen from the durable side: the cursor is before RunUntil, RunUntil is
        // behind us, so the slots between the two are owed and the series ends right after them.
        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-10);
        var definition = Every(1, CatchUp(TimeSpan.FromHours(1), 50, maxPending: 10));

        // Anchored on the cursor, not on "now": the bound has to land exactly ON a slot, because what is
        // being pinned is that the slot falling on it is excluded and the three before it are not.
        definition.RunUntil = cursor.AddMinutes(3);

        var scheduleId = await SeedScheduleAsync(definition, cursor);

        // The row carries the bound too: the recovery filter reads columns, not the serialized definition.
        var row = (await Storage.Get(t => t.Id == scheduleId))[0];
        row.RunUntil = definition.RunUntil;
        await Storage.UpdateTask(row);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => Storage.Get(t => t.Id == scheduleId),
            rows => rows[0].NextRunUtc == null, 30000);

        var schedule = (await Storage.Get(t => t.Id == scheduleId))[0];
        schedule.Status.ShouldBe(QueuedTaskStatus.Completed);
        schedule.NextRunUtc.ShouldBeNull("a series whose grid is spent ends with its cursor cleared");

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .ToList();

        slots.ShouldBe([cursor, cursor.AddMinutes(1), cursor.AddMinutes(2)],
            "every slot owed before the boundary is still owed after a downtime, and the one that falls ON " +
            "RunUntil is not a slot at all");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(_dbFile))
                File.Delete(_dbFile);
        }
        catch (IOException)
        {
            // A file the engine has not released yet is left for the next clean build: the test is over.
        }
    }
}
