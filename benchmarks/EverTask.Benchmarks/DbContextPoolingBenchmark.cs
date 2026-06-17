using BenchmarkDotNet.Attributes;
using EverTask.Storage;
using EverTask.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Benchmarks;

/// <summary>
/// P-? — DbContext-per-task allocation on the EF Core storage path.
///
/// The relational hot path (Persist/SetInProgress/SetCompleted, even when the SQL itself is a stored
/// proc / writable CTE) opens a FRESH DbContext per call: <c>await contextFactory.CreateDbContextAsync()</c>.
/// A task's lifecycle does ~4 such calls → ~4 DbContexts/task, and the providers register
/// <c>AddDbContextFactory</c> (NOT pooled) despite comments/CHANGELOG claiming "built-in pooling".
/// This A/B quantifies what <c>AddPooledDbContextFactory</c> saves.
///
/// As of the pooling refactor the production <see cref="SqliteTaskStoreContext"/> IS poolable (its ctor
/// now takes a single <c>DbContextOptions</c>; the schema travels via <c>UseEverTaskSchema</c>) and
/// <c>AddSqliteStorage</c> registers <c>AddPooledDbContextFactory</c>. So <see cref="Create_Real_Pooled"/>
/// resolves the production POOLED factory and should match <see cref="Create_Proxy_Pooled"/> — that is the
/// end-to-end proof the win actually landed in production. The proxy pair (non-pooled baseline vs pooled)
/// remains as the before/after contrast.
/// </summary>
[MemoryDiagnoser]
public class DbContextPoolingBenchmark
{
    private string _dbPath = null!;
    private ServiceProvider _realProvider = null!;
    private ServiceProvider _proxyNonPooledProvider = null!;
    private ServiceProvider _proxyPooledProvider = null!;

    private IDbContextFactory<SqliteTaskStoreContext> _realFactory = null!;
    private IDbContextFactory<ProxyTaskContext> _proxyNonPooled = null!;
    private IDbContextFactory<ProxyTaskContext> _proxyPooled = null!;

    private const string NoopUpdate = "UPDATE QueuedTasks SET Status = Status WHERE Id = {0}";
    private static readonly string NoopId = Guid.NewGuid().ToString();

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"evertask-pool-bench-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={_dbPath};Cache=Shared";

        // Production wiring (now POOLED after the refactor). AddSqliteStorage also migrates the schema.
        var realServices = new ServiceCollection();
        realServices.AddLogging();
        realServices.AddEverTask(o => o.RegisterTasksFromAssembly(typeof(DbContextPoolingBenchmark).Assembly))
                    .AddSqliteStorage(cs);
        _realProvider = realServices.BuildServiceProvider();
        _realFactory = _realProvider.GetRequiredService<IDbContextFactory<SqliteTaskStoreContext>>();

        // Pool-compatible proxy, non-pooled (the same EF machinery, minus the pooling).
        var npServices = new ServiceCollection();
        npServices.AddDbContextFactory<ProxyTaskContext>(o => o.UseSqlite(cs));
        _proxyNonPooledProvider = npServices.BuildServiceProvider();
        _proxyNonPooled = _proxyNonPooledProvider.GetRequiredService<IDbContextFactory<ProxyTaskContext>>();

        // Pool-compatible proxy, pooled (the achievable target).
        var pServices = new ServiceCollection();
        pServices.AddPooledDbContextFactory<ProxyTaskContext>(o => o.UseSqlite(cs));
        _proxyPooledProvider = pServices.BuildServiceProvider();
        _proxyPooled = _proxyPooledProvider.GetRequiredService<IDbContextFactory<ProxyTaskContext>>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _realProvider.Dispose();
        _proxyNonPooledProvider.Dispose();
        _proxyPooledProvider.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); }
            catch { /* best effort */ }
        }
    }

    // --- CreateDbContext + dispose: isolates the pure per-context allocation ---

    [Benchmark(Baseline = true)]
    public async Task Create_Proxy_NonPooled()
    {
        await using var ctx = await _proxyNonPooled.CreateDbContextAsync();
    }

    // Production wiring, now pooled — should match Create_Proxy_Pooled (the win landed in production).
    [Benchmark]
    public async Task Create_Real_Pooled()
    {
        await using var ctx = await _realFactory.CreateDbContextAsync();
    }

    [Benchmark]
    public async Task Create_Proxy_Pooled()
    {
        await using var ctx = await _proxyPooled.CreateDbContextAsync();
    }

    // --- CreateDbContext + one ExecuteSqlRaw write + dispose: the realistic per-op shape ---

    [Benchmark]
    public async Task Execute_Proxy_NonPooled()
    {
        await using var ctx = await _proxyNonPooled.CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync(NoopUpdate, NoopId);
    }

    [Benchmark]
    public async Task Execute_Proxy_Pooled()
    {
        await using var ctx = await _proxyPooled.CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync(NoopUpdate, NoopId);
    }
}

/// <summary>
/// Pool-compatible proxy: single DbContextOptions ctor (so <c>AddPooledDbContextFactory</c> accepts it),
/// mapping one real entity. Navigations are ignored to keep the model robust — model size is built once
/// and cached, so it doesn't change the per-context allocation we're measuring.
/// </summary>
public class ProxyTaskContext(DbContextOptions<ProxyTaskContext> options) : DbContext(options)
{
    public DbSet<QueuedTask> QueuedTasks => Set<QueuedTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueuedTask>()
                    .Ignore(q => q.StatusAudits)
                    .Ignore(q => q.RunsAudits)
                    .Ignore(q => q.ExecutionLogs);
    }
}
