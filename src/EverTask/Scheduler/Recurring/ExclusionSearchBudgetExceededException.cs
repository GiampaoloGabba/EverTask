namespace EverTask.Scheduler.Recurring;

internal sealed class ExclusionSearchBudgetExceededException(DateTimeOffset standingInstant)
    : Exception($"The recurring exclusion search exhausted its budget while standing at {standingInstant:O}.")
{
    internal DateTimeOffset StandingInstant { get; } = standingInstant;
}
