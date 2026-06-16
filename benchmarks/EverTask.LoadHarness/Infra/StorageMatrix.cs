using EverTask.LoadHarness.Tasks;
using EverTask.Storage;
using EverTask.Storage.Postgres;
using EverTask.Storage.Sqlite;
using EverTask.Storage.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Builds a standalone <see cref="ITaskStorage"/> for each backend (A4-storage / A3). Provisioning of
/// the external resource (SQLite temp file, Docker container) is delegated to
/// <see cref="StorageProvisioner"/>; this just wires the storage into a minimal DI graph. Logging is
/// pinned to Warning so the per-write Info logs don't pollute the measurement (BENCHMARK_PLAN §5).
/// </summary>
public static class StorageMatrix
{
    public static async Task<StorageHandle> CreateAsync(string storage, CancellationToken ct = default)
    {
        var prov = await StorageProvisioner.ProvisionAsync(storage, ct);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var builder = services.AddEverTask(o => o.RegisterTasksFromAssembly(typeof(CountingTask).Assembly));
        Register(builder, prov);

        var provider = services.BuildServiceProvider();
        var storageSvc = provider.GetRequiredService<ITaskStorage>();

        return new StorageHandle(storageSvc, prov.Description, async () =>
        {
            await provider.DisposeAsync();
            await prov.Cleanup();
        });
    }

    /// <summary>Register the provisioned backend on an EverTask builder. Shared with <see cref="HostFactory"/>.</summary>
    public static void Register(EverTaskServiceBuilder builder, Provisioned p)
    {
        switch (p.Kind)
        {
            case "inmemory":  builder.AddMemoryStorage(); break;
            case "sqlite":    builder.AddSqliteStorage(p.ConnectionString!); break;
            case "sqlserver": builder.AddSqlServerStorage(p.ConnectionString!); break;
            case "postgres":  builder.AddPostgresStorage(p.ConnectionString!); break;
            default: throw new ArgumentException($"Unknown storage kind '{p.Kind}'.");
        }
    }
}

/// <summary>An <see cref="ITaskStorage"/> plus the disposables that back it.</summary>
public sealed class StorageHandle(ITaskStorage storage, string description, Func<ValueTask> dispose) : IAsyncDisposable
{
    public ITaskStorage Storage => storage;
    public string Description => description;
    public ValueTask DisposeAsync() => dispose();
}
