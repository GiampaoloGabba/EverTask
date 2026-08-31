using System.Collections.ObjectModel;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// When a row's execution actually began — the term every "how late did it start" question needs, and the one
/// no single column holds. <see cref="QueuedTask.LastExecutionUtc"/> is not it: storage writes that column
/// only on TERMINAL transitions, so it stamps the moment a run FINISHED.
/// </summary>
internal static class TaskRunTiming
{
    /// <summary>
    /// The recorded start of the last run of each of <paramref name="rows"/>, asked of the storage in one
    /// query. Rows the store has no recorded start for are absent, and so are the ones whose current state is
    /// not about a run that has begun.
    /// </summary>
    /// <remarks>
    /// The audit trail is READ, never taken off <see cref="QueuedTask.StatusAudits"/>: no storage read
    /// populates that navigation, so the same row would answer differently per backend.
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
    /// The row's own <see cref="QueuedTaskStatus.InProgress"/> transition first, the only source that can
    /// answer for a run still in flight; otherwise the end of the run minus a duration
    /// <see cref="MeasuredDurationMs"/> accepts as measured — an unmeasured one reads as 0, and subtracting
    /// that would return the end of the run as its start.
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
    /// <see cref="QueuedTask.ExecutionTimeMs"/> is written together with the end of the run by a completion and
    /// only by a completion; a failure and a series finalized without a run stamp the end and leave the
    /// duration at 0. 0 is therefore "unmeasured", never "instant", and it is the one value this refuses.
    /// </remarks>
    public static double? MeasuredDurationMs(QueuedTask row) =>
        row is { Status: QueuedTaskStatus.Completed, ExecutionTimeMs: > 0 } ? row.ExecutionTimeMs : null;

    /// <summary>
    /// The mean of the durations that were really measured across <paramref name="rows"/>, or 0 when none of
    /// them was.
    /// </summary>
    /// <remarks>
    /// The ONE average of execution time in the API — overview, queue metrics and queue configuration all
    /// answer from here, so the same rows cannot produce three different means. The audit trail is not an
    /// alternative source: it is empty on the relational stores and below
    /// <see cref="EverTask.Abstractions.AuditLevel.Full"/>.
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
    /// queue — stands for a run that has not started, and an EARLIER run's start is not that run's.
    /// </remarks>
    private static bool HasBegunTheRunItStandsFor(QueuedTaskStatus status) =>
        status is QueuedTaskStatus.InProgress
            or QueuedTaskStatus.Completed
            or QueuedTaskStatus.Failed
            or QueuedTaskStatus.Cancelled;
}
