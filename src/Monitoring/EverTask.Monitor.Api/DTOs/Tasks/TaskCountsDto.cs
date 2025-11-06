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
);
