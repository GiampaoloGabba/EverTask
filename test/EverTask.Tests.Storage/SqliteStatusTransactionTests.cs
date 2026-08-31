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

[Collection("DatabaseTests")]
public sealed class SqliteStatusTransactionTests : IDisposable
{
    private readonly string _dbFile = $"SqliteStatusTransactions_{Guid.NewGuid():N}.db";

    private readonly TransactionCountingInterceptor              _interceptor = new();
    private readonly DbContextOptions<SqliteTaskStoreContext>     _plainOptions;
    private readonly SqliteTaskStorage                            _storage;

    public SqliteStatusTransactionTests()
    {
        var connectionString = $"Data Source={_dbFile}";

        var plain = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        plain.UseSqlite(connectionString).UseEverTaskSchema("");
        _plainOptions = plain.Options;

        var intercepted = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        intercepted.UseSqlite(connectionString).UseEverTaskSchema("").AddInterceptors(_interceptor);

        using (var migrator = new SqliteTaskStoreContext(_plainOptions))
            migrator.Database.Migrate();

        _storage = new SqliteTaskStorage(new ContextFactory(intercepted.Options),
            Mock.Of<IEverTaskLogger<SqliteTaskStorage>>());
    }

    [Fact]
    public async Task Should_open_a_transaction_only_when_SetStatus_writes_an_audit()
    {
        var nonAuditedId = await SeedAsync();
        var auditedId    = await SeedAsync();

        _interceptor.Reset();
        await _storage.SetStatus(nonAuditedId, QueuedTaskStatus.Completed, null, AuditLevel.None);

        _interceptor.Starts.ShouldBe(0);
        (await _storage.Get(t => t.Id == nonAuditedId))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        await using (var probe = new SqliteTaskStoreContext(_plainOptions))
            (await probe.StatusAudit.CountAsync(a => a.QueuedTaskId == nonAuditedId)).ShouldBe(0);

        _interceptor.Reset();
        await _storage.SetStatus(auditedId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        _interceptor.Starts.ShouldBe(1);
        (await _storage.Get(t => t.Id == auditedId))[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        await using (var probe = new SqliteTaskStoreContext(_plainOptions))
            (await probe.StatusAudit.CountAsync(a => a.QueuedTaskId == auditedId)).ShouldBe(1);
    }

    private async Task<Guid> SeedAsync()
    {
        var id = Guid.NewGuid();
        await _storage.Persist(new QueuedTask
        {
            Id           = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Type         = "StatusTransactionProbe",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.InProgress
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

    private sealed class TransactionCountingInterceptor : DbTransactionInterceptor
    {
        private int _starts;

        public int Starts => _starts;

        public void Reset() => Interlocked.Exchange(ref _starts, 0);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return ValueTask.FromResult(result);
        }
    }
}
