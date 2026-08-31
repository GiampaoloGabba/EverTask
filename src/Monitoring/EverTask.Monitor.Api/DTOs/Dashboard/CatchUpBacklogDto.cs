namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// The state of every materialized occurrence in the store, plus how far behind the oldest one that has not
/// run yet is.
/// </summary>
/// <remarks>
/// Slots a schedule DROPPED — outside the misfire window, over the overflow cap, or no longer current — never
/// became rows and are not counted here; they are reported by the <c>OccurrenceSkipped</c> monitoring event.
/// </remarks>
/// <param name="Pending">Occurrences that are materialized and waiting to start.</param>
/// <param name="Active">Occurrences running right now.</param>
/// <param name="Failed">Occurrences that ended Failed, after their retries.</param>
/// <param name="Skipped">Occurrences that will never run: cancelled on their own or with their schedule.</param>
/// <param name="Completed">Occurrences that ran to completion and are still in the store.</param>
/// <param name="OldestPendingSlotUtc">
/// The nominal slot of the oldest occurrence that has not started, or null when nothing is pending.
/// </param>
/// <param name="LagSeconds">
/// How far past its nominal slot the oldest pending occurrence already is, in seconds. Zero when nothing is
/// pending or when the oldest pending slot is still in the future.
/// </param>
/// <param name="HaltedSchedules">
/// Durable schedules whose catch-up has halted itself over the overflow cap. A halt does not release itself:
/// it stands until an operator resumes or reschedules the series.
/// </param>
public record CatchUpBacklogDto(
    int Pending,
    int Active,
    int Failed,
    int Skipped,
    int Completed,
    DateTimeOffset? OldestPendingSlotUtc,
    double LagSeconds,
    int HaltedSchedules
);
