using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// The shape of a nightly batch job: it reconciles ONE business day, and which day that is comes from the
/// slot the occurrence stands for — never from <c>DateTime.Today</c>.
/// </summary>
/// <remarks>
/// The payload carries no date on purpose. A schedule replaying a backlog delivers several occurrences within
/// seconds of each other, so a handler that asked the clock which day it was would reconcile today's data
/// three times and leave the three missed days untouched.
/// </remarks>
public record NightlyReconciliationTask : IEverTask;

public class NightlyReconciliationHandler(ILogger<NightlyReconciliationHandler> logger)
    : EverTaskHandler<NightlyReconciliationTask>
{
    public override async Task Handle(NightlyReconciliationTask task, CancellationToken cancellationToken)
    {
        // ScheduledAtLocal is the slot on the schedule's OWN clock, offset included: for a 02:00 Europe/Rome
        // schedule it is 02:00 all year, while ScheduledAtUtc moves by an hour across daylight saving. The
        // business day to reconcile is therefore the local date of the slot, minus one.
        var slot        = Context.ScheduledAtLocal ?? Context.StartedAtUtc;
        var businessDay = DateOnly.FromDateTime(slot.Date).AddDays(-1);

        if (Context.Misfire is { } misfire)
        {
            logger.LogWarning(
                "Reconciling {BusinessDay} LATE ({Kind}, {Lateness} behind): this occurrence stands for " +
                "{MissedCount} missed slot(s), {From:yyyy-MM-dd} through {Through:yyyy-MM-dd}",
                businessDay, misfire.Kind, misfire.Lateness, misfire.MissedCount, misfire.MissedFromUtc,
                misfire.MissedThroughUtc);
        }
        else
        {
            logger.LogInformation("Reconciling {BusinessDay} (run {RunNumber} of the series, slot {Slot})",
                businessDay, Context.RunNumber, slot);
        }

        await Task.Delay(250, cancellationToken);

        logger.LogInformation("Reconciliation of {BusinessDay} completed", businessDay);
    }
}
