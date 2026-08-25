namespace EverTask.Scheduler.Recurring;

/// <summary>
/// The calendar unit one occurrence of a schedule belongs to (M18): what a cursor has to stay inside when a
/// reschedule rebases it.
/// </summary>
/// <remarks>
/// Explicit rather than inferred at the rebase site, because inferring it is exactly the bug M18 exists to
/// prevent: reading the old cursor literally on a new definition loses the logical day the moment the zone or
/// the time of day changes, and reading it "approximately" crosses into the next period.
/// </remarks>
internal enum SchedulePeriodKind
{
    /// <summary>No nominal period at all: a cron expression, whose period nothing can state reliably.</summary>
    None = 0,

    /// <summary>
    /// The instant itself. A plain cadence has no calendar structure to preserve — the same set of instants in
    /// every zone — so the cursor IS the period.
    /// </summary>
    Instant = 1,

    /// <summary>A calendar day, on the schedule's own clock.</summary>
    Day = 2,

    /// <summary>A calendar week, Sunday through Saturday, matching the day-of-week walk.</summary>
    Week = 3,

    /// <summary>A calendar month.</summary>
    Month = 4
}
