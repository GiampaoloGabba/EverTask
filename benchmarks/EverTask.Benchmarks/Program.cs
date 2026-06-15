using BenchmarkDotNet.Running;

// Console host for the EverTask performance benchmarks.
// Not a CI gate: the deterministic xUnit gates own pass/fail; these benchmarks only quantify the
// magnitude of each optimisation (allocated-bytes/op, Gen0, threading) for benchmarks/RESULTS.md.
//
// Usage:
//   dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *
//   dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *Recurring*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program;
