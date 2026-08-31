using System.Reflection;
using BenchmarkDotNet.Attributes;
using EverTask.Abstractions;
using EverTask.Storage;
using Microsoft.EntityFrameworkCore;

namespace EverTask.Benchmarks;

/// <summary>
/// Issue #17 — is the TRACKED <c>Persist</c> insert worth replacing with a raw parameterized INSERT?
///
/// <c>EfCoreTaskStorage.Persist</c> is the one write of the ~4 per task that goes through the change
/// tracker: <c>QueuedTasks.Add(entity)</c> + <c>SaveChangesAsync()</c> materializes an <c>EntityEntry</c>
/// and a full property snapshot for a 24-column row, then builds a <c>ModificationCommandBatch</c>. The
/// other three writes are no-tracking (ExecuteUpdate on the base, procs on SQL Server, CTEs on Postgres).
///
/// Three ways to write the SAME row, per provider:
/// <list type="bullet">
/// <item><c>Tracked_Persist</c> — the production path, called through the real <see cref="ITaskStorage"/>.</item>
/// <item><c>Raw_Insert_EfCommand</c> — a parameterized INSERT through a pooled context's ExecuteSqlRaw
/// (issue #17 option 1: shared SQL in the EF base).</item>
/// <item><c>Raw_Insert_AdoCommand</c> — the same INSERT on a raw ADO command over a connection from the
/// same ADO pool (issue #17 option 2, which composes with #18).</item>
/// </list>
///
/// The entity is allocated once and only its <c>Id</c> changes per op, so what the numbers compare is the
/// INSERT MECHANISM, not the row construction (which the caller of <c>Persist</c> pays either way).
/// <c>[GlobalSetup]</c> writes one row through each variant and asserts, column by column, that the three
/// rows read back identically — a raw INSERT that stores a different shape would be a bug, not a win.
///
/// Allocation is deterministic and is the decision metric; the times are indicative (a real DB round-trip
/// on a shared machine).
/// </summary>
[MemoryDiagnoser]
public class PersistInsertBenchmark
{
    public static IEnumerable<string> Providers => StorageBenchEnvironment.SelectedProviders;

    [ParamsSource(nameof(Providers))]
    public string Provider { get; set; } = "sqlite";

    private StorageBenchHost    _host   = null!;
    private QueuedTaskRawInsert _raw    = null!;
    private QueuedTask          _entity = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host   = StorageBenchHost.Create(Provider);
        _raw    = new QueuedTaskRawInsert(_host);
        _entity = NewRow();

        _host.Truncate();
        VerifyIdenticalRowsAsync().GetAwaiter().GetResult();
        _host.Truncate();
    }

    [GlobalCleanup]
    public void Cleanup() => _host.Dispose();

    [Benchmark(Baseline = true)]
    public async Task Tracked_Persist()
    {
        _entity.Id = Guid.NewGuid();
        await _host.Storage.Persist(_entity);
    }

    [Benchmark]
    public async Task Raw_Insert_EfCommand()
    {
        _entity.Id = Guid.NewGuid();
        _entity.NormalizeTimestampsToUtc();

        await using var ctx = await _host.ContextFactory.CreateDbContextAsync();
        await ((DbContext)ctx).Database.ExecuteSqlRawAsync(
            _raw.Sql, (IEnumerable<object>)_raw.Parameters(_entity), CancellationToken.None);
    }

    [Benchmark]
    public async Task Raw_Insert_AdoCommand()
    {
        _entity.Id = Guid.NewGuid();
        _entity.NormalizeTimestampsToUtc();

        await using var conn = await _host.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = _raw.Sql;
        foreach (var parameter in _raw.Parameters(_entity))
            cmd.Parameters.Add(parameter);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A realistic immediate dispatch: the "carry IDs, keep it small" payload the docs recommend, a
    /// null <c>TaskKey</c> (the column is uniquely indexed) and no recurring fields.
    /// </summary>
    private static QueuedTask NewRow() => new()
    {
        Id                    = Guid.NewGuid(),
        CreatedAtUtc          = DateTimeOffset.UtcNow,
        Type                  = "EverTask.Benchmarks.Tasks.ProcessOrderTask, EverTask.Benchmarks",
        Request               = """{"OrderId":"9f0a3d21-6b7c-4f18-9a2e-51c0d4e7b8a3","Attempt":1}""",
        Handler               = "EverTask.Benchmarks.Tasks.ProcessOrderTaskHandler, EverTask.Benchmarks",
        Status                = QueuedTaskStatus.WaitingQueue,
        IsRecurring           = false,
        AuditLevel            = (int)AuditLevel.None,
        ExecutionTimeMs       = 0,
        ScheduleVersion       = 0,
        ScheduledExecutionUtc = null,
        QueueName             = null,
        TaskKey               = null,
        RuntimeInfo           = null
    };

    /// <summary>
    /// Writes one row per variant and compares every mapped column of the three rows read back through EF.
    /// Without this the raw variants could be "faster" simply by storing something else.
    /// </summary>
    private async Task VerifyIdenticalRowsAsync()
    {
        await Tracked_Persist();
        var trackedId = _entity.Id;

        await Raw_Insert_EfCommand();
        var efRawId = _entity.Id;

        await Raw_Insert_AdoCommand();
        var adoRawId = _entity.Id;

        var rows = await _host.Storage.Get(t => t.Id == trackedId || t.Id == efRawId || t.Id == adoRawId);
        if (rows.Length != 3)
            throw new InvalidOperationException($"{Provider}: expected 3 rows written, read back {rows.Length}.");

        var tracked = rows.Single(r => r.Id == trackedId);
        Compare(tracked, rows.Single(r => r.Id == efRawId), "Raw_Insert_EfCommand");
        Compare(tracked, rows.Single(r => r.Id == adoRawId), "Raw_Insert_AdoCommand");
    }

    private void Compare(QueuedTask tracked, QueuedTask raw, string variant)
    {
        foreach (var property in typeof(QueuedTask).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(QueuedTask.Id) || !property.CanWrite) continue;
            if (property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>)) continue;

            var left  = Normalize(property.GetValue(tracked));
            var right = Normalize(property.GetValue(raw));

            if (!Equals(left, right))
                throw new InvalidOperationException(
                    $"{Provider}/{variant}: column '{property.Name}' round-trips differently — " +
                    $"tracked='{left ?? "<null>"}' raw='{right ?? "<null>"}'.");
        }

        // A DateTimeOffset is compared as an instant above; on SQLite the stored TEXT keeps the offset, so
        // also assert the raw path stored the SAME textual offset the tracked one did.
        if (tracked.CreatedAtUtc.Offset != raw.CreatedAtUtc.Offset)
            throw new InvalidOperationException(
                $"{Provider}/{variant}: CreatedAtUtc stored a different offset ({tracked.CreatedAtUtc.Offset} vs {raw.CreatedAtUtc.Offset}).");
    }

    // A boxed nullable is boxed as its underlying type, so one DateTimeOffset case covers both.
    private static object? Normalize(object? value) =>
        value is DateTimeOffset dto ? dto.ToUniversalTime() : value;
}
