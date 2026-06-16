using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Scenarios;

// EverTask load harness — see benchmarks/BENCHMARK_PLAN.md.
// Usage:
//   dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- <scenario> [--key value ...]
// Scenarios today (Tier 0 anchors):
//   A1   raw Channel<T> ceiling        A2   bare Task.Run floor
//   A3   naive DB-polling reference    A4S  storage-only (3 writes)    A4W  worker-only (engine, no persistence)
//   tier0  run A1, A2, A4W (no DB)      anchors  run all Tier-0 (A4S/A3 honour --storage)
// Common knobs: --count 1m --parallelism 16 --producers 4 --capacity 2000 --warmup 3 --measured 7
//               --storage inmemory|sqlite|sqlserver|postgres   --poll-interval 1000   --out benchmarks/results

var scenarioId = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToUpperInvariant() : "TIER0";
int configStart = args.Length > 0 && !args[0].StartsWith("--") ? 1 : 0;
var cfg = RunConfig.Parse(args, configStart);

var registry = new Dictionary<string, IScenario>(StringComparer.OrdinalIgnoreCase)
{
    ["A1"] = new A1RawChannelCeiling(),
    ["A2"] = new A2BareTaskRunFloor(),
    ["A3"] = new A3NaivePolling(),
    ["A4S"] = new A4StorageOnly(),
    ["A4W"] = new A4WorkerOnly(),
    ["L8"] = new L8Lifecycle(),
    ["LDP"] = new LDispatchProd()
};

IReadOnlyList<IScenario> toRun = scenarioId switch
{
    // tier0 = the no-DB anchors, safe to run anywhere.
    "TIER0" => [registry["A1"], registry["A2"], registry["A4W"]],
    // anchors = the full Tier-0 set; A4S/A3 use --storage (inmemory by default).
    "ANCHORS" => [registry["A1"], registry["A2"], registry["A4W"], registry["A4S"], registry["A3"]],
    // tier1 = the production headline pair (both honour --storage).
    "TIER1" => [registry["L8"], registry["LDP"]],
    _ when registry.TryGetValue(scenarioId, out var s) => [s],
    _ => []
};

if (toRun.Count == 0)
{
    Console.Error.WriteLine($"Unknown scenario '{scenarioId}'. Known: {string.Join(", ", registry.Keys)}, tier0.");
    return 1;
}

var env = EnvInfo.Capture();
env.Print();

foreach (var scenario in toRun)
    await Runner.RunAsync(scenario, cfg, env);

return 0;
