using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using EverTask.Abstractions;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EverTask.Benchmarks;

/// <summary>
/// L-payload [B] — task payload serialization cost vs size/shape, as an A/B between the OLD serializer
/// (Newtonsoft.Json, the pre-3.10 <c>EverTaskJson</c>) and the NEW one (System.Text.Json, the current
/// <c>EverTaskJson</c>). Newtonsoft is the <see cref="BenchmarkAttribute.Baseline"/> so the ratio column
/// reads as "STJ vs Newtonsoft" directly. Measuring both in ONE run removes cross-run machine drift — the
/// alloc deltas are an apples-to-apples per-payload before/after.
///
/// Faithful to production: <c>EverTaskJson</c> is internal, so the settings are replicated exactly on both
/// sides. OLD = <c>TypeNameHandling.None</c> (the concrete type lives in <c>QueuedTask.Type</c>, no
/// <c>$type</c> markers). NEW = the STJ options EverTaskJson now uses (PascalCase / case-insensitive read /
/// relaxed encoder / AllowReadingFromString). The internal <c>TolerantEnumConverterFactory</c> is omitted
/// here because none of these payloads contain an enum, so it has zero effect on the bytes measured.
///
/// Serialize is what <c>ToQueuedTask()</c> pays at dispatch; Deserialize is what recovery / monitoring pay.
/// </summary>
[MemoryDiagnoser]
public class PayloadSerializationBenchmark
{
    // OLD: the exact pre-migration Newtonsoft settings.
    private static readonly JsonSerializerSettings Newtonsoft = new() { TypeNameHandling = TypeNameHandling.None };

    // NEW: byte-for-byte the options the current EverTaskJson uses (minus the enum converter — N/A here).
    private static readonly JsonSerializerOptions Stj = new()
    {
        PropertyNamingPolicy        = null,
        PropertyNameCaseInsensitive = true,
        Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString
    };

    // tiny = primitives only (the "keep tasks simple, pass IDs" best practice);
    // blob1k/blob64k = a linear string field; nested = an object graph (the non-linear case).
    [Params("tiny", "blob1k", "blob64k", "nested")]
    public string Payload = "tiny";

    private IEverTask _task = null!;
    private Type _type = null!;
    private string _jsonNewtonsoft = null!;
    private string _jsonStj = null!;

    [GlobalSetup]
    public void Setup()
    {
        _task           = Build(Payload);
        _type           = _task.GetType();
        _jsonNewtonsoft = JsonConvert.SerializeObject(_task, Newtonsoft);
        _jsonStj        = JsonSerializer.Serialize((object?)_task, Stj);
        Console.WriteLine(
            $"[{Payload}] newtonsoft len = {_jsonNewtonsoft.Length:N0}, stj len = {_jsonStj.Length:N0} chars");
    }

    [Benchmark(Baseline = true)]
    public string Serialize_Newtonsoft() => JsonConvert.SerializeObject(_task, Newtonsoft);

    [Benchmark]
    public string Serialize_Stj() => JsonSerializer.Serialize((object?)_task, Stj);

    [Benchmark]
    public object? Deserialize_Newtonsoft() => JsonConvert.DeserializeObject(_jsonNewtonsoft, _type, Newtonsoft);

    [Benchmark]
    public object? Deserialize_Stj() => JsonSerializer.Deserialize(_jsonStj, _type, Stj);

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
