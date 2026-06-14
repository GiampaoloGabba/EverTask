using EverTask.Abstractions;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// G3: a handler registered manually as a singleton on the interface must not be shared across
/// concurrent eager dispatches. The worker sets per-execution state on the handler instance, so a
/// shared instance corrupts concurrent executions. The eager path must resolve a fresh instance per
/// dispatch (via the concrete type), exactly like the lazy path does at execution time.
/// </summary>
public class HandlerRegistrationIntegrationTests
{
    [Fact]
    public async Task Should_not_corrupt_eager_handler_with_manual_singleton_registration()
    {
        const int dispatchCount = 12;
        EagerSharedStateHandler.Reset(dispatchCount);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                // Manual handler registration BEFORE AddEverTask: a singleton bound to the interface.
                services.AddSingleton<IEverTaskHandler<EagerSharedStateTask>, EagerSharedStateHandler>();

                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(EagerSharedStateTask).Assembly)
                       .SetChannelOptions(100)
                       .SetMaxDegreeOfParallelism(8)
                       .DisableLazyHandlerResolution(); // force eager resolution
                })
                .AddMemoryStorage();
            })
            .Build();

        await host.StartAsync();

        try
        {
            var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();

            await Parallel.ForEachAsync(Enumerable.Range(0, dispatchCount), async (i, ct) =>
            {
                await dispatcher.Dispatch(new EagerSharedStateTask(i), cancellationToken: ct);
            });

            EagerSharedStateHandler.Countdown!.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue(
                $"expected {dispatchCount} executions, saw {EagerSharedStateHandler.SeenInstanceIds.Count}");

            // Each eager dispatch must get its OWN handler instance, not the shared singleton.
            EagerSharedStateHandler.SeenInstanceIds.Distinct().Count().ShouldBe(dispatchCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
