using System.Diagnostics;
using System.Threading.Channels;
using EverTask.LoadHarness.Infra;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// A1 — raw <see cref="Channel{T}"/> + worker pool, no EverTask. The theoretical ceiling of the engine:
/// a bounded channel, one producer, N consumers running a no-op. Every EverTask throughput number is
/// read as "% overhead above A1" (BENCHMARK_PLAN §4). A struct payload keeps per-task allocation at the
/// floor, so A1's bytes/task ≈ 0 — exactly the reference we want.
/// </summary>
public sealed class A1RawChannelCeiling : IScenario
{
    public string Id => "A1";
    public string Description => "raw Channel<T> + worker pool ceiling (no EverTask)";

    private readonly record struct Item(long DispatchTicks);

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        int producerCount = Math.Max(1, cfg.Producers);
        var channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(cfg.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = producerCount == 1,
            AllowSynchronousContinuations = false
        });

        long perProducer = cfg.Count / producerCount;
        long count = perProducer * producerCount; // drop the remainder so producers are identical
        var sw = Stopwatch.StartNew();

        // Consumers: each competes for items off the shared reader; latency = dequeue − dispatch.
        var consumers = new Task[cfg.Parallelism];
        for (int c = 0; c < cfg.Parallelism; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    latency.Record(Stopwatch.GetTimestamp() - item.DispatchTicks);
            }, ct);
        }

        // Multiple producers, full-speed; Wait back-pressure caps in-flight at Capacity.
        // Multi-writer mirrors EverTask's channel (dispatcher + scheduler + recovery all write).
        var producers = new Task[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            producers[p] = Task.Run(async () =>
            {
                for (long i = 0; i < perProducer; i++)
                    await channel.Writer.WriteAsync(new Item(Stopwatch.GetTimestamp()), ct).ConfigureAwait(false);
            }, ct);
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        channel.Writer.Complete();
        await Task.WhenAll(consumers).ConfigureAwait(false);
        sw.Stop();

        return new IterationOutcome(count, sw.Elapsed.TotalSeconds);
    }
}
