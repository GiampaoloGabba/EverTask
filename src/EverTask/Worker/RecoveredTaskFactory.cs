using EverTask.Dispatcher;
using EverTask.Scheduler.Occurrences;

namespace EverTask.Worker;

/// <summary>
/// Everything a persisted row carries, rebuilt once: the deserialized payload, the validated schedule and the
/// per-row metadata that must survive a re-dispatch.
/// </summary>
/// <remarks>
/// The two error fields are kept apart on purpose, because they lead to opposite outcomes: a payload that will
/// not deserialize in THIS build may heal (a serializer fix, a redeploy) and gets a bounded retry, while a
/// schedule that no longer deserializes can never run as a schedule again and is poisoned terminally.
/// </remarks>
internal readonly record struct RecoveredTask(
    IEverTask? Task,
    RecurringTask? Recurring,
    AuditLevel AuditLevel,
    DateTimeOffset? ExecutionTime,
    Guid? ParentTaskId,
    string? RuntimeInfo,
    OccurrenceRuntimeInfo? Occurrence,
    int ScheduleVersion,
    int? CurrentRunCount,
    string? TaskKey,
    string? QueueName,
    QueuedTaskStatus Status,
    bool TypeWasLoadable,
    Exception? PayloadError,
    Exception? ScheduleError)
{
    /// <summary>
    /// True when the row is a DURABLE schedule: it owns a definition and a cursor but runs no handler.
    /// The recovery defers these to a second pass, after every ordinary row (and every occurrence) is back.
    /// </summary>
    public bool IsDurableSchedule => Recurring?.OccurrenceMode == OccurrenceMode.Durable;

    /// <summary>
    /// The slice of the row the re-dispatch must work from rather than re-derive — or read back.
    /// </summary>
    public DispatchRowMetadata RowMetadata =>
        new(ParentTaskId, RuntimeInfo, ScheduleVersion, QueueName, Status,
            Occurrence?.RunNumber, Occurrence?.SlotUtc);
}

/// <summary>
/// Rebuilds a <see cref="RecoveredTask"/> from a persisted row. The single place that knows how a row maps
/// back to an executable task, instead of the metadata being re-derived argument by argument at each call.
/// </summary>
internal static class RecoveredTaskFactory
{
    /// <param name="row">The persisted row.</param>
    /// <param name="validationContext">
    /// The registered occurrence providers and exclusion calendars, when the caller can reach them. A row
    /// naming a definition this build no longer registers is then corrupt schedule metadata like an
    /// unparseable cron, and takes the same terminal poison route instead of failing at every next-run.
    /// </param>
    public static RecoveredTask FromRow(QueuedTask row, ScheduleValidationContext? validationContext = null)
    {
        IEverTask? task            = null;
        var        typeWasLoadable = false;
        Exception? payloadError    = null;

        // "The type itself cannot be loaded" (assembly/type gone — genuine corruption, can never run) is a
        // different verdict from "the type is loadable but its persisted payload did not deserialize in this
        // build" (an unrecognized format that may heal), so the two are reported separately.
        try
        {
            var type = Type.GetType(row.Type);
            if (type != null && typeof(IEverTask).IsAssignableFrom(type))
            {
                typeWasLoadable = true;
                task            = (IEverTask?)EverTaskJson.Deserialize(row.Request, type);
            }
        }
        catch (Exception e)
        {
            payloadError = e;
        }

        RecurringTask? recurring     = null;
        Exception?     scheduleError = null;
        try
        {
            if (!string.IsNullOrEmpty(row.RecurringTask))
            {
                recurring = EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask);

                // B2: a schedule that DESERIALIZES but is corrupt (an unparseable cron, an out-of-range
                // OnDays/OnHours/OnMonths, a negative Interval) must be treated like un-deserializable
                // metadata — validated HERE so the caller's poison guard sees it, instead of throwing later at
                // next-run (a bounded per-restart failure) or scheduling a wrong/never-firing occurrence.
                recurring?.Validate(validationContext);
            }
        }
        catch (Exception e)
        {
            scheduleError = e;
            recurring     = null; // ensure the caller's "recurring row with no schedule" poison guard fires
        }

        return new RecoveredTask(
            task,
            recurring,
            // A null persisted level means Full, for rows written before per-task audit levels existed.
            row.AuditLevel.HasValue ? (AuditLevel)row.AuditLevel.Value : AuditLevel.Full,
            // Recurring rows resume from their cursor; everything else from its scheduled time.
            row.NextRunUtc ?? row.ScheduledExecutionUtc,
            row.ParentTaskId,
            row.RuntimeInfo,
            // Read ONCE, here, where a row becomes a task: the occurrence's durable slot and run number are
            // facts of the row that no column can restate (its own run counter belongs to the one-shot, not
            // to the series). A schedule row's runtime state lives in the same column and yields nothing.
            row.ParentTaskId != null ? OccurrenceRuntimeInfo.TryParse(row.RuntimeInfo) : null,
            row.ScheduleVersion,
            row.CurrentRunCount,
            row.TaskKey,
            row.QueueName,
            row.Status,
            typeWasLoadable,
            payloadError,
            scheduleError);
    }
}
