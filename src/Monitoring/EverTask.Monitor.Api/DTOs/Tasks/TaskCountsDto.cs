namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Task counts by category for dashboard badges.
/// </summary>
/// <param name="All">Total count of all tasks</param>
/// <param name="Standard">Count of standard (non-recurring) tasks</param>
/// <param name="Recurring">Count of recurring tasks</param>
/// <param name="Failed">Count of failed tasks</param>
public record TaskCountsDto(
    int All,
    int Standard,
    int Recurring,
    int Failed
)
{
    // An init property rather than a fifth positional parameter: the constructor and Deconstruct signatures of
    // a public record stay what they were.

    /// <summary>
    /// Count of materialized occurrences — the rows a durable schedule created, which are also counted in
    /// <see cref="Standard"/> because each of them is a one-shot task in its own right.
    /// </summary>
    public int Occurrences { get; init; }
}
