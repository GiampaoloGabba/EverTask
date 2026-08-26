namespace EverTask.Scheduler.Recurring;

/// <summary>
/// What a next-run calculation decided before it asked the grid: either the answer itself, or which instant
/// the grid has to be asked about and what the first-run configuration would have preferred.
/// </summary>
/// <param name="IsFinal">True when <paramref name="Answer"/> is the whole answer and the grid is not asked.</param>
/// <param name="Answer">The next run, when the bounds or an <c>InitialDelay</c> already decided it.</param>
/// <param name="BaseTime">The instant the grid is asked for the occurrence strictly after.</param>
/// <param name="Runtime">
/// The first-run instant a <c>RunNow</c> or <c>RunAt</c> asked for, when this is the first run and it may
/// still win over the grid's slot.
/// </param>
/// <param name="Current">The instant the calculation started from, in UTC.</param>
/// <param name="CurrentRun">Runs already spent, which is what makes a first run a first run.</param>
/// <remarks>
/// It exists so the two grids — the built-in arithmetic and an <see cref="INextOccurrenceProvider"/>, which
/// answers asynchronously — share one copy of the rules around the grid step instead of one each.
/// </remarks>
internal readonly record struct NextRunPlan(
    bool IsFinal,
    DateTimeOffset? Answer,
    DateTimeOffset BaseTime,
    DateTimeOffset? Runtime,
    DateTimeOffset Current,
    int CurrentRun)
{
    /// <summary>A calculation that is over before the grid is asked.</summary>
    internal static NextRunPlan Final(DateTimeOffset? answer) =>
        new(true, answer, default, null, default, 0);
}
