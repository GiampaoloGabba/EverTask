namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// L8 — the PRODUCTION throughput number: the full task lifecycle through the real engine against a
/// durable store (Persist on dispatch + SetInProgress + handler + SetCompleted), per <c>--storage</c>.
/// This is "how much EverTask really sustains" in the config real apps run. Compare across storages
/// (delta vs In-Memory = DB cost) and against A4-worker (delta = persistence cost) — BENCHMARK_PLAN §5.
///
/// Parallelism scales this on real concurrent-writer DBs (SqlServer/Postgres + connection pools); on
/// SQLite (single writer) it stays write-bound regardless of parallelism.
/// </summary>
public sealed class L8Lifecycle : EngineScenarioBase
{
    protected override string StorageMode => "storage";
    public override string Id => "L8";
    public override string Description => "full lifecycle throughput per --storage (production primary)";
}
