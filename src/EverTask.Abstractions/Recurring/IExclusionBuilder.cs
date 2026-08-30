namespace EverTask.Abstractions;

/// <summary>Builds fixed moments that a recurring schedule's grid must not produce.</summary>
public interface IExclusionBuilder
{
    /// <summary>Excludes whole days of the week on the schedule's exclusion clock.</summary>
    /// <param name="days">The days to exclude. Repeated calls are additive.</param>
    /// <returns>The builder, for chaining.</returns>
    IExclusionBuilder OnDays(params DayOfWeek[] days);

    /// <summary>Excludes whole calendar dates on the schedule's exclusion clock.</summary>
    /// <param name="dates">The dates to exclude. Repeated calls are additive.</param>
    /// <returns>The builder, for chaining.</returns>
    IExclusionBuilder OnDates(params DateOnly[] dates);

    /// <summary>Excludes every instant in the half-open absolute window <c>[from, to)</c>.</summary>
    /// <param name="from">The inclusive start of the window.</param>
    /// <param name="to">The exclusive end of the window.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="from"/> is not earlier than <paramref name="to"/>.</exception>
    IExclusionBuilder Between(DateTimeOffset from, DateTimeOffset to);
}
