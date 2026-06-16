using BenchmarkDotNet.Attributes;
using EverTask.Abstractions;
using Newtonsoft.Json;

namespace EverTask.Benchmarks;

/// <summary>
/// L-payload [B] — task payload serialization cost vs size/shape, on the CURRENT serializer
/// (Newtonsoft.Json, `EverTaskJson`). This is the BEFORE baseline for the Newtonsoft → System.Text.Json
/// switch: re-run the same micro after the switch for a clean per-payload before/after (ns/op + byte/op).
///
/// Faithful to production: `EverTaskJson` is internal, so the settings are replicated exactly
/// (`TypeNameHandling.None` — EverTask stores the concrete type name in `QueuedTask.Type` and deserializes
/// to it, rather than emitting `$type` markers). Serialize is what `ToQueuedTask()` pays at dispatch;
/// Deserialize is what recovery / monitoring pays.
/// </summary>
[MemoryDiagnoser]
public class PayloadSerializationBenchmark
{
    private static readonly JsonSerializerSettings Settings = new() { TypeNameHandling = TypeNameHandling.None };

    // tiny = primitives only (the "keep tasks simple, pass IDs" best practice);
    // blob1k/blob64k = a linear string field; nested = an object graph (the non-linear case).
    [Params("tiny", "blob1k", "blob64k", "nested")]
    public string Payload = "tiny";

    private IEverTask _task = null!;
    private Type _type = null!;
    private string _json = null!;

    [GlobalSetup]
    public void Setup()
    {
        _task = Build(Payload);
        _type = _task.GetType();
        _json = JsonConvert.SerializeObject(_task, Settings);
        Console.WriteLine($"[{Payload}] serialized length = {_json.Length:N0} chars");
    }

    [Benchmark]
    public string Serialize() => JsonConvert.SerializeObject(_task, Settings);

    [Benchmark]
    public object? Deserialize() => JsonConvert.DeserializeObject(_json, _type, Settings);

    private static IEverTask Build(string kind) => kind switch
    {
        "tiny"    => new TinyPayload(Guid.NewGuid(), 42, DateTimeOffset.UtcNow),
        "blob1k"  => new BlobPayload(Guid.NewGuid(), new string('x', 1024)),
        "blob64k" => new BlobPayload(Guid.NewGuid(), new string('x', 64 * 1024)),
        "nested"  => new NestedPayload(Guid.NewGuid(),
                         Enumerable.Range(0, 50)
                                   .Select(i => new PayloadItem(i, $"item-{i}", DateTimeOffset.UtcNow, i * 1.5))
                                   .ToList()),
        _ => throw new ArgumentException($"Unknown payload '{kind}'")
    };
}

public record TinyPayload(Guid Id, int N, DateTimeOffset When) : IEverTask;
public record BlobPayload(Guid Id, string Data) : IEverTask;
public record NestedPayload(Guid Id, List<PayloadItem> Items) : IEverTask;
public record PayloadItem(int Index, string Name, DateTimeOffset At, double Value);
