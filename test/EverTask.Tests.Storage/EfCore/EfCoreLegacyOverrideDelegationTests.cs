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
/// P9, from the point of view of an out-of-tree provider: it derives from <see cref="EfCoreTaskStorage"/> and
/// overrides only the pre-4.0 signatures — the four-argument <c>RetrievePending</c> and the three-argument
/// <c>TrySetQueuedIfRecoverable</c> — because that is all the shipped scaffolding knew about. The core now
/// calls the clock-carrying overloads exclusively, and a base-class virtual resolves statically to the base
/// body, so without an explicit hand-back that override becomes dead code and the base answers with its own
/// server-side query: a page the provider deliberately filtered comes back, or the untranslatable comparison
/// throws.
/// </summary>
/// <remarks>
/// No database and no container: the fact under test is which implementation the call reaches. The context
/// factory throws on use, so a base query that slipped through fails loudly instead of quietly returning
/// something plausible.
/// </remarks>
public class EfCoreLegacyOverrideDelegationTests
{
    [Fact]
    public async Task RetrievePending_with_a_clock_must_reach_the_derived_legacy_override()
    {
        var probe = new LegacyOnlyStorageProbe();

        var page = await ((ITaskStorage)probe).RetrievePending(DateTimeOffset.UtcNow, null, null, 10);

        probe.LegacyRetrievePendingCalls.ShouldBe(1);
        page.ShouldHaveSingleItem().Type.ShouldBe("decided-by-the-derived-override");
    }

    [Fact]
    public async Task TrySetQueuedIfRecoverable_with_a_clock_must_reach_the_derived_legacy_override()
    {
        var probe = new LegacyOnlyStorageProbe();

        var requeued = await ((ITaskStorage)probe)
            .TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, Guid.NewGuid(), AuditLevel.Full);

        probe.LegacyTrySetQueuedCalls.ShouldBe(1);
        requeued.ShouldBeFalse("the derived override refused, and its answer is the one that counts");
    }

    [Fact]
    public async Task A_legacy_override_that_calls_base_must_not_bounce_between_the_two_overloads()
    {
        // The hand-back only holds while the base's own legacy signature goes straight to the implementation.
        // If it delegated back to the nowUtc overload, this shape — the natural one for a provider that only
        // wraps the base — would recurse until the stack ran out.
        var probe = new LegacyOverrideCallingBaseProbe();

        var page = await ((ITaskStorage)probe).RetrievePending(DateTimeOffset.UtcNow, null, null, 10);

        probe.LegacyRetrievePendingCalls.ShouldBe(1);
        page.ShouldBeEmpty("the base query ran once, against an empty store");
    }

    /// <summary>An out-of-tree provider that answers entirely on its own, as SQLite's overrides do.</summary>
    private sealed class LegacyOnlyStorageProbe()
        : EfCoreTaskStorage(new UnreachableContextFactory(),
            new Mock<IEverTaskLogger<EfCoreTaskStorage>>().Object)
    {
        public int LegacyRetrievePendingCalls { get; private set; }
        public int LegacyTrySetQueuedCalls    { get; private set; }

        public override Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                           CancellationToken ct = default)
        {
            LegacyRetrievePendingCalls++;
            return Task.FromResult<QueuedTask[]>([
                new QueuedTask { Type = "decided-by-the-derived-override", Request = "{}", Handler = "H" }
            ]);
        }

        public override Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel,
                                                             CancellationToken ct = default)
        {
            LegacyTrySetQueuedCalls++;
            return Task.FromResult(false);
        }
    }

    /// <summary>An out-of-tree provider that only decorates the base's legacy signature.</summary>
    private sealed class LegacyOverrideCallingBaseProbe()
        : EfCoreTaskStorage(new InMemoryContextFactory(new DbContextOptionsBuilder<ProbeContext>()
                                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            new Mock<IEverTaskLogger<EfCoreTaskStorage>>().Object)
    {
        public int LegacyRetrievePendingCalls { get; private set; }

        public override Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                           CancellationToken ct = default)
        {
            LegacyRetrievePendingCalls++;
            return base.RetrievePending(lastCreatedAt, lastId, take, ct);
        }
    }

    /// <summary>
    /// Must never be reached: the probe above answers without touching a database, so a call here means the
    /// base ran its own query and the derived override was bypassed.
    /// </summary>
    private sealed class UnreachableContextFactory : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The base server-side query ran: the derived legacy override was bypassed.");
    }

    private sealed class InMemoryContextFactory(DbContextOptions<ProbeContext> options) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ITaskStoreDbContext>(new ProbeContext(options));
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
