namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Who is asking the grid: the schedule an evaluator call is about, in the terms an
/// <see cref="INextOccurrenceProvider"/> is given them.
/// </summary>
/// <param name="ScheduleId">
/// The schedule row, or <see cref="Guid.Empty"/> before it exists — the first occurrence of a brand-new
/// dispatch is computed before anything is persisted.
/// </param>
/// <param name="TaskKey">The dispatch key, when the schedule has one.</param>
/// <param name="RunNumber">
/// The 1-based number of the run being computed, or 0 when the question is not about a particular run (a
/// catch-up plan probing where a backlog begins).
/// </param>
/// <remarks>
/// The built-in grid ignores it entirely — an interval knows nothing about which schedule it belongs to — so
/// every call site that has no identity to give simply leaves it at its default. It is also what a provider
/// failure is attributed to: the backoff of a stalled schedule is counted per <see cref="ScheduleId"/>.
/// </remarks>
internal readonly record struct ScheduleIdentity(Guid ScheduleId, string? TaskKey, int RunNumber);
