using System.Text.Json;
using BenchmarkDotNet.Attributes;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using Newtonsoft.Json;
using Stj = System.Text.Json.JsonSerializer;

namespace EverTask.Benchmarks;

/// <summary>
/// Newtonsoft.Json (current) vs System.Text.Json (candidate) for EverTask's serialization hot path:
/// the task payload + the RecurringTask metadata that <c>EverTaskJson</c> (de)serializes on every
/// dispatch and on every recovery. Quantifies the per-op time AND allocations gain of dropping
/// Newtonsoft — input to <c>review/newtonsoft-removal-feasibility.md</c>.
///
/// Settings mirror production: Newtonsoft with TypeNameHandling.None; STJ with no naming policy +
/// case-insensitive read (the recommended parity options).
///
/// Run: dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *Serialization*
/// </summary>
[MemoryDiagnoser]
public class SerializationBenchmark
{
    public enum Shape { SmallPayload, LargePayload, RecurringCron }

    [Params(Shape.SmallPayload, Shape.LargePayload, Shape.RecurringCron)]
    public Shape Kind { get; set; }

    private static readonly JsonSerializerSettings NewtonsoftSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None
    };

    private static readonly JsonSerializerOptions StjOptions = new()
    {
        PropertyNamingPolicy        = null,
        PropertyNameCaseInsensitive = true
    };

    private object _payload = null!;
    private Type   _type    = null!;
    private string _newtonsoftJson = "";
    private string _stjJson        = "";

    [GlobalSetup]
    public void Setup()
    {
        _payload = Kind switch
        {
            Shape.SmallPayload  => new SmallPayload(Guid.NewGuid(), 42, "order-create"),
            Shape.LargePayload  => LargePayload.Create(),
            Shape.RecurringCron => new RecurringTask
            {
                CronInterval = new CronInterval("0 */6 * * *"),
                MaxRuns      = 100,
                RunUntil     = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            _ => throw new ArgumentOutOfRangeException()
        };
        _type           = _payload.GetType();
        _newtonsoftJson = JsonConvert.SerializeObject(_payload, NewtonsoftSettings);
        _stjJson        = Stj.Serialize(_payload, _type, StjOptions);
    }

    [Benchmark(Baseline = true, Description = "Newtonsoft serialize")]
    public string Newtonsoft_Serialize() => JsonConvert.SerializeObject(_payload, NewtonsoftSettings);

    [Benchmark(Description = "STJ serialize")]
    public string Stj_Serialize() => Stj.Serialize(_payload, _type, StjOptions);

    [Benchmark(Description = "Newtonsoft deserialize")]
    public object? Newtonsoft_Deserialize() => JsonConvert.DeserializeObject(_newtonsoftJson, _type, NewtonsoftSettings);

    [Benchmark(Description = "STJ deserialize")]
    public object? Stj_Deserialize() => Stj.Deserialize(_stjJson, _type, StjOptions);
}

public record SmallPayload(Guid Id, int Amount, string Operation);

public record LargePayload(
    Guid Id,
    string Title,
    string Description,
    DateTimeOffset CreatedAt,
    decimal Total,
    List<int> LineItemIds,
    string[] Tags,
    Dictionary<string, string> Headers)
{
    public static LargePayload Create() => new(
        Guid.NewGuid(),
        "A reasonably sized task title",
        new string('x', 512),
        DateTimeOffset.UtcNow,
        12345.67m,
        Enumerable.Range(1, 50).ToList(),
        Enumerable.Range(1, 20).Select(i => $"tag-{i}").ToArray(),
        Enumerable.Range(1, 15).ToDictionary(i => $"h{i}", i => $"value-{i}"));
}
