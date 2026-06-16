using EverTask.Abstractions;
using EverTask.LoadHarness.Tasks;
using EverTask.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Spins up a real EverTask <see cref="IHost"/> (dispatcher + worker service + scheduler) wired to a
/// chosen storage and the harness's <see cref="CountingHandler"/>. Used by the engine scenarios.
///
/// <c>storageMode</c>:
/// - <c>"null"</c> → <see cref="NullTaskStorage"/>: the "worker-only" anchor (A4) — engine minus persistence.
/// - <c>"storage"</c> → the backend named by <see cref="RunConfig.Storage"/> (L1 in-memory, L8 relational).
/// </summary>
public static class HostFactory
{
    public static async Task<HostHandle> CreateAsync(RunConfig cfg, string storageMode, CancellationToken ct = default)
    {
        Provisioned? prov = storageMode == "null"
            ? null
            : await StorageProvisioner.ProvisionAsync(cfg.Storage, ct);

        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton<RunContext>();

                var builder = services.AddEverTask(o =>
                {
                    o.RegisterTasksFromAssembly(typeof(CountingTask).Assembly);
                    o.SetMaxDegreeOfParallelism(cfg.Parallelism);
                    o.SetChannelOptions(cfg.Capacity);
                    o.SetDefaultAuditLevel(ParseAudit(cfg.Audit));
                });

                if (prov is null)
                    services.TryAddSingleton<ITaskStorage, NullTaskStorage>();
                else
                    StorageMatrix.Register(builder, prov);
            })
            .Build();

        await host.StartAsync(ct);

        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var context = host.Services.GetRequiredService<RunContext>();
        return new HostHandle(host, dispatcher, context, prov?.Description ?? "worker-only (NullStorage)",
                              prov?.Cleanup);
    }

    private static AuditLevel ParseAudit(string audit) => audit switch
    {
        "none"       => AuditLevel.None,
        "minimal"    => AuditLevel.Minimal,
        "errorsonly" => AuditLevel.ErrorsOnly,
        _            => AuditLevel.Full
    };
}

public sealed class HostHandle(
    IHost host, ITaskDispatcher dispatcher, RunContext context, string description, Func<ValueTask>? cleanup)
    : IAsyncDisposable
{
    public ITaskDispatcher Dispatcher => dispatcher;
    public RunContext Context => context;
    public string Description => description;

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
        if (cleanup is not null) await cleanup();
    }
}
