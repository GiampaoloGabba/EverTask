using System.Collections.ObjectModel;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// When a row's execution actually began — the term every "how late did it start" question needs, and the one
/// no single column holds.
/// </summary>
/// <remarks>
/// <see cref="QueuedTask.LastExecutionUtc"/> is not it: storage writes that column only on TERMINAL
/// transitions, so it stamps the moment a run FINISHED. Reading it as a start makes a punctual occurrence with
/// a three-minute handler look three minutes late, which is the whole execution time reported as tardiness.
/// </remarks>
internal static class TaskRunTiming
{
    /// <summary>
    /// The recorded start of the last run of each of <paramref name="rows"/>, asked of the storage in one
    /// query. Rows the store has no recorded start for are absent, and so are the ones whose current state is
    /// not about a run that has begun.
    /// </summary>
    /// <remarks>
    /// The audit trail is READ, never taken off <see cref="QueuedTask.StatusAudits"/>: no storage read
    /// populates that navigation, so the relational providers hand back rows with an empty collection and the
    /// same row would answer differently depending on the backend behind it.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> RecordedStartsAsync(
        ITaskStorage storage, IReadOnlyCollection<QueuedTask> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => HasBegunTheRunItStandsFor(r.Status)).Select(r => r.Id).ToArray();

        return ids.Length == 0
                   ? ReadOnlyDictionary<Guid, DateTimeOffset>.Empty
                   : await storage.GetLastRunStarts(ids, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The instant the row's last (or current) run began, or null when nothing that ran can answer for it.
    /// </summary>
    /// <remarks>
    /// Two sources, in order. The row's own <see cref="QueuedTaskStatus.InProgress"/> transition, as
    /// <paramref name="recordedStarts"/> carries it, is the recorded start and the only one that can answer
    /// for a run still in flight. Otherwise the start is derived from the two columns every row has, the end
    /// of the run and the duration <see cref="MeasuredDurationMs"/> — and only <see cref="MeasuredDurationMs"/>
    /// — accepts as measured around it: a duration nobody measured reads as 0, and subtracting that returns
    /// the moment the run ended as the moment it began, which is the whole execution reported as tardiness.
    /// </remarks>
    public static DateTimeOffset? StartOfLastRun(QueuedTask row,
                                                 IReadOnlyDictionary<Guid, DateTimeOffset> recordedStarts)
    {
        if (!HasBegunTheRunItStandsFor(row.Status))
            return null;

        if (recordedStarts.TryGetValue(row.Id, out var recorded))
            return recorded;

        return MeasuredDurationMs(row) is { } measured && row.LastExecutionUtc is { } finished
                   ? finished.AddMilliseconds(-measured)
                   : null;
    }

    /// <summary>
    /// How long a run of this row took, as a completion measured it — and nothing at all for a row no
    /// completion measured.
    /// </summary>
    /// <remarks>
    /// <see cref="QueuedTask.ExecutionTimeMs"/> is written together with the end of the run, by a completion
    /// and only by a completion. Every other write leaves it at 0 while stamping the end: a failure
    /// (<c>SetStatus</c> with no duration), and a series finalized WITHOUT a run — the last materialization of
    /// a durable schedule, and a recurring series whose remaining slots recovery found past their bound, both
    /// of which stamp the finalization instant as the end of a run no handler ever performed. 0 is therefore
    /// "unmeasured", not "instant", and it is the one value this refuses.
    /// </remarks>
    public static double? MeasuredDurationMs(QueuedTask row) =>
        row is { Status: QueuedTaskStatus.Completed, ExecutionTimeMs: > 0 } ? row.ExecutionTimeMs : null;

    /// <summary>
    /// The mean of the durations that were really measured across <paramref name="rows"/>, or 0 when none of
    /// them was.
    /// </summary>
    /// <remarks>
    /// The one average of execution time in the API: the overview, the queue metrics and the queue
    /// configuration all answer from the measured column, so the same set of rows cannot produce three
    /// different means. Deriving it from the audit trail instead answered 0 on every relational store, where
    /// no read populates <see cref="QueuedTask.StatusAudits"/>, and nothing at all below
    /// <see cref="EverTask.Abstractions.AuditLevel.Full"/>, which records no transition to subtract.
    /// </remarks>
    public static double AverageMeasuredDurationMs(IEnumerable<QueuedTask> rows)
    {
        var total = 0d;
        var count = 0;

        foreach (var row in rows)
        {
            if (MeasuredDurationMs(row) is not { } measured)
                continue;

            total += measured;
            count++;
        }

        return count == 0 ? 0d : total / count;
    }

    /// <summary>
    /// True while the row's CURRENT state is about a run that has already begun.
    /// </summary>
    /// <remarks>
    /// A row waiting for its delivery — queued, parked, or stopped by a host that will hand it back to a
    /// queue — stands for a run that has not started, and the start of an EARLIER run is not that run's.
    /// Reporting it made a recurring row waiting for its next slot, and an occurrence requeued after a
    /// failure, answer with the previous attempt's start.
    /// </remarks>
    private static bool HasBegunTheRunItStandsFor(QueuedTaskStatus status) =>
        status is QueuedTaskStatus.InProgress
            or QueuedTaskStatus.Completed
            or QueuedTaskStatus.Failed
            or QueuedTaskStatus.Cancelled;
}
