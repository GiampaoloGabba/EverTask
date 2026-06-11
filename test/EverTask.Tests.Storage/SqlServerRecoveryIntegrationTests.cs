using EverTask.Abstractions;
using EverTask.Storage;
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
/// End-to-end recovery resilience tests against a REAL SQL Server (Testcontainers).
/// Unlike the memory-backed QueueResilienceIntegrationTests, these exercise the concurrent
/// recovery flow (WorkerService + consumers + dispatcher + scheduler) against actual storage
/// transactions, covering the gap that the in-memory tests cannot: real DB concurrency,
/// keyset pagination over a real table, and the SetQueued/SetStatus interplay under load.
/// </summary>
[Collection("DatabaseTests")]
public class SqlServerRecoveryIntegrationTests : IsolatedIntegrationTestBase, IAsyncLifetime
{
    private static MsSqlContainer? _sqlContainer;
    private static bool _containerInitialized;
    private static readonly object _lock = new();
    private static string _connectionString = "";
    private Respawner? _respawner;
    private readonly ResilienceTestState _state = new();

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

    private Task<IHost> CreateSqlServerHostAsync(
        bool startHost = true,
        Action<EverTaskServiceConfiguration>? configureEverTask = null,
        Action<EverTaskServiceBuilder>? configureBuilder = null) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddSqlServerStorage(_connectionString, opt => opt.AutoApplyMigrations = true);
                builder.Services.AddSingleton(_state);
                configureBuilder?.Invoke(builder);
            },
            startHost: startHost,
            configureEverTask: configureEverTask);

    private static QueuedTask SeededTask(IEverTask task, QueuedTaskStatus status, DateTimeOffset createdAt) =>
        new()
        {
            Id           = Guid.NewGuid(),
            Type         = task.GetType().AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(task),
            Handler      = "seeded-by-test",
            Status       = status,
            CreatedAtUtc = createdAt
        };

    [Fact]
    public async Task Should_recover_backlog_exceeding_capacity_without_deadlock_or_loss()
    {
        const int backlog = 60;

        // Tiny channel (capacity 5): before the fix, recovery's blocking writes filled the
        // channel before consumers started and wedged startup forever. Against a real DB this
        // also exercises keyset pagination + concurrent SetQueued/SetInProgress/SetCompleted.
        await CreateSqlServerHostAsync(
            startHost: false,
            configureEverTask: cfg => cfg.SetChannelOptions(5).SetMaxDegreeOfParallelism(4));

        var seededAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < backlog; i++)
            await Storage.Persist(SeededTask(new ResilienceCounterTask(i), QueuedTaskStatus.Queued, seededAt.AddMilliseconds(i)));

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ExecutedIndexes.Count >= backlog, timeoutMs: 60000);

        // Exactly-once, every index, persisted as Completed in the real DB.
        var executions = _state.ExecutedIndexes.GroupBy(i => i).ToList();
        executions.Count.ShouldBe(backlog);
        executions.ShouldAllBe(g => g.Count() == 1);

        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.Length == backlog && tasks.All(t => t.Status == QueuedTaskStatus.Completed),
            timeoutMs: 20000);
    }

    [Fact]
    public async Task Should_recover_delayed_WaitingQueue_task_after_restart()
    {
        // Host 1: a delayed one-shot parked in the scheduler is persisted as WaitingQueue.
        await CreateSqlServerHostAsync();

        var taskId = await Dispatcher.Dispatch(new ResilienceCounterTask(7), TimeSpan.FromSeconds(10));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 8000);

        // Host 2 points at the SAME database (real restart). Before the fix WaitingQueue was
        // excluded from RetrievePending and the task was lost.
        await CreateSqlServerHostAsync();

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 25000);
        _state.ExecutedIndexes.Count(i => i == 7).ShouldBe(1);
    }

    [Fact]
    public async Task Should_revive_recurring_task_after_restart_preserving_stored_NextRunUtc()
    {
        // Host 1: dynamically created recurring task, never re-registered at boot.
        await CreateSqlServerHostAsync();

        var taskId = await Dispatcher.Dispatch(new ResilienceRecurringTask(),
            r => r.RunDelayed(TimeSpan.FromMilliseconds(500)).Then().UseCron("*/10 * * * * *"));

        var anchorTask = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).FirstOrDefault(t => t.Id == taskId),
            t => t is { NextRunUtc: not null, CurrentRunCount: > 0 }
                 && t.NextRunUtc!.Value - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(6),
            timeoutMs: 40000);

        var anchor       = anchorTask!.NextRunUtc!.Value;
        var runsAtAnchor = anchorTask.CurrentRunCount!.Value;

        // Host 2 (real restart, same DB): the recurring task must be revived and keep firing.
        await CreateSqlServerHostAsync();

        // Lost-update guard: revival must not rewrite the stored NextRunUtc.
        await Task.Delay(2000);
        var afterRevival = (await Storage.GetAll()).First(t => t.Id == taskId);
        afterRevival.NextRunUtc.ShouldBe(anchor);

        // Occurrence-skip guard (P0): the next run advances exactly once past the anchor.
        var afterRun = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).First(t => t.Id == taskId),
            t => (t.CurrentRunCount ?? 0) >= runsAtAnchor + 1,
            timeoutMs: 30000);

        afterRun.LastExecutionUtc.ShouldNotBeNull();
        afterRun.LastExecutionUtc!.Value.ShouldBeLessThan(anchor.AddSeconds(8));
    }
}
