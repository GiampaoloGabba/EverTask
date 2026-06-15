using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// Residual D — the EF Core / SQL Server run-counter writes (<see cref="EfCoreTaskStorage.UpdateCurrentRun"/>,
/// <see cref="EfCoreTaskStorage.CompleteRecurringRun"/>, <see cref="EfCoreTaskStorage.SetRecurringSeriesCompleted"/>)
/// used to catch the persistence exception, LogCritical, and SWALLOW it. WorkerExecutor then advanced the
/// in-memory schedule on UNPERSISTED state — CurrentRunCount diverged from real executions and, on restart,
/// the stale row re-ran the occurrence. A failed accounting persist must PROPAGATE so QueueNextOccourrence
/// does NOT reach scheduler.Schedule (no advance on unpersisted state) and the recoverable row is re-run.
/// The consumer (WorkerService.ConsumeAsync) catches the propagated exception defensively and continues.
///
/// <para>Fault-injection on a real relational provider (SQLite): the row UPDATE / SaveChanges is injected
/// to fail; the call must throw rather than complete silently.</para>
/// </summary>
public class RunCounterPersistFailurePropagationTests
{
    [Fact]
    public async Task UpdateCurrentRun_propagates_persistence_failure_instead_of_swallowing()
        => await AssertPropagates((storage, id) =>
            storage.UpdateCurrentRun(id, 100.0, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full));

    [Fact]
    public async Task CompleteRecurringRun_propagates_persistence_failure_instead_of_swallowing()
        => await AssertPropagates((storage, id) =>
            storage.CompleteRecurringRun(id, 100.0, DateTimeOffset.UtcNow.AddMinutes(1), AuditLevel.Full));

    [Fact]
    public async Task SetRecurringSeriesCompleted_propagates_persistence_failure_instead_of_swallowing()
        => await AssertPropagates((storage, id) =>
            storage.SetRecurringSeriesCompleted(id, 100.0, AuditLevel.Full));

    private static async Task AssertPropagates(Func<ITaskStorage, Guid, Task> act)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<Ctx>().UseSqlite(connection).Options;
            await using (var ctx = new Ctx(options))
                await ctx.Database.EnsureCreatedAsync();

            var id = Guid.NewGuid();
            await using (var seed = new Ctx(options))
            {
                seed.QueuedTasks.Add(new QueuedTask
                {
                    Id              = id,
                    Type            = "T", Request = "{}", Handler = "H",
                    Status          = QueuedTaskStatus.InProgress,
                    IsRecurring     = true,
                    CurrentRunCount = 1,
                    NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(5),
                    CreatedAtUtc    = DateTimeOffset.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            // The storage uses contexts whose SaveChangesAsync is injected to fail (the row load still works).
            var storage = new EfCoreTaskStorage(new ThrowingFactory(options),
                Mock.Of<IEverTaskLogger<EfCoreTaskStorage>>());

            await Should.ThrowAsync<Exception>(
                () => act(storage, id),
                "a failed counter/completion persist must PROPAGATE, not be swallowed (residual D)");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private sealed class ThrowingFactory(DbContextOptions<Ctx> options) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITaskStoreDbContext>(new ThrowOnSaveCtx(options));
    }

    private class Ctx(DbContextOptions<Ctx> options) : DbContext(options), ITaskStoreDbContext
    {
        public string? Schema => null;

        public DbSet<QueuedTask>       QueuedTasks       => Set<QueuedTask>();
        public DbSet<StatusAudit>      StatusAudit       => Set<StatusAudit>();
        public DbSet<RunsAudit>        RunsAudit         => Set<RunsAudit>();
        public DbSet<TaskExecutionLog> TaskExecutionLogs => Set<TaskExecutionLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<QueuedTask>().HasKey(t => t.Id);
            modelBuilder.Entity<StatusAudit>().HasKey(a => a.Id);
            modelBuilder.Entity<RunsAudit>().HasKey(a => a.Id);
            modelBuilder.Entity<TaskExecutionLog>().HasKey(l => l.Id);
        }
    }

    private sealed class ThrowOnSaveCtx(DbContextOptions<Ctx> options) : Ctx(options)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
                                                   CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected persistence failure");
    }
}
