using System.Diagnostics;
using EverTask.LoadHarness.Infra;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// A2 — bare <see cref="Task.Run(Action)"/> floor. The minimum the thread-pool itself costs to take a
/// unit of work and start running it: the physical floor for "dispatch → start" latency. EverTask can
/// never beat this; the question A2 answers is how far above the floor it sits (BENCHMARK_PLAN §4).
///
/// Work is split across <c>parallelism</c> outer loops, each measuring the latency of scheduling a
/// no-op <see cref="Task.Run(Action)"/> and it actually starting. Completion is by await (no shared
/// counters), so the floor stays clean.
/// </summary>
public sealed class A2BareTaskRunFloor : IScenario
{
    public string Id => "A2";
    public string Description => "bare Task.Run(noop) thread-pool floor";

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        long count = cfg.Count;
        int lanes = cfg.Parallelism;
        long perLane = count / lanes;
        long total = perLane * lanes; // ignore the remainder so every lane is identical

        var sw = Stopwatch.StartNew();

        var lanesTasks = new Task[lanes];
        for (int l = 0; l < lanes; l++)
        {
            lanesTasks[l] = Task.Run(async () =>
            {
                for (long i = 0; i < perLane; i++)
                {
                    long scheduled = Stopwatch.GetTimestamp();
                    await Task.Run(() => latency.Record(Stopwatch.GetTimestamp() - scheduled), ct)
                              .ConfigureAwait(false);
                }
            }, ct);
        }

        await Task.WhenAll(lanesTasks).ConfigureAwait(false);
        sw.Stop();

        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }
}
