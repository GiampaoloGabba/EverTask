using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// X2, "capability and implementation are inseparable", on the one EF Core configuration this base class
/// still supports without being relational: the in-memory provider, which it serves with the client-side
/// fallbacks in <c>TrySetQueuedIfRecoverable</c> and <c>SetStatus</c>.
/// </summary>
/// <remarks>
/// Every durable-occurrence operation opens with <c>RequireRelational</c>, so on this provider the two
/// capabilities can only answer false. Answering true would let a caller's <c>SupportsDurableOccurrences</c>
/// guard pass at dispatch and turn a clean refusal into a <see cref="NotSupportedException"/> much later, at
/// materialization — with a schedule already persisted as durable. No database and no container: what is
/// under test is the answer, and whether the operations agree with it.
/// </remarks>
public class EfCoreNonRelationalCapabilityTests
{
    private static EfCoreTaskStorage CreateInMemoryStorage() =>
        new(new InMemoryContextFactory(), new Mock<IEverTaskLogger<EfCoreTaskStorage>>().Object);

    [Fact]
    public void A_non_relational_provider_advertises_neither_capability()
    {
        var storage = CreateInMemoryStorage();

        storage.SupportsDurableOccurrences.ShouldBeFalse(
            "the operations behind this flag all require a relational provider");
        storage.SupportsScheduleVersioning.ShouldBeFalse();

        // Asked twice: the answer is resolved once and cached, and it must not change between reads.
        storage.SupportsDurableOccurrences.ShouldBeFalse();
        storage.SupportsScheduleVersioning.ShouldBeFalse();
    }

    [Fact]
    public async Task The_operations_a_non_relational_provider_refuses_are_exactly_the_ones_it_does_not_advertise()
    {
        ITaskStorage storage = CreateInMemoryStorage();

        var slot = DateTimeOffset.UtcNow;
        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            Type                  = "T",
            Request               = "{}",
            Handler               = "H",
            ScheduledExecutionUtc = slot
        };

        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.MaterializeOccurrence(Guid.NewGuid(), 0, slot, occurrence, slot.AddMinutes(5),
                AuditLevel.Full));

        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.TrySetRecurringSeriesCompleted(Guid.NewGuid(), slot, QueuedTaskStatus.Queued, 0, 0,
                AuditLevel.Full));

        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.CancelSchedule(Guid.NewGuid(), AuditLevel.Full));

        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.UpdateSchedule(Guid.NewGuid(), 0, slot, "{}", null, slot, null, null, null));
    }

    [Fact]
    public async Task A_non_relational_provider_still_serves_the_recovery_transition_it_does_advertise()
    {
        // The control: refusing the compare-and-swap family is not the same as being unsupported. The
        // client-side fallbacks are why this configuration exists, and they must keep working.
        ITaskStorage storage = CreateInMemoryStorage();

        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = "T",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await storage.Persist(row);

        (await storage.RetrievePending(DateTimeOffset.UtcNow, null, null, 10))
            .ShouldContain(t => t.Id == row.Id);
        (await storage.TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, row.Id, AuditLevel.Full)).ShouldBeTrue();
        (await storage.Get(t => t.Id == row.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    private sealed class InMemoryContextFactory : ITaskStoreDbContextFactory
    {
        private readonly DbContextOptions<ProbeContext> _options =
            new DbContextOptionsBuilder<ProbeContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ITaskStoreDbContext>(new ProbeContext(_options));
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options), ITaskStoreDbContext
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
