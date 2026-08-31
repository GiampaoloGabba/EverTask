namespace EverTask.Abstractions;

/// <summary>
/// What a runtime reschedule does with the slot the old definition was pointing at.
/// </summary>
public enum RescheduleMode
{
    /// <summary>
    /// The new cursor is the new definition's first occurrence after now. Anything the old definition still
    /// owed is dropped — on a durable schedule that backlog is reported as it goes.
    /// </summary>
    RecalculateFromNow = 0,

    /// <summary>
    /// The new cursor stays where the old one stood inside the calendar period it belonged to: the day, week
    /// or month the old cursor fell in is read on the OLD definition's clock, and the new cursor is the new
    /// definition's occurrence at that same POSITION in the period, read on the NEW one.
    /// </summary>
    /// <remarks>
    /// This is what preserves the logical date when only the time of day or the zone changes — moving a daily
    /// 09:00 job to 10:00, or a Rome schedule to Kiritimati, keeps it on the day it was already on instead of
    /// jumping to the next one.
    /// <para>
    /// The position matters as soon as a period holds more than one slot. A schedule at 09:00 and 15:00 whose
    /// cursor stands at the second one is rebased onto the second slot of the new definition, not onto its
    /// first: answering with the first would wind the cursor back onto an occurrence that has already run,
    /// replay it and spend one more of <c>MaxRuns</c>. A period that holds fewer slots than the position the
    /// cursor had reached is refused, exactly like a period with no slot at all.
    /// </para>
    /// <para>
    /// It is deliberately narrow: the two definitions must have the same shape (same cadence, same day/month
    /// selectors, same period kind), the period is never crossed, and a slot at or past the new
    /// <c>RunUntil</c> is refused rather than parked. Cron schedules expose no nominal period and are refused
    /// outright.
    /// </para>
    /// </remarks>
    RebaseFromCursor = 1
}
