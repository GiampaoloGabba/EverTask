using System.Collections.Concurrent;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace EverTask.Benchmarks;

/// <summary>
/// P-B.1 / F23 — lifecycle MethodInfo resolution on the lazy hot path. Pre-fix the worker did
/// handler.GetType().GetMethod("OnStarted") + "OnCompleted" (+ "OnError") on every execution;
/// post-fix they are resolved once per type and read from a cache. Models the per-op cost.
/// </summary>
[MemoryDiagnoser]
public class LifecycleResolutionBenchmark
{
    private sealed class SampleHandler
    {
        public void OnStarted() { }
        public void OnCompleted() { }
        public void OnError() { }
    }

    private readonly Type _type = typeof(SampleHandler);
    private readonly ConcurrentDictionary<Type, (MethodInfo?, MethodInfo?, MethodInfo?)> _cache = new();

    [GlobalSetup]
    public void Setup() =>
        _cache[_type] = (_type.GetMethod("OnStarted"), _type.GetMethod("OnCompleted"), _type.GetMethod("OnError"));

    [Benchmark(Baseline = true, Description = "GetMethod per call (pre-fix)")]
    public MethodInfo? PerCall()
    {
        var s = _type.GetMethod("OnStarted");
        _ = _type.GetMethod("OnCompleted");
        _ = _type.GetMethod("OnError");
        return s;
    }

    [Benchmark(Description = "Cached lookup (post-fix)")]
    public MethodInfo? Cached()
    {
        _cache.TryGetValue(_type, out var methods);
        return methods.Item1;
    }
}

/// <summary>
/// P-B.2 / L30 — RegisterEvent used to string.Format + box the args array even when the log level was
/// filtered AND there were no subscribers. Post-fix it short-circuits. Models the discarded-event case
/// (level filtered, no subscribers).
/// </summary>
[MemoryDiagnoser]
public class EventFormattingBenchmark
{
    private const string Template = "Task with id {0} was completed in {1} ms.";
    private readonly object[] _args = [Guid.NewGuid(), 12.5d];

    // Simulates "nobody consumes the event": level filtered out and no monitoring subscribers.
    // static readonly (not const) so the unconsumed branch is not folded away as unreachable code.
    private static readonly bool LevelEnabled = false;
    private static readonly bool HasSubscribers = false;

    [Benchmark(Baseline = true, Description = "Always format (pre-fix)")]
    public string AlwaysFormat() => string.Format(Template, _args);

    [Benchmark(Description = "Guarded skip (post-fix)")]
    public string GuardedSkip()
    {
        if (!LevelEnabled && !HasSubscribers)
            return Template; // short-circuit: no format, no boxing of a runtime string
        return string.Format(Template, _args);
    }
}
