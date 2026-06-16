namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// A4-worker — the real EverTask engine (dispatch → serialize → enqueue → worker → executor →
/// handler) over <see cref="Infra.NullTaskStorage"/>, so persistence costs nothing. Isolates engine
/// overhead: the gap to the raw-channel A1 is what wrappers/executor/registry/blacklist add; the gap to
/// L1 (in-memory) is the in-memory storage cost (BENCHMARK_PLAN §4).
/// </summary>
public sealed class A4WorkerOnly : EngineScenarioBase
{
    protected override string StorageMode => "null";
    public override string Id => "A4W";
    public override string Description => "worker-only: real engine over NullTaskStorage (no persistence)";
}
