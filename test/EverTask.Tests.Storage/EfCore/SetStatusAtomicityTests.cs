using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// F20 — in the EF Core base <see cref="EfCoreTaskStorage.SetStatus"/> the StatusAudit insert and the
/// row UPDATE were two independent operations (audit committed first, then ExecuteUpdate), so a failure
/// in between left the audit and the row divergent — unlike the transactional SQL Server stored proc.
/// They must be atomic: either both land or neither.
///
/// Focused fault-injection test on a real relational provider (SQLite): the status update is injected
/// to fail. The cross-provider base already pins the positive behaviour on every provider.
/// </summary>
public class SetStatusAtomicityTests
{
    [Fact]
    public async Task Should_write_setstatus_audit_and_row_atomically()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AtomicityContext>().UseSqlite(connection).Options;
            await using (var ctx = new AtomicityContext(options))
                await ctx.Database.EnsureCreatedAsync();

            var factory = new Factory(options);
            var storage = new FaultingStatusUpdateStorage(factory, Mock.Of<IEverTaskLogger<EfCoreTaskStorage>>());

            var id = Guid.NewGuid();
            await storage.Persist(new QueuedTask
            {
                Id           = id,
                Type         = "T",
                Request      = "{}",
                Handler      = "H",
                Status       = QueuedTaskStatus.InProgress,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            // The status update is injected to fail. Whether SetStatus swallows or propagates, the
            // persisted state must respect the both-or-nothing invariant.
            try
            {
                await storage.SetStatus(id, QueuedTaskStatus.Failed,
                    new InvalidOperationException("boom"), AuditLevel.Full);
            }
            catch
            {
                // a propagated failure is acceptable — the invariant below is what we assert
            }

            var row = (await storage.Get(t => t.Id == id))[0];

            await using var probe = new AtomicityContext(options);
            var auditCount = probe.StatusAudit.Count(a => a.QueuedTaskId == id && a.NewStatus == QueuedTaskStatus.Failed);

            // Atomicity: the row is updated to Failed IFF its audit exists.
            (row.Status == QueuedTaskStatus.Failed).ShouldBe(auditCount > 0,
                "SetStatus must write the status audit and the row update atomically");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task Should_write_setstatus_audit_and_row_on_non_relational_provider()
    {
        // Covers the non-relational branch (EF Core InMemory): the audit + row update are applied as a
        // single tracked SaveChanges, since ExecuteUpdate/transactions are unavailable there.
        var options = new DbContextOptionsBuilder<AtomicityContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;

        var storage = new EfCoreTaskStorage(new Factory(options), Mock.Of<IEverTaskLogger<EfCoreTaskStorage>>());

        var id = Guid.NewGuid();
        await storage.Persist(new QueuedTask
        {
            Id           = id,
            Type         = "T",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.InProgress,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await storage.SetStatus(id, QueuedTaskStatus.Failed, new InvalidOperationException("boom"), AuditLevel.Full);

        var row = (await storage.Get(t => t.Id == id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Failed);

        await using var probe = new AtomicityContext(options);
        probe.StatusAudit.Count(a => a.QueuedTaskId == id && a.NewStatus == QueuedTaskStatus.Failed).ShouldBe(1);
    }

    private sealed class Factory(DbContextOptions<AtomicityContext> options) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITaskStoreDbContext>(new AtomicityContext(options));
    }

    private sealed class FaultingStatusUpdateStorage(
        ITaskStoreDbContextFactory factory, IEverTaskLogger<EfCoreTaskStorage> logger)
        : EfCoreTaskStorage(factory, logger)
    {
        protected override Task<int> ExecuteStatusUpdateAsync(
            ITaskStoreDbContext dbContext, Guid taskId, QueuedTaskStatus status, string? exception,
            double? executionTimeMs, DateTimeOffset? lastExecutionUtc, CancellationToken ct)
            => throw new InvalidOperationException("Injected status update failure");
    }

    private sealed class AtomicityContext(DbContextOptions<AtomicityContext> options)
        : DbContext(options), ITaskStoreDbContext
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
}
