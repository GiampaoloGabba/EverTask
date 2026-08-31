namespace EverTask.Scheduler.Occurrences;

internal static class RecurringSeriesFinalizer
{
    internal static async Task<bool> FinalizeAsync(
        ITaskStorage storage,
        Guid taskId,
        DateTimeOffset? expectedCursorUtc,
        QueuedTaskStatus expectedStatus,
        int expectedScheduleVersion,
        double executionTimeMs,
        AuditLevel auditLevel,
        RecurringSeriesFinalizationPolicy policy,
        CancellationToken ct = default)
    {
        var effectivePolicy = policy;

        if (effectivePolicy == RecurringSeriesFinalizationPolicy.Recovery)
        {
            if (storage.SupportsScheduleVersioning)
                effectivePolicy = RecurringSeriesFinalizationPolicy.Conditional;
            else if (!storage.SupportsDurableOccurrences)
                effectivePolicy = RecurringSeriesFinalizationPolicy.Unconditional;
            else
                return false;
        }

        if (effectivePolicy == RecurringSeriesFinalizationPolicy.Conditional)
        {
            return await storage
                         .TrySetRecurringSeriesCompleted(taskId, expectedCursorUtc, expectedStatus,
                             expectedScheduleVersion, executionTimeMs, auditLevel, ct)
                         .ConfigureAwait(false);
        }

        await storage.SetRecurringSeriesCompleted(taskId, executionTimeMs, auditLevel).ConfigureAwait(false);
        return true;
    }
}

internal enum RecurringSeriesFinalizationPolicy
{
    Unconditional,
    Conditional,
    Recovery
}
