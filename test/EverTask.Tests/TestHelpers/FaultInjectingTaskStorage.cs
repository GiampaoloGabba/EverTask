using System.Collections.Concurrent;
using System.Linq.Expressions;
using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// A REAL storage with a fault valve in front of it: every call is forwarded to <paramref name="inner"/>
/// unchanged unless a fault has been armed for that operation, in which case it throws BEFORE the inner
/// call and the store is left exactly as it was.
/// </summary>
/// <remarks>
/// This is deliberately not a mock. A mock replaces the operation with a canned answer and hides whatever
/// the real implementation would have done — which is precisely the behaviour these tests are about (did
/// the transaction really roll back? did the counter really move?). A decorator keeps the real store in the
/// loop and only chooses WHEN it is reached.
/// <para>
/// Every default interface member is forwarded explicitly. Leaving one out would silently run the
/// interface's own default against this wrapper (a <c>NotSupportedException</c> for the atomic operations,
/// an unindexed scan for the reads) instead of the inner store's real implementation.
/// </para>
/// </remarks>
public sealed class FaultInjectingTaskStorage(ITaskStorage inner) : ITaskStorage
{
    private readonly ConcurrentDictionary<string, Func<Exception?>> _faults =
        new(StringComparer.Ordinal);

    /// <summary>Number of times each operation was reached, whether or not it threw.</summary>
    public ConcurrentDictionary<string, int> Calls { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Makes <paramref name="operation"/> throw on its next <paramref name="times"/> calls.</summary>
    public void FailNext(string operation, int times, Func<Exception>? error = null)
    {
        var remaining = times;
        _faults[operation] = () => Interlocked.Decrement(ref remaining) >= 0
                                       ? error?.Invoke() ?? new InvalidOperationException($"injected {operation} fault")
                                       : null;
    }

    /// <summary>Makes <paramref name="operation"/> throw on every call until <see cref="Heal"/>.</summary>
    public void FailAlways(string operation, Func<Exception>? error = null) =>
        _faults[operation] = () => error?.Invoke() ?? new InvalidOperationException($"injected {operation} fault");

    /// <summary>Removes the armed fault, so the operation reaches the real store again.</summary>
    public void Heal(string operation) => _faults.TryRemove(operation, out _);

    private void Gate(string operation)
    {
        Calls.AddOrUpdate(operation, 1, static (_, count) => count + 1);

        if (_faults.TryGetValue(operation, out var fault) && fault() is { } error)
            throw error;
    }

    public bool SupportsDurableOccurrences => inner.SupportsDurableOccurrences;
    public bool SupportsScheduleVersioning => inner.SupportsScheduleVersioning;

    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        Gate(nameof(Get));
        return inner.Get(where, ct);
    }

    public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        Gate(nameof(GetAll));
        return inner.GetAll(ct);
    }

    public Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        Gate(nameof(Persist));
        return inner.Persist(executor, ct);
    }

    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                              CancellationToken ct = default)
    {
        Gate(nameof(RetrievePending));
        return inner.RetrievePending(lastCreatedAt, lastId, take, ct);
    }

    public Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt, Guid? lastId,
                                              int take, CancellationToken ct = default)
    {
        Gate(nameof(RetrievePending));
        return inner.RetrievePending(nowUtc, lastCreatedAt, lastId, take, ct);
    }

    public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(SetQueued));
        return inner.SetQueued(taskId, auditLevel, ct);
    }

    public Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(TrySetQueuedIfRecoverable));
        return inner.TrySetQueuedIfRecoverable(taskId, auditLevel, ct);
    }

    public Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                                CancellationToken ct = default)
    {
        Gate(nameof(TrySetQueuedIfRecoverable));
        return inner.TrySetQueuedIfRecoverable(nowUtc, taskId, auditLevel, ct);
    }

    public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(SetInProgress));
        return inner.SetInProgress(taskId, auditLevel, ct);
    }

    public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        Gate(nameof(SetCompleted));
        return inner.SetCompleted(taskId, executionTimeMs, auditLevel);
    }

    public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel)
    {
        Gate(nameof(SetCancelledByUser));
        return inner.SetCancelledByUser(taskId, auditLevel);
    }

    public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel)
    {
        Gate(nameof(SetCancelledByService));
        return inner.SetCancelledByService(taskId, exception, auditLevel);
    }

    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                          double? executionTimeMs = null, CancellationToken ct = default)
    {
        Gate(nameof(SetStatus));
        return inner.SetStatus(taskId, status, exception, auditLevel, executionTimeMs, ct);
    }

    public Task<int> GetCurrentRunCount(Guid taskId)
    {
        Gate(nameof(GetCurrentRunCount));
        return inner.GetCurrentRunCount(taskId);
    }

    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel)
    {
        Gate(nameof(UpdateCurrentRun));
        return inner.UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel);
    }

    public Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel, int expectedScheduleVersion)
    {
        Gate(nameof(UpdateCurrentRun));
        return inner.UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel, expectedScheduleVersion);
    }

    public Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                     AuditLevel auditLevel)
    {
        Gate(nameof(CompleteRecurringRun));
        return inner.CompleteRecurringRun(taskId, executionTimeMs, nextRun, auditLevel);
    }

    public Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                        AuditLevel auditLevel, int expectedScheduleVersion)
    {
        Gate(nameof(CompleteRecurringRun));
        return inner.CompleteRecurringRun(taskId, executionTimeMs, nextRun, auditLevel, expectedScheduleVersion);
    }

    public Task SetRecurringSeriesCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        Gate(nameof(SetRecurringSeriesCompleted));
        return inner.SetRecurringSeriesCompleted(taskId, executionTimeMs, auditLevel);
    }

    public Task<bool> TrySetRecurringSeriesCompleted(Guid taskId, DateTimeOffset? expectedCursorUtc,
                                                     QueuedTaskStatus expectedStatus, int expectedScheduleVersion,
                                                     double executionTimeMs, AuditLevel auditLevel,
                                                     CancellationToken ct = default)
    {
        Gate(nameof(TrySetRecurringSeriesCompleted));
        return inner.TrySetRecurringSeriesCompleted(taskId, expectedCursorUtc, expectedStatus,
            expectedScheduleVersion, executionTimeMs, auditLevel, ct);
    }

    public Task SetRecurringTaskPoisoned(Guid taskId, Exception exception, AuditLevel auditLevel,
                                         CancellationToken ct = default)
    {
        Gate(nameof(SetRecurringTaskPoisoned));
        return inner.SetRecurringTaskPoisoned(taskId, exception, auditLevel, ct);
    }

    public Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        Gate(nameof(IncrementRecoveryFailure));
        return inner.IncrementRecoveryFailure(taskId, ct);
    }

    public Task ClearRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        Gate(nameof(ClearRecoveryFailure));
        return inner.ClearRecoveryFailure(taskId, ct);
    }

    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        Gate(nameof(GetByTaskKey));
        return inner.GetByTaskKey(taskKey, ct);
    }

    public Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        Gate(nameof(UpdateTask));
        return inner.UpdateTask(task, ct);
    }

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        Gate(nameof(Remove));
        return inner.Remove(taskId, ct);
    }

    public Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(MaterializeOccurrence));
        return inner.MaterializeOccurrence(parentId, expectedScheduleVersion, expectedCursorUtc, occurrence,
            newCursorUtc, auditLevel, ct);
    }

    public Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(CancelSchedule));
        return inner.CancelSchedule(parentId, auditLevel, ct);
    }

    public Task<bool> RequeueTerminal(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        Gate(nameof(RequeueTerminal));
        return inner.RequeueTerminal(taskId, auditLevel, ct);
    }

    public Task<bool> TryRequeueStaleOccurrence(Guid childId, QueuedTaskStatus expectedStatus, AuditLevel auditLevel,
                                                CancellationToken ct = default)
    {
        Gate(nameof(TryRequeueStaleOccurrence));
        return inner.TryRequeueStaleOccurrence(childId, expectedStatus, auditLevel, ct);
    }

    public Task<bool> UpdateSchedule(Guid taskId, int expectedScheduleVersion, string recurringTaskJson,
                                     string? recurringInfo, DateTimeOffset? nextRunUtc, int? maxRuns,
                                     DateTimeOffset? runUntil, string? runtimeInfo, CancellationToken ct = default)
    {
        Gate(nameof(UpdateSchedule));
        return inner.UpdateSchedule(taskId, expectedScheduleVersion, recurringTaskJson, recurringInfo, nextRunUtc,
            maxRuns, runUntil, runtimeInfo, ct);
    }

    public Task<bool> TryHaltSchedule(Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc,
                                      QueuedTaskStatus expectedStatus, string runtimeInfo,
                                      CancellationToken ct = default)
    {
        Gate(nameof(TryHaltSchedule));
        return inner.TryHaltSchedule(parentId, expectedScheduleVersion, expectedCursorUtc, expectedStatus,
            runtimeInfo, ct);
    }

    public Task<QueuedTask[]> GetOccurrences(Guid parentId, bool nonTerminalOnly = false,
                                             CancellationToken ct = default)
    {
        Gate(nameof(GetOccurrences));
        return inner.GetOccurrences(parentId, nonTerminalOnly, ct);
    }

    public Task<int> CountActiveOccurrences(Guid parentId, CancellationToken ct = default)
    {
        Gate(nameof(CountActiveOccurrences));
        return inner.CountActiveOccurrences(parentId, ct);
    }

    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                       CancellationToken cancellationToken)
    {
        Gate(nameof(SaveExecutionLogsAsync));
        return inner.SaveExecutionLogsAsync(taskId, logs, cancellationToken);
    }

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId,
                                                                      CancellationToken cancellationToken)
    {
        Gate(nameof(GetExecutionLogsAsync));
        return inner.GetExecutionLogsAsync(taskId, cancellationToken);
    }

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take,
                                                                      CancellationToken cancellationToken)
    {
        Gate(nameof(GetExecutionLogsAsync));
        return inner.GetExecutionLogsAsync(taskId, skip, take, cancellationToken);
    }
}
