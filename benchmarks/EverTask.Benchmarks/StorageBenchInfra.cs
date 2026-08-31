using System.Data;
using System.Data.Common;
using System.Globalization;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.Postgres;
using EverTask.Storage.Sqlite;
using EverTask.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EverTask.Benchmarks;

/// <summary>
/// Docker-backed connection strings for the storage micros (#17 <see cref="PersistInsertBenchmark"/> and
/// #18 <see cref="ProcRawAdoBenchmark"/>).
///
/// BenchmarkDotNet runs every benchmark CASE in its own child process, so a container started from
/// <c>[GlobalSetup]</c> would mean one SQL Server start per case — minutes of pure startup and a fresh,
/// differently-warmed database each time. Instead the host process provisions the containers ONCE and hands
/// the connection strings to the children through a temp file (an env var would rely on the child inheriting
/// the parent's runtime-modified environment block; the file does not).
/// </summary>
public static class StorageBenchEnvironment
{
    private static readonly string HandoffPath =
        Path.Combine(Path.GetTempPath(), "evertask-storage-bench.handoff");

    /// <summary>
    /// Which providers the storage micros run against. Defaults to all three; narrow it with
    /// <c>EVERTASK_BENCH_PROVIDERS=postgres,sqlserver</c> to run one backend at a time (SQLite writes to the
    /// temp volume, so it should not share a run with anything else measuring that disk).
    /// </summary>
    public static IEnumerable<string> SelectedProviders
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("EVERTASK_BENCH_PROVIDERS");
            return string.IsNullOrWhiteSpace(configured)
                ? ["sqlite", "postgres", "sqlserver"]
                : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>True when the command line asks for a benchmark that needs SqlServer/Postgres containers.</summary>
    public static bool NeedsDocker(string[] args) =>
        SelectedProviders.Any(p => p is "postgres" or "sqlserver")
        && args.Any(a => a.Contains("PersistInsert", StringComparison.OrdinalIgnoreCase)
                         || a.Contains("ProcRawAdo", StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts the two containers and writes the handoff file. Disposing stops them.</summary>
    public static async Task<IAsyncDisposable> ProvisionAsync()
    {
        var pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
        var ms = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

        Console.WriteLine("[storage-bench] starting postgres:16-alpine + mssql/server:2022-latest ...");
        await Task.WhenAll(pg.StartAsync(), ms.StartAsync());

        await File.WriteAllLinesAsync(HandoffPath, [
            "postgres=" + pg.GetConnectionString(),
            "sqlserver=" + ms.GetConnectionString()
        ]);
        Console.WriteLine($"[storage-bench] containers up; handoff at {HandoffPath}");

        return new Handoff(pg, ms, HandoffPath);
    }

    /// <summary>The connection string a child process should use for <paramref name="provider"/>.</summary>
    public static string Require(string provider)
    {
        if (!File.Exists(HandoffPath))
            throw new InvalidOperationException(
                $"No container handoff at '{HandoffPath}'. Run the whole benchmark through the host process " +
                "(dotnet run -- --filter *PersistInsert*), which provisions the containers.");

        foreach (var line in File.ReadAllLines(HandoffPath))
        {
            var split = line.IndexOf('=');
            if (split > 0 && line.AsSpan(0, split).SequenceEqual(provider))
                return line[(split + 1)..];
        }

        throw new InvalidOperationException($"Handoff file has no entry for '{provider}'.");
    }

    private sealed class Handoff(PostgreSqlContainer pg, MsSqlContainer ms, string path) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { File.Delete(path); } catch { /* best effort */ }
            await pg.DisposeAsync();
            await ms.DisposeAsync();
        }
    }
}

/// <summary>
/// One provider wired exactly as production wires it (<c>AddSqliteStorage</c> / <c>AddPostgresStorage</c> /
/// <c>AddSqlServerStorage</c>: pooled context factory, migrations applied, real <see cref="ITaskStorage"/>),
/// plus the raw-ADO handles the A/B alternatives need — a connection from the SAME ADO pool (identical
/// connection string) and a provider-typed <see cref="DbParameter"/> factory.
/// </summary>
public sealed class StorageBenchHost : IDisposable
{
    public string Provider { get; }
    public string ConnectionString { get; }
    public ServiceProvider Services { get; }
    public ITaskStorage Storage { get; }
    public ITaskStoreDbContextFactory ContextFactory { get; }

    /// <summary>Schema-qualified, provider-quoted table reference (e.g. <c>[EverTask].[QueuedTasks]</c>).</summary>
    public string QueuedTasksTable { get; }

    public string? Schema { get; }

    private readonly string? _sqliteDbPath;

    private StorageBenchHost(string provider, string connectionString, ServiceProvider services, string? sqliteDbPath)
    {
        Provider         = provider;
        ConnectionString = connectionString;
        Services         = services;
        _sqliteDbPath    = sqliteDbPath;
        Storage          = services.GetRequiredService<ITaskStorage>();
        ContextFactory   = services.GetRequiredService<ITaskStoreDbContextFactory>();

        using var ctx    = (DbContext)ContextFactory.CreateDbContext();
        var helper       = ctx.GetService<ISqlGenerationHelper>();
        var entityType   = ctx.Model.FindEntityType(typeof(QueuedTask))!;
        Schema           = entityType.GetSchema();
        QueuedTasksTable = helper.DelimitIdentifier(entityType.GetTableName()!, Schema);
    }

    public static StorageBenchHost Create(string provider)
    {
        string? sqlitePath = null;
        string  cs;

        var services = new ServiceCollection();
        // Warning floor: the storage layer's per-write Debug logs must not be part of what we measure
        // (they are identical on both sides of every A/B, but they would add noise for nothing).
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var builder = services.AddEverTask(o => o.RegisterTasksFromAssembly(typeof(StorageBenchHost).Assembly));

        switch (provider)
        {
            case "sqlite":
                sqlitePath = Path.Combine(Path.GetTempPath(), $"evertask-storagebench-{Guid.NewGuid():N}.db");
                cs         = $"Data Source={sqlitePath};Cache=Shared";
                // WAL: the production recommendation, and what StorageProvisioner uses in the load harness.
                using (var pragmaConn = new SqliteConnection(cs))
                {
                    pragmaConn.Open();
                    using var pragma = pragmaConn.CreateCommand();
                    pragma.CommandText = "PRAGMA journal_mode=WAL;";
                    pragma.ExecuteNonQuery();
                }
                builder.AddSqliteStorage(cs);
                break;

            case "postgres":
                cs = StorageBenchEnvironment.Require("postgres");
                builder.AddPostgresStorage(cs);
                break;

            case "sqlserver":
                cs = StorageBenchEnvironment.Require("sqlserver");
                builder.AddSqlServerStorage(cs);
                break;

            default:
                throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        }

        return new StorageBenchHost(provider, cs, services.BuildServiceProvider(), sqlitePath);
    }

    public DbConnection OpenConnection()
    {
        DbConnection conn = Provider switch
        {
            "sqlite"    => new SqliteConnection(ConnectionString),
            "postgres"  => new NpgsqlConnection(ConnectionString),
            "sqlserver" => new SqlConnection(ConnectionString),
            _           => throw new InvalidOperationException()
        };
        conn.Open();
        return conn;
    }

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        DbConnection conn = Provider switch
        {
            "sqlite"    => new SqliteConnection(ConnectionString),
            "postgres"  => new NpgsqlConnection(ConnectionString),
            "sqlserver" => new SqlConnection(ConnectionString),
            _           => throw new InvalidOperationException()
        };
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// A provider-typed parameter. The type is set EXPLICITLY, never inferred: Npgsql cannot resolve a
    /// <see cref="DBNull"/> in a position PostgreSQL will not infer (42P08) and SqlClient would type a null
    /// as <c>nvarchar</c> against a <c>datetimeoffset</c> column. This is part of the cost of raw SQL, so it
    /// belongs inside the measured op.
    /// </summary>
    public DbParameter Parameter(string name, object? value, Type clrType)
    {
        var boxed = value ?? DBNull.Value;

        switch (Provider)
        {
            case "sqlite":
                return new SqliteParameter(name, boxed);

            case "postgres":
                return new NpgsqlParameter(name, NpgsqlTypeOf(clrType)) { Value = boxed };

            case "sqlserver":
                return new SqlParameter(name, SqlTypeOf(clrType)) { Value = boxed };

            default:
                throw new InvalidOperationException();
        }
    }

    private static NpgsqlDbType NpgsqlTypeOf(Type t) =>
        Nullable.GetUnderlyingType(t) is { } u ? NpgsqlTypeOf(u)
        : t == typeof(Guid)           ? NpgsqlDbType.Uuid
        : t == typeof(DateTimeOffset) ? NpgsqlDbType.TimestampTz
        : t == typeof(double)         ? NpgsqlDbType.Double
        : t == typeof(bool)           ? NpgsqlDbType.Boolean
        : t == typeof(int)            ? NpgsqlDbType.Integer
        : t == typeof(long)           ? NpgsqlDbType.Bigint
        : t == typeof(string)         ? NpgsqlDbType.Text
        : throw new NotSupportedException(t.Name);

    private static SqlDbType SqlTypeOf(Type t) =>
        Nullable.GetUnderlyingType(t) is { } u ? SqlTypeOf(u)
        : t == typeof(Guid)           ? SqlDbType.UniqueIdentifier
        : t == typeof(DateTimeOffset) ? SqlDbType.DateTimeOffset
        : t == typeof(double)         ? SqlDbType.Float
        : t == typeof(bool)           ? SqlDbType.Bit
        : t == typeof(int)            ? SqlDbType.Int
        : t == typeof(long)           ? SqlDbType.BigInt
        : t == typeof(string)         ? SqlDbType.NVarChar
        : throw new NotSupportedException(t.Name);

    /// <summary>Empties the task tables so every case starts from a comparable table size.</summary>
    public void Truncate()
    {
        using var ctx = (DbContext)ContextFactory.CreateDbContext();
        var helper    = ctx.GetService<ISqlGenerationHelper>();

        string Table<T>() where T : class
        {
            var et = ctx.Model.FindEntityType(typeof(T))!;
            return helper.DelimitIdentifier(et.GetTableName()!, et.GetSchema());
        }

        // Children first: the audit FKs cascade, but TaskExecutionLogs and the self-referencing occurrence FK
        // (Restrict) do not.
        foreach (var table in new[] { Table<TaskExecutionLog>(), Table<RunsAudit>(), Table<StatusAudit>(), Table<QueuedTask>() })
        {
            // Concatenated, not interpolated: EF1002 flags an interpolated literal even when every part is
            // a model-derived, provider-quoted identifier.
            var delete = "DELETE FROM " + table;
            ctx.Database.ExecuteSqlRaw(delete);
        }
    }

    public void Dispose()
    {
        Services.Dispose();
        if (_sqliteDbPath is null) return;

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_sqliteDbPath + suffix)) File.Delete(_sqliteDbPath + suffix); }
            catch { /* best effort */ }
        }
    }
}

/// <summary>
/// The raw <c>INSERT INTO QueuedTasks</c> equivalent of the tracked <c>Add</c> + <c>SaveChanges</c>.
///
/// The COLUMN LIST is derived from the EF model, so a schema change that adds a column and is not reflected
/// in <see cref="Values"/> makes the benchmark throw instead of silently measuring a narrower insert. The
/// VALUES are hand-written exactly as a production raw implementation would have to write them — including
/// the <c>Status</c> string conversion the model declares.
/// </summary>
public sealed class QueuedTaskRawInsert
{
    /// <summary>Property order = parameter order = column order.</summary>
    private static readonly string[] PropertyOrder =
    [
        nameof(QueuedTask.Id),
        nameof(QueuedTask.CreatedAtUtc),
        nameof(QueuedTask.LastExecutionUtc),
        nameof(QueuedTask.ExecutionTimeMs),
        nameof(QueuedTask.ScheduledExecutionUtc),
        nameof(QueuedTask.Type),
        nameof(QueuedTask.Request),
        nameof(QueuedTask.Handler),
        nameof(QueuedTask.Exception),
        nameof(QueuedTask.Status),
        nameof(QueuedTask.IsRecurring),
        nameof(QueuedTask.RecurringTask),
        nameof(QueuedTask.RecurringInfo),
        nameof(QueuedTask.CurrentRunCount),
        nameof(QueuedTask.MaxRuns),
        nameof(QueuedTask.RunUntil),
        nameof(QueuedTask.NextRunUtc),
        nameof(QueuedTask.QueueName),
        nameof(QueuedTask.TaskKey),
        nameof(QueuedTask.AuditLevel),
        nameof(QueuedTask.RecoveryDispatchFailureCount),
        nameof(QueuedTask.ParentTaskId),
        nameof(QueuedTask.RuntimeInfo),
        nameof(QueuedTask.ScheduleVersion)
    ];

    private readonly StorageBenchHost _host;
    private readonly Type[]           _clrTypes;
    private readonly string[]         _names;

    public string Sql { get; }

    public QueuedTaskRawInsert(StorageBenchHost host)
    {
        _host = host;

        using var ctx  = (DbContext)host.ContextFactory.CreateDbContext();
        var helper     = ctx.GetService<ISqlGenerationHelper>();
        var entityType = ctx.Model.FindEntityType(typeof(QueuedTask))!;
        var table      = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());

        var mapped = entityType.GetProperties()
                               .Where(p => p.GetColumnName(table) is not null)
                               .ToDictionary(p => p.Name, StringComparer.Ordinal);

        // Schema-drift guard: measuring a narrower INSERT than production writes would be worse than failing.
        var missing = mapped.Keys.Except(PropertyOrder, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"QueuedTask has mapped columns the raw INSERT does not write: {string.Join(", ", missing)}. " +
                "Add them to PropertyOrder and Values.");

        var columns = new string[PropertyOrder.Length];
        _clrTypes   = new Type[PropertyOrder.Length];
        _names      = new string[PropertyOrder.Length];

        for (var i = 0; i < PropertyOrder.Length; i++)
        {
            var property  = mapped[PropertyOrder[i]];
            // The STORE type, not the CLR one: Status is an enum mapped through HasConversion<string>().
            var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
            columns[i]    = helper.DelimitIdentifier(property.GetColumnName(table)!);
            _clrTypes[i]  = converter?.ProviderClrType ?? property.ClrType;
            _names[i]     = "@p" + i.ToString(CultureInfo.InvariantCulture);
        }

        Sql = $"INSERT INTO {host.QueuedTasksTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", _names)})";
    }

    /// <summary>
    /// The parameter array for one row. Boxing 24 values into <see cref="DbParameter"/>s is the irreducible
    /// cost of the raw path — the same shape <c>SqlServerTaskStorage</c> already pays for its procs.
    /// </summary>
    public DbParameter[] Parameters(QueuedTask e)
    {
        var values = Values(e);
        var result = new DbParameter[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = _host.Parameter(_names[i], values[i], _clrTypes[i]);
        return result;
    }

    private static object?[] Values(QueuedTask e) =>
    [
        e.Id,
        e.CreatedAtUtc,
        e.LastExecutionUtc,
        e.ExecutionTimeMs,
        e.ScheduledExecutionUtc,
        e.Type,
        e.Request,
        e.Handler,
        e.Exception,
        // The model maps Status with HasConversion<string>(); the raw INSERT has to reproduce it.
        e.Status.ToString(),
        e.IsRecurring,
        e.RecurringTask,
        e.RecurringInfo,
        e.CurrentRunCount,
        e.MaxRuns,
        e.RunUntil,
        e.NextRunUtc,
        e.QueueName,
        e.TaskKey,
        e.AuditLevel,
        e.RecoveryDispatchFailureCount,
        e.ParentTaskId,
        e.RuntimeInfo,
        e.ScheduleVersion
    ];
}
