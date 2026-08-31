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
public sealed class SqliteOccurrenceIdProjectionTests : IDisposable
{
    private readonly string _dbFile = $"SqliteOccurrenceIds_{Guid.NewGuid():N}.db";

    private readonly CommandObservingInterceptor               _interceptor = new();
    private readonly DbContextOptions<SqliteTaskStoreContext>   _plainOptions;
    private readonly SqliteTaskStorage                          _storage;

    public SqliteOccurrenceIdProjectionTests()
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
    public async Task Should_project_only_occurrence_ids_without_reading_request_payloads()
    {
        var parentId = Guid.NewGuid();
        var now      = DateTimeOffset.UtcNow;
        await _storage.Persist(new QueuedTask
        {
            Id           = parentId,
            CreatedAtUtc = now.AddMinutes(-1),
            Type         = "ProjectionSchedule",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.Queued,
            IsRecurring  = true,
            NextRunUtc   = now
        });

        var childIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var childId = Guid.NewGuid();
            childIds.Add(childId);
            await _storage.Persist(new QueuedTask
            {
                Id                    = childId,
                CreatedAtUtc          = now,
                Type                  = "ProjectionOccurrence",
                Request               = new string('x', 20_000),
                Handler               = "H",
                Status                = QueuedTaskStatus.WaitingQueue,
                ParentTaskId          = parentId,
                ScheduledExecutionUtc = now.AddMinutes(i)
            });
        }

        _interceptor.Reset();
        var ids = await _storage.GetOccurrenceIds(parentId, nonTerminalOnly: true);

        ids.OrderBy(id => id).ShouldBe(childIds.OrderBy(id => id));
        var sql = _interceptor.CommandText.ShouldNotBeNull();
        sql.ShouldContain("\"Id\"");
        sql.ShouldNotContain("\"Request\"");
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

    private sealed class CommandObservingInterceptor : DbCommandInterceptor
    {
        public string? CommandText { get; private set; }

        public void Reset() => CommandText = null;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandText = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }
}
