using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// M1 / L20 — the recovery transition (Status -> Queued) and its audit row must be written
/// ATOMICALLY: either both land or neither. Today the client-side transition saves the status,
/// then writes the audit in a SEPARATE SaveChanges whose failure is swallowed — leaving a row
/// transitioned-to-Queued with no audit (and, on a crash, a resurrected execution with no trace).
///
/// This is a focused fault-injection test (an audit write that throws): the shared-context
/// cross-provider base (<see cref="EfCoreTaskStorageTestsBase"/>) cannot inject that failure, while
/// its positive cases already pin the transition/audit behaviour on every provider.
/// </summary>
public class RecoveryTransitionAtomicityTests
{
    [Fact]
    public async Task Should_write_recovery_transition_and_audit_atomically()
    {
        var options = new DbContextOptionsBuilder<FaultingTaskStoreContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;

        var factory = new FaultingContextFactory(options);
        var storage = new EfCoreTaskStorage(factory, Mock.Of<IEverTaskLogger<EfCoreTaskStorage>>());

        var id = Guid.NewGuid();
        await storage.Persist(new QueuedTask
        {
            Id           = id,
            Type         = "T",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.WaitingQueue, // recoverable
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // The recovery transition writes Status=Queued AND a Queued StatusAudit. The audit write is
        // injected to fail. Whether the call swallows or propagates the failure, the persisted state
        // must respect the both-or-nothing invariant.
        try
        {
            await storage.TrySetQueuedIfRecoverable(id, AuditLevel.Full);
        }
        catch
        {
            // A propagated failure is acceptable — the invariant below is what we assert.
        }

        var row = (await storage.Get(t => t.Id == id))[0];

        await using var probe = new FaultingTaskStoreContext(options);
        var auditCount = probe.StatusAudit
            .Count(a => a.QueuedTaskId == id && a.NewStatus == QueuedTaskStatus.Queued);

        // Atomicity: the row must NEVER be left transitioned-to-Queued without its audit.
        if (row.Status == QueuedTaskStatus.Queued)
            auditCount.ShouldBeGreaterThan(0,
                "the transition was committed but its audit was lost — transition + audit are not atomic");
    }

    private sealed class FaultingContextFactory(DbContextOptions<FaultingTaskStoreContext> options) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITaskStoreDbContext>(new FaultingTaskStoreContext(options));
    }

    /// <summary>
    /// EF Core InMemory context that fails the audit write of a recovery transition: SaveChanges
    /// throws whenever a Queued <see cref="StatusAudit"/> is pending insertion. The InMemory provider
    /// has no ExecuteUpdate, so <see cref="EfCoreTaskStorage.TrySetQueuedIfRecoverable"/> runs its
    /// client-side path — exactly the path whose atomicity this test pins.
    /// </summary>
    private sealed class FaultingTaskStoreContext(DbContextOptions<FaultingTaskStoreContext> options)
        : DbContext(options), ITaskStoreDbContext
    {
        public string? Schema => null;

        public DbSet<QueuedTask>       QueuedTasks       => Set<QueuedTask>();
        public DbSet<StatusAudit>      StatusAudit       => Set<StatusAudit>();
        public DbSet<RunsAudit>        RunsAudit         => Set<RunsAudit>();
        public DbSet<TaskExecutionLog> TaskExecutionLogs => Set<TaskExecutionLog>();

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var auditPending = ChangeTracker.Entries<StatusAudit>()
                .Any(e => e.State == EntityState.Added && e.Entity.NewStatus == QueuedTaskStatus.Queued);

            if (auditPending)
                throw new InvalidOperationException("Injected audit write failure");

            return base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<QueuedTask>().HasKey(t => t.Id);
            modelBuilder.Entity<StatusAudit>().HasKey(a => a.Id);
            modelBuilder.Entity<RunsAudit>().HasKey(a => a.Id);
            modelBuilder.Entity<TaskExecutionLog>().HasKey(l => l.Id);
        }
    }
}
