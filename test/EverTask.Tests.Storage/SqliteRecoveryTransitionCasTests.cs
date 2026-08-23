using System.Data.Common;
using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// The SQLite recovery transition has to be a real check-and-set. SQLite cannot translate the temporal half
/// of the recoverable predicate, so <see cref="SqliteTaskStorage.TrySetQueuedIfRecoverable(DateTimeOffset,
/// Guid, AuditLevel, CancellationToken)"/> reads the row first — but the write that follows must still be
/// conditional, or a transition that linearizes in between is silently overwritten with <c>Queued</c> and the
/// task is executed anyway.
/// </summary>
/// <remarks>
/// The interleaving is deterministic, not a stress loop: a real EF interceptor runs a real concurrent write
/// (a second <see cref="SqliteTaskStorage"/> on its own connection) in the one instant that window is open.
/// Everything below the tests is production code against a real SQLite file.
/// </remarks>
[Collection("DatabaseTests")]
public sealed class SqliteRecoveryTransitionCasTests : IDisposable
{
    private readonly string _dbFile = $"SqliteCas_{Guid.NewGuid():N}.db";
    private readonly string _connectionString;

    private readonly RaceInterceptor                          _interceptor = new();
    private readonly DbContextOptions<SqliteTaskStoreContext> _plainOptions;
    private readonly SqliteTaskStorage                        _storage;
    private readonly SqliteTaskStorage                        _concurrentWriter;

    public SqliteRecoveryTransitionCasTests()
    {
        _connectionString = $"Data Source={_dbFile}";

        var plain = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        plain.UseSqlite(_connectionString).UseEverTaskSchema("");
        _plainOptions = plain.Options;

        var intercepted = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        intercepted.UseSqlite(_connectionString).UseEverTaskSchema("").AddInterceptors(_interceptor);

        using (var migrator = new SqliteTaskStoreContext(_plainOptions))
            migrator.Database.Migrate();

        _storage = new SqliteTaskStorage(new ContextFactory(intercepted.Options),
            Mock.Of<IEverTaskLogger<SqliteTaskStorage>>());

        // The racing writer runs on the UNintercepted options: its own transaction must not trip the hook.
        _concurrentWriter = new SqliteTaskStorage(new ContextFactory(_plainOptions),
            Mock.Of<IEverTaskLogger<SqliteTaskStorage>>());
    }

    [Fact]
    public async Task Should_refuse_the_recovery_transition_when_a_cancel_lands_between_the_read_and_the_write()
    {
        var id = await SeedRecoverableSeriesAsync();

        _interceptor.Arm(() => _concurrentWriter.SetCancelledByUser(id, AuditLevel.Full));

        var transitioned = await _storage.TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, id, AuditLevel.Full);

        _interceptor.Fired.ShouldBe(1,
            "the cancel has to interleave BETWEEN the read and the write, or this test proves nothing");
        transitioned.ShouldBeFalse("the row was cancelled after it was read: the check-and-set must lose");

        var row = (await _storage.Get(t => t.Id == id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "recovery overwrote a cancellation, so the cancelled task would run again at the next restart");

        await using var probe = new SqliteTaskStoreContext(_plainOptions);
        probe.StatusAudit.Count(a => a.QueuedTaskId == id && a.NewStatus == QueuedTaskStatus.Queued)
             .ShouldBe(0, "a refused transition must leave no audit trace");
    }

    [Fact]
    public async Task Should_transition_and_audit_a_recoverable_series_when_nothing_races_it()
    {
        // The control: the compare-and-swap re-asserts NextRunUtc and RunUntil BY VALUE, so a mismatch
        // between what SQLite stores and what it parameterizes would refuse every recurring recovery.
        var id = await SeedRecoverableSeriesAsync();

        var transitioned = await _storage.TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, id, AuditLevel.Full);

        _interceptor.Fired.ShouldBe(0);
        transitioned.ShouldBeTrue();

        (await _storage.Get(t => t.Id == id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        await using var probe = new SqliteTaskStoreContext(_plainOptions);
        probe.StatusAudit.Count(a => a.QueuedTaskId == id && a.NewStatus == QueuedTaskStatus.Queued)
             .ShouldBe(1, "the transition and its audit commit together");
    }

    /// <summary>
    /// A recurring row between two runs, with both temporal columns populated: the shape whose recoverability
    /// only the in-memory half of the predicate can decide on SQLite.
    /// </summary>
    private async Task<Guid> SeedRecoverableSeriesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var id  = Guid.NewGuid();

        await _storage.Persist(new QueuedTask
        {
            Id              = id,
            CreatedAtUtc    = now.AddMinutes(-10),
            Type            = "CasProbe",
            Request         = "{}",
            Handler         = "H",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = now.AddMinutes(-1),
            RunUntil        = now.AddHours(1),
            CurrentRunCount = 2
        });

        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { /* best-effort cleanup */ }
    }

    private sealed class ContextFactory(DbContextOptions<SqliteTaskStoreContext> options) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ITaskStoreDbContext>(new SqliteTaskStoreContext(options));

        public ITaskStoreDbContext CreateDbContext() => new SqliteTaskStoreContext(options);
    }

    /// <summary>
    /// Runs the armed action once, immediately before the storage opens the transaction that carries its
    /// write — the only instant a concurrent writer can still get in. The read is already done and its lock
    /// released; Microsoft.Data.Sqlite issues <c>BEGIN IMMEDIATE</c>, so one statement later the racing
    /// connection would be locked out and would simply apply after the commit.
    /// </summary>
    /// <remarks>
    /// The hook is the transaction, not the UPDATE, precisely because it is the same instant for both
    /// shapes of the transition: the tracked read-then-SaveChanges this replaced (EF wraps its two-command
    /// batch in a transaction) and the conditional ExecuteUpdate that stands here now.
    /// </remarks>
    private sealed class RaceInterceptor : DbTransactionInterceptor
    {
        private Func<Task>? _armed;
        private int         _fired;

        public int Fired => _fired;

        public void Arm(Func<Task> race)
        {
            _armed = race;
            _fired = 0;
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (_armed is not { } race)
                return result;

            _armed = null;
            Interlocked.Increment(ref _fired);
            await race().ConfigureAwait(false);

            return result;
        }
    }
}
