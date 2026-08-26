namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// The circuit breaker of an overflowing catch-up, as it is written on the schedule row.
/// </summary>
/// <remarks>
/// A halt does not release itself — not by the backlog ageing out, not by a restart. Only
/// <c>ResumeSchedule</c> or a <c>Reschedule</c> clears it, which is why the dashboard shows it as a standing
/// condition instead of a past event.
/// </remarks>
/// <param name="AtUtc">When the halt was decided.</param>
/// <param name="Reason">Human-readable reason.</param>
/// <param name="DetectedAtLeast">The backlog that triggered the halt.</param>
/// <param name="IsExact">
/// Whether <paramref name="DetectedAtLeast"/> is the real total or only a lower bound.
/// </param>
/// <param name="CursorUtc">The cursor the halt was decided against.</param>
/// <param name="ScheduleVersion">The schedule version the halt was decided against.</param>
public record ScheduleHaltDto(
    DateTimeOffset AtUtc,
    string? Reason,
    int DetectedAtLeast,
    bool IsExact,
    DateTimeOffset? CursorUtc,
    int ScheduleVersion
);
