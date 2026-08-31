using System.Data.Common;
using BenchmarkDotNet.Attributes;
using EverTask.Abstractions;
using EverTask.Storage;
using Microsoft.EntityFrameworkCore;

namespace EverTask.Benchmarks;

/// <summary>
/// Issue #18 — the SQL Server procs and the Postgres CTEs are already single-round-trip raw SQL, but they are
/// invoked through a pooled <c>DbContext</c> used purely as a vehicle: no change tracking, no model, no query
/// translation, only the connection and the raw command. This measures what the vehicle costs.
///
/// The op measured is the <c>Queued</c> status write at <c>AuditLevel.None</c> — literally the
/// <c>SetQueued</c> call <c>WorkerQueue</c> makes after enqueuing, which is also the write issue #16 wants to
/// fold into <c>Persist</c>. So the baseline column doubles as #16's "what does one SetQueued cost".
///
/// Three ways to issue the SAME statement, per provider:
/// <list type="bullet">
/// <item><c>Production_SetQueued</c> — <see cref="ITaskStorage.SetQueued"/>, exactly what the worker calls.</item>
/// <item><c>Ef_ExecuteSqlRaw</c> — pooled <c>CreateDbContextAsync</c> + <c>ExecuteSqlRawAsync</c> of the same
/// proc/CTE with the same parameters (the production shape, minus the storage class's own work).</item>
/// <item><c>Raw_AdoCommand</c> — the same SQL on a raw <c>SqlCommand</c>/<c>NpgsqlCommand</c> over a
/// connection from the SAME ADO pool (identical connection string, so no connection churn).</item>
/// </list>
///
/// SQLite is included for #16's per-provider SetQueued cost and as context for #19: it has no proc/CTE, so
/// its baseline is the base relational path (explicit transaction + <c>ExecuteUpdate</c>) and its two raw
/// rows are the plain UPDATE that path reduces to when no audit row is written. Those SQLite raw rows are
/// NOT issue #18 (which is scoped to the proc/CTE providers).
///
/// Allocation is deterministic and is the decision metric; times are indicative.
/// </summary>
[MemoryDiagnoser]
public class ProcRawAdoBenchmark
{
    public static IEnumerable<string> Providers => StorageBenchEnvironment.SelectedProviders;

    [ParamsSource(nameof(Providers))]
    public string Provider { get; set; } = "sqlite";

    private StorageBenchHost _host = null!;
    private Guid             _taskId;
    private string           _sql = null!;

    private const QueuedTaskStatus TargetStatus = QueuedTaskStatus.Queued;

    [GlobalSetup]
    public void Setup()
    {
        _host = StorageBenchHost.Create(Provider);
        _host.Truncate();

        _taskId = Guid.NewGuid();
        _host.Storage.Persist(new QueuedTask
        {
            Id           = _taskId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Type         = "EverTask.Benchmarks.Tasks.ProcessOrderTask, EverTask.Benchmarks",
            Request      = """{"OrderId":"9f0a3d21-6b7c-4f18-9a2e-51c0d4e7b8a3","Attempt":1}""",
            Handler      = "EverTask.Benchmarks.Tasks.ProcessOrderTaskHandler, EverTask.Benchmarks",
            Status       = QueuedTaskStatus.WaitingQueue,
            AuditLevel   = (int)AuditLevel.None
        }).GetAwaiter().GetResult();

        _sql = BuildSql();

        // Every variant must actually hit the row; a proc that silently swallowed (SetStatus does) or a
        // statement that matched nothing would measure an empty write.
        Production_SetQueued().GetAwaiter().GetResult();
        AssertQueued(nameof(Production_SetQueued));
        ResetStatus();

        Ef_ExecuteSqlRaw().GetAwaiter().GetResult();
        AssertQueued(nameof(Ef_ExecuteSqlRaw));
        ResetStatus();

        Raw_AdoCommand().GetAwaiter().GetResult();
        AssertQueued(nameof(Raw_AdoCommand));
    }

    [GlobalCleanup]
    public void Cleanup() => _host.Dispose();

    [Benchmark(Baseline = true)]
    public Task Production_SetQueued() => _host.Storage.SetQueued(_taskId, AuditLevel.None);

    [Benchmark]
    public async Task Ef_ExecuteSqlRaw()
    {
        await using var ctx = await _host.ContextFactory.CreateDbContextAsync();
        await ((DbContext)ctx).Database.ExecuteSqlRawAsync(
            _sql, (IEnumerable<object>)StatusParameters(), CancellationToken.None);
    }

    [Benchmark]
    public async Task Raw_AdoCommand()
    {
        await using var conn = await _host.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = _sql;
        foreach (var parameter in StatusParameters())
            cmd.Parameters.Add(parameter);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The provider's own hot-path statement, verbatim from the storage class (schema from the EF model).
    /// </summary>
    private string BuildSql() => Provider switch
    {
        "sqlserver" =>
            $"EXEC [{_host.Schema}].[usp_SetTaskStatus] @TaskId, @Status, @Exception, @AuditLevel, @ExecutionTimeMs",

        "postgres" =>
            $"""

             WITH updated AS (
                 UPDATE "{_host.Schema}"."QueuedTasks"
                 SET "Status"           = @status,
                     "Exception"        = @exception,
                     "LastExecutionUtc" = CASE WHEN @stampLast THEN now() ELSE "LastExecutionUtc" END,
                     "ExecutionTimeMs"  = CASE WHEN @hasExecTime THEN @execTime ELSE "ExecutionTimeMs" END
                 WHERE "Id" = @taskId
                 RETURNING "Id"
             )
             INSERT INTO "{_host.Schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
             SELECT @taskId, now(), @status, @exception FROM updated WHERE @createAudit;
             """,

        // No proc/CTE on SQLite: the statement the base's no-audit SetStatus reduces to.
        "sqlite" =>
            """
            UPDATE "QueuedTasks"
            SET "Status" = @status,
                "LastExecutionUtc" = COALESCE(@lastExecution, "LastExecutionUtc"),
                "Exception" = @exception
            WHERE "Id" = @taskId
            """,

        _ => throw new InvalidOperationException()
    };

    /// <summary>The parameters the production call builds for this write, per provider.</summary>
    private DbParameter[] StatusParameters() => Provider switch
    {
        "sqlserver" =>
        [
            _host.Parameter("@TaskId", _taskId, typeof(Guid)),
            _host.Parameter("@Status", TargetStatus.ToString(), typeof(string)),
            _host.Parameter("@Exception", null, typeof(string)),
            _host.Parameter("@AuditLevel", (int)AuditLevel.None, typeof(int)),
            _host.Parameter("@ExecutionTimeMs", null, typeof(double))
        ],

        "postgres" =>
        [
            _host.Parameter("taskId", _taskId, typeof(Guid)),
            _host.Parameter("status", TargetStatus.ToString(), typeof(string)),
            _host.Parameter("exception", null, typeof(string)),
            // Queued is not a terminal transition: LastExecutionUtc is preserved, no execution time.
            _host.Parameter("stampLast", false, typeof(bool)),
            _host.Parameter("hasExecTime", false, typeof(bool)),
            _host.Parameter("execTime", 0d, typeof(double)),
            _host.Parameter("createAudit", false, typeof(bool))
        ],

        "sqlite" =>
        [
            _host.Parameter("@status", TargetStatus.ToString(), typeof(string)),
            _host.Parameter("@lastExecution", null, typeof(DateTimeOffset)),
            _host.Parameter("@exception", null, typeof(string)),
            _host.Parameter("@taskId", _taskId, typeof(Guid))
        ],

        _ => throw new InvalidOperationException()
    };

    private void AssertQueued(string variant)
    {
        var row = _host.Storage.Get(t => t.Id == _taskId).GetAwaiter().GetResult().Single();
        if (row.Status != TargetStatus)
            throw new InvalidOperationException(
                $"{Provider}/{variant}: the write did not reach the row (status is {row.Status}).");
    }

    private void ResetStatus() =>
        _host.Storage.SetStatus(_taskId, QueuedTaskStatus.WaitingQueue, null, AuditLevel.None)
             .GetAwaiter().GetResult();
}
