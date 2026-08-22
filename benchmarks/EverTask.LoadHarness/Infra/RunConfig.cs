using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Run parameters parsed from the CLI. Only the knobs Tier-0 needs are wired today; the rest of the
/// matrix (--storage, --rate, --payload, --audit, --error-rate, --fullmode, --gc, --affinity …) lands
/// with the scenarios that use it (BENCHMARK_PLAN §2.3).
/// </summary>
public sealed record RunConfig
{
    /// <summary>Tasks per iteration. In-Memory/raw can take 1M; DB scenarios will lower this.</summary>
    public long Count { get; init; } = 1_000_000;

    /// <summary>Consumer/worker count (raw scenarios) — defaults to logical cores.</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Producer/writer count for the raw-channel anchor. EverTask's channel is multi-writer, and real
    /// dispatches come from many threads — a single producer would make itself the bottleneck and
    /// understate the ceiling. Default 4; set 1 for the single-producer reference.
    /// </summary>
    public int Producers { get; init; } = 4;

    /// <summary>Bounded channel capacity for the raw-channel anchor.</summary>
    public int Capacity { get; init; } = 2_000;

    /// <summary>Warmup iterations (discarded): JIT/PGO + thread-pool ramp-up.</summary>
    public int Warmup { get; init; } = 3;

    /// <summary>Measured iterations: throughput mean/stdev, latency accumulated across them.</summary>
    public int Measured { get; init; } = 7;

    /// <summary>Storage backend: inmemory | sqlite | sqlserver | postgres.</summary>
    public string Storage { get; init; } = "inmemory";

    /// <summary>Polling interval (ms) for the naive-polling anchor A3.</summary>
    public int PollIntervalMs { get; init; } = 1_000;

    /// <summary>Default audit level for the engine scenarios (L8/LDP): none | minimal | errorsonly | full.
    /// Full (the engine default) writes a StatusAudit row per transition — the realistic but heavy setting.</summary>
    public string Audit { get; init; } = "full";

    /// <summary>Where the raw JSON report is written.</summary>
    public string OutputDir { get; init; } = "benchmarks/results";

    /// <summary>Task payload body size: none (tiny/primitives, default) | 1k | 64k | &lt;n&gt;[k]. Sizes the
    /// string the engine serializes at Persist — the axis where the Newtonsoft→STJ switch pays off on the
    /// durable path (a tiny task is dominated by the DB layer, a real payload by serialization).</summary>
    public string Payload { get; init; } = "none";

    /// <summary>Minimum level for EverTask's own logging: Trace | Debug | Information | Warning | Error |
    /// Critical | None. Warning is the harness's historical setting — the per-write Info logs stay off, so
    /// numbers captured before this knob existed remain comparable.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogLevel Log { get; init; } = LogLevel.Warning;

    /// <summary>What consumes the log records: none (no provider, historical) | render | enumerate. See
    /// <see cref="NullSinkLoggerProvider"/> — with <c>none</c>, an enabled level costs only what the call
    /// site itself allocates; a sink is what makes formatting and state-reading show up in bytes/task.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogSinkMode Sink { get; init; } = LogSinkMode.None;

    public static RunConfig Parse(IReadOnlyList<string> args, int startIndex)
    {
        var cfg = new RunConfig();
        for (var i = startIndex; i < args.Count - 1; i += 2)
        {
            var key = args[i];
            var val = args[i + 1];
            cfg = key switch
            {
                "--count"       => cfg with { Count = ParseLong(val) },
                "--parallelism" => cfg with { Parallelism = int.Parse(val) },
                "--producers"   => cfg with { Producers = int.Parse(val) },
                "--capacity"    => cfg with { Capacity = int.Parse(val) },
                "--storage"     => cfg with { Storage = val.ToLowerInvariant() },
                "--poll-interval" => cfg with { PollIntervalMs = int.Parse(val) },
                "--audit"       => cfg with { Audit = val.ToLowerInvariant() },
                "--warmup"      => cfg with { Warmup = int.Parse(val) },
                "--measured"    => cfg with { Measured = int.Parse(val) },
                "--out"         => cfg with { OutputDir = val },
                "--payload"     => cfg with { Payload = val.ToLowerInvariant() },
                "--log"         => cfg with { Log = ParseEnum<LogLevel>("--log", val) },
                "--sink"        => cfg with { Sink = ParseEnum<LogSinkMode>("--sink", val) },
                _               => cfg
            };
        }
        return cfg;
    }

    /// <summary>The payload body as a string the engine serializes, or null for the tiny/primitive task.
    /// "1k"/"64k" use binary KB (1024) to match the serialization micro's <c>new string('x', 1024)</c>.</summary>
    public string? BuildPayload()
    {
        var spec = Payload.Trim();
        if (spec is "" or "none" or "tiny" or "0") return null;
        var suffix = char.ToLowerInvariant(spec[^1]);
        var chars = suffix == 'k' ? int.Parse(spec[..^1]) * 1024 : int.Parse(spec);
        return chars <= 0 ? null : new string('x', chars);
    }

    // Reject a typo loudly: silently falling back to the default would mislabel an A/B result.
    private static T ParseEnum<T>(string option, string raw) where T : struct, Enum =>
        Enum.TryParse<T>(raw, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ArgumentException($"Unknown value '{raw}' for {option}. Expected: {string.Join(" | ", Enum.GetNames<T>())}.");

    // Accept human-friendly counts: 1_000_000, 1000000, 1m, 100k.
    private static long ParseLong(string raw)
    {
        raw = raw.Replace("_", "").Trim();
        if (raw.Length == 0) return 0;
        var suffix = char.ToLowerInvariant(raw[^1]);
        if (suffix is 'k' or 'm')
        {
            var b = long.Parse(raw[..^1]);
            return suffix == 'k' ? b * 1_000 : b * 1_000_000;
        }
        return long.Parse(raw);
    }
}
