using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EverTask")]
[assembly: InternalsVisibleTo("EverTask.Storage.EfCore")]

// The test assembly sees the internals of the core already; it needs this one too because the backoff an
// OccurrenceProviderException carries is internal — a number the library acts on, not a promise to a caller.
[assembly: InternalsVisibleTo("EverTask.Tests")]

namespace EverTask.Abstractions;

// Task.Delay (and every .NET timer, CancellationTokenSource.CancelAfter included) rejects delays above
// uint.MaxValue - 1 milliseconds (~49.7 days) with an ArgumentOutOfRangeException. The retry policies
// validate or clamp against this bound so the failure cannot surface mid-Execute, WorkerExecutor clamps
// the per-task timeout and AuditCleanupHostedService its intervals (hence the InternalsVisibleTo above),
// and the ET0009 analyzer flags over-limit literals at compile time.
internal static class TaskDelayLimit
{
    internal static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
}
