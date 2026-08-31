using BenchmarkDotNet.Running;
using EverTask.Benchmarks;

// Console host for the EverTask performance benchmarks.
// Not a CI gate: the deterministic xUnit gates own pass/fail; these benchmarks only quantify the
// magnitude of each optimisation (allocated-bytes/op, Gen0, threading) for benchmarks/RESULTS.md.
//
// Usage:
//   dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *
//   dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *Recurring*
//
// The storage micros (*PersistInsert*, *ProcRawAdo*) need SqlServer + Postgres. This host starts the two
// containers ONCE and hands the connection strings to BenchmarkDotNet's child processes, which would
// otherwise each start their own. Set EVERTASK_BENCH_PROVIDERS=sqlite to run their Docker-free cases only.
if (StorageBenchEnvironment.NeedsDocker(args))
{
    await using var containers = await StorageBenchEnvironment.ProvisionAsync();
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

public partial class Program;
