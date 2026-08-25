namespace EverTask.Scheduler.Occurrences;

// EventId range 1800–1899 (OccurrenceMaterializer). Ranges are allocated per component in the #32 plan;
// keep every new log line inside this range and never reuse an id.
//
// Unlike WorkerExecutor's, these are ordinary generator-guarded methods called directly: the materializer
// runs outside a delivery, so it logs through its own category and publishes the monitoring counterpart as a
// separate, already-rendered message. The one rule that still holds is the reason RegisterEvent exists —
// never hand a rendered sentence to a logger as if it were a template.
internal static partial class OccurrenceMaterializerLog
{
    [LoggerMessage(EventId = 1800, Level = LogLevel.Debug,
        Message = "Materialization of schedule {ParentId} skipped: {Reason}")]
    public static partial void MaterializationSkipped(this ILogger logger, Guid parentId, string reason);

    [LoggerMessage(EventId = 1801, Level = LogLevel.Debug,
        Message = "Materialized occurrence {OccurrenceId} of schedule {ParentId} for slot {SlotUtc:O} (run {RunNumber})")]
    public static partial void OccurrenceMaterialized(this ILogger logger, Guid occurrenceId, Guid parentId,
                                                      DateTimeOffset slotUtc, int runNumber);

    // The three losses below are three different mistakes with three different fixes — a window too narrow for
    // the outage, a per-episode cap that kept only the newest slots, a skip policy doing what it says — so each
    // has its own id and its own sentence. One line for all three said "outside the misfire window" over an
    // overflow that no window had touched.
    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning,
        Message = "Schedule {ParentId} dropped {SkippedCount} due slot(s) (exact count: {IsExact}) from " +
                  "{FromUtc:O}: outside the misfire window")]
    public static partial void OccurrenceSkipped(this ILogger logger, Guid parentId, int skippedCount, bool isExact,
                                                 DateTimeOffset fromUtc);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Error,
        Message = "Catch-up of schedule {ParentId} halted at cursor {CursorUtc:O}: {DetectedAtLeast} slots are " +
                  "due (exact count: {IsExact}), more than the configured cap. Nothing is materialized until " +
                  "the schedule is resumed or rescheduled")]
    public static partial void CatchUpHalted(this ILogger logger, Guid parentId, DateTimeOffset? cursorUtc,
                                             int detectedAtLeast, bool isExact);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Warning,
        Message = "Occurrence {OccurrenceId} of schedule {ParentId} was stranded in {Status} with no live " +
                  "delivery behind it and has been requeued")]
    public static partial void StaleOccurrenceDetected(this ILogger logger, Guid occurrenceId, Guid parentId,
                                                       QueuedTaskStatus status);

    [LoggerMessage(EventId = 1805, Level = LogLevel.Warning,
        Message = "The registered scheduler cannot report whether a task is parked, so stranded occurrences " +
                  "cannot be detected: every non-terminal occurrence counts against the concurrency budget " +
                  "until it terminates")]
    public static partial void ScheduleInspectionUnsupported(this ILogger logger);

    [LoggerMessage(EventId = 1806, Level = LogLevel.Debug,
        Message = "Materialization of schedule {ParentId} lost a race ({Outcome}): the schedule is re-read on " +
                  "the next run")]
    public static partial void MaterializationLostRace(this ILogger logger, Guid parentId,
                                                       OccurrenceMaterializationOutcome outcome);

    [LoggerMessage(EventId = 1807, Level = LogLevel.Error,
        Message = "Materialization of schedule {ParentId} failed")]
    public static partial void MaterializationFailed(this ILogger logger, Exception exception, Guid parentId);

    [LoggerMessage(EventId = 1808, Level = LogLevel.Debug,
        Message = "Schedule {ParentId} re-parked at {NextRunUtc:O}")]
    public static partial void ScheduleReparked(this ILogger logger, Guid parentId, DateTimeOffset nextRunUtc);

    [LoggerMessage(EventId = 1809, Level = LogLevel.Information,
        Message = "Durable schedule {ParentId} completed: its grid has no occurrence left inside its bounds")]
    public static partial void DurableSeriesCompleted(this ILogger logger, Guid parentId);

    [LoggerMessage(EventId = 1810, Level = LogLevel.Error,
        Message = "Occurrence {OccurrenceId} of schedule {ParentId} cannot be rebuilt from its row and has been " +
                  "marked Failed: nothing in this build can deliver it, and left non-terminal it would hold a " +
                  "slot of the schedule's concurrency budget for ever")]
    public static partial void OccurrenceRowUnusable(this ILogger logger, Exception exception, Guid occurrenceId,
                                                     Guid parentId);

    [LoggerMessage(EventId = 1811, Level = LogLevel.Debug,
        Message = "Cursor advance of schedule {ParentId} lost its compare-and-swap: the schedule is re-read on " +
                  "the next run")]
    public static partial void CursorAdvanceLost(this ILogger logger, Guid parentId);

    [LoggerMessage(EventId = 1812, Level = LogLevel.Warning,
        Message = "Halt marker of schedule {ParentId} was not written: another writer moved the schedule first")]
    public static partial void HaltNotPersisted(this ILogger logger, Guid parentId);

    [LoggerMessage(EventId = 1813, Level = LogLevel.Warning,
        Message = "Slot {SlotUtc:O} of schedule {ParentId} already has an occurrence: the cursor is carried " +
                  "past it instead of materializing it a second time")]
    public static partial void SlotAlreadyServed(this ILogger logger, Guid parentId, DateTimeOffset slotUtc);

    [LoggerMessage(EventId = 1814, Level = LogLevel.Error,
        Message = "Schedule {ParentId} could not be re-parked after a failed materialization: it is parked " +
                  "nowhere and will only come back at the next startup recovery")]
    public static partial void ReparkAfterFailureFailed(this ILogger logger, Exception exception, Guid parentId);

    [LoggerMessage(EventId = 1815, Level = LogLevel.Warning,
        Message = "Run of schedule {ParentId} stopped after walking past {WalkedSlots} slot(s) that already had " +
                  "an occurrence: the rest is left to the operational retry, which resumes from the cursor this " +
                  "run carried forward")]
    public static partial void ServedSlotWalkTruncated(this ILogger logger, Guid parentId, int walkedSlots);

    [LoggerMessage(EventId = 1816, Level = LogLevel.Warning,
        Message = "Schedule {ParentId} cannot be rebuilt from its row, so it materializes nothing: startup " +
                  "recovery owns the bounded retry and the terminal verdict on such a row, and this run only " +
                  "parks it for the operational retry")]
    public static partial void ScheduleRowUnusable(this ILogger logger, Exception exception, Guid parentId);

    [LoggerMessage(EventId = 1817, Level = LogLevel.Warning,
        Message = "Occurrence {OccurrenceId} of schedule {ParentId} could not be rebuilt right now, but a " +
                  "handler for it IS registered: it keeps its slot of the schedule's budget and is looked at " +
                  "again on the next run, instead of being ended for a failure that may not last")]
    public static partial void OccurrenceRebuildDeferred(this ILogger logger, Exception exception, Guid occurrenceId,
                                                         Guid parentId);

    [LoggerMessage(EventId = 1818, Level = LogLevel.Warning,
        Message = "Occurrence {OccurrenceId} of schedule {ParentId} is still {Status} after being marked " +
                  "Failed: the status write did not land, so its slot of the concurrency budget stays taken " +
                  "until a later run ends it for real")]
    public static partial void OccurrenceTerminalizationLost(this ILogger logger, Guid occurrenceId, Guid parentId,
                                                             QueuedTaskStatus status);

    [LoggerMessage(EventId = 1819, Level = LogLevel.Warning,
        Message = "Schedule {ParentId} dropped {SkippedCount} due slot(s) (exact count: {IsExact}) from " +
                  "{FromUtc:O}: the backlog exceeded the catch-up cap and SkipOldest keeps the most recent " +
                  "slots")]
    public static partial void OccurrencesDroppedByOverflow(this ILogger logger, Guid parentId, int skippedCount,
                                                            bool isExact, DateTimeOffset fromUtc);

    [LoggerMessage(EventId = 1820, Level = LogLevel.Warning,
        Message = "Schedule {ParentId} dropped {SkippedCount} due slot(s) (exact count: {IsExact}) from " +
                  "{FromUtc:O}: the skip policy does not replay a slot that is no longer the current one")]
    public static partial void OccurrencesSkippedByPolicy(this ILogger logger, Guid parentId, int skippedCount,
                                                          bool isExact, DateTimeOffset fromUtc);
}
