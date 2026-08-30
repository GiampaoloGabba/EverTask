namespace EverTask.Scheduler.Recurring.Builder;

internal sealed class ExclusionBuilder : IExclusionBuilder
{
    private readonly HashSet<DayOfWeek> _days = [];
    private readonly HashSet<DateOnly> _dates = [];
    private readonly List<ExclusionRange> _ranges = [];
    private bool _configured;

    public IExclusionBuilder OnDays(params DayOfWeek[] days)
    {
        ArgumentNullException.ThrowIfNull(days);
        _configured |= days.Length > 0;
        _days.UnionWith(days);
        return this;
    }

    public IExclusionBuilder OnDates(params DateOnly[] dates)
    {
        ArgumentNullException.ThrowIfNull(dates);
        _configured |= dates.Length > 0;
        _dates.UnionWith(dates);
        return this;
    }

    public IExclusionBuilder Between(DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
            throw new ArgumentException("The exclusion window start must be earlier than its end.", nameof(from));

        _configured = true;
        _ranges.Add(new ExclusionRange { FromUtc = from.ToUniversalTime(), ToUtc = to.ToUniversalTime() });
        return this;
    }

    internal ScheduleExclusions Build(ScheduleExclusions? existing)
    {
        if (!_configured)
            throw new InvalidOperationException("Except added no exclusion.");

        return new ScheduleExclusions
        {
            Days = (existing?.Days ?? []).Concat(_days).Distinct().ToArray(),
            Dates = (existing?.Dates ?? []).Concat(_dates).Distinct().ToArray(),
            Ranges = (existing?.Ranges ?? []).Concat(_ranges).ToArray()
        };
    }
}
