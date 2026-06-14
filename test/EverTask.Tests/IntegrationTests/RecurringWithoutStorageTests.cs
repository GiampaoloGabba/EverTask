using EverTask.Abstractions;
using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// F18: a recurring series scheduled the next occurrence only inside the storage guard, so with no
/// storage registered it ran exactly once and the series died silently. The next-occurrence
/// calculation/scheduling must happen regardless of storage (only persistence is gated on it).
/// </summary>
public class RecurringWithoutStorageTests
{
    [Fact]
    public async Task Should_keep_recurring_alive_without_storage()
    {
        NoStorageRecurringHandler.Reset();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                // NOTE: no AddMemoryStorage()/AddSqlStorage() — the worker's taskStorage is null.
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(NoStorageRecurringTask).Assembly)
                       .SetChannelOptions(50)
                       .SetMaxDegreeOfParallelism(4);
                });
            })
            .Build();

        await host.StartAsync();

        try
        {
            var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
            host.Services.GetService<ITaskStorage>().ShouldBeNull("the test runs with no storage registered");

            // RunNow + every second, bounded to two runs. The second run can only happen if the series
            // is rescheduled without storage.
            await dispatcher.Dispatch(new NoStorageRecurringTask(),
                r => r.RunNow().Then().Every(1).Seconds().MaxRuns(2));

            var reachedTwo = await WaitForExecutionsAsync(2, TimeSpan.FromSeconds(10));
            reachedTwo.ShouldBeTrue($"the recurring series must run more than once; saw {NoStorageRecurringHandler.Executions}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<bool> WaitForExecutionsAsync(int target, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Volatile.Read(ref NoStorageRecurringHandler.Executions) >= target)
                return true;
            await Task.Delay(50);
        }
        return Volatile.Read(ref NoStorageRecurringHandler.Executions) >= target;
    }
}
