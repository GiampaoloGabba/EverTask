namespace EverTask.Scheduler.Recurring;

/// <summary>
/// What a schedule's occurrence grid is anchored to, and therefore whether a time zone can govern it (T5).
/// </summary>
public enum ScheduleSemantics
{
    /// <summary>
    /// A constant step in elapsed time: <c>Every(n).Seconds/Minutes/Hours</c>, with the optional
    /// <c>AtMinute</c> / <c>AtSecond</c> alignments that only re-phase that step. The grid is the same set of
    /// instants in every zone, so a zone would change nothing: it is refused at validation instead of
    /// silently doing nothing. Across a DST fall-back these schedules fire in BOTH passes of the repeated
    /// hour, and inside a gap they are not compressed — 01:45 plus 30 minutes is 03:15 local.
    /// </summary>
    Elapsed = 0,

    /// <summary>
    /// Anchored to a wall clock or a calendar: a time of day, a day of the week, a day or month selector, a
    /// selected hour, or a cron expression. Day, week and month intervals are always calendar-anchored —
    /// they snap the result to a time of day, which only means something on some clock. These are the
    /// schedules a time zone governs.
    /// </summary>
    Calendar = 1
}
