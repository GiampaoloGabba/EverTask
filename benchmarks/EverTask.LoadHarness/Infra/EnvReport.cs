using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Captures the bits of the environment that change benchmark numbers and must be declared with every
/// result (BENCHMARK_PLAN §2.6/§7): GC mode, timer resolution, core count, tiering. Printed at start
/// and embedded in the JSON report.
/// </summary>
public sealed record EnvInfo(
    string Runtime,
    string Os,
    int LogicalCores,
    bool ServerGc,
    bool ConcurrentGc,
    string GcLatencyMode,
    bool HighResTimer,
    long StopwatchFrequency,
    string? TieredCompilation,
    string? TieredPgo,
    string ProcessAffinity)
{
    public static EnvInfo Capture()
    {
        string affinity = "n/a";
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            try { affinity = Process.GetCurrentProcess().ProcessorAffinity.ToString("X"); }
            catch { affinity = "n/a"; }
        }

        return new EnvInfo(
            Runtime: RuntimeInformation.FrameworkDescription,
            Os: RuntimeInformation.OSDescription,
            LogicalCores: Environment.ProcessorCount,
            ServerGc: GCSettings.IsServerGC,
            ConcurrentGc: GCSettings.LatencyMode != GCLatencyMode.Batch,
            GcLatencyMode: GCSettings.LatencyMode.ToString(),
            HighResTimer: Stopwatch.IsHighResolution,
            StopwatchFrequency: Stopwatch.Frequency,
            TieredCompilation: Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            TieredPgo: Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            ProcessAffinity: affinity);
    }

    public void Print()
    {
        Console.WriteLine("Environment");
        Console.WriteLine($"  Runtime         : {Runtime}");
        Console.WriteLine($"  OS              : {Os}");
        Console.WriteLine($"  Logical cores   : {LogicalCores}");
        Console.WriteLine($"  GC              : {(ServerGc ? "Server" : "Workstation")}, latency={GcLatencyMode}");
        Console.WriteLine($"  Timer           : highRes={HighResTimer}, freq={StopwatchFrequency:N0} Hz");
        Console.WriteLine($"  TieredCompilation: {TieredCompilation ?? "(default/on)"}  TieredPGO: {TieredPgo ?? "(default/on)"}");
        Console.WriteLine($"  ProcessAffinity : 0x{ProcessAffinity}");
        Console.WriteLine();
    }
}
