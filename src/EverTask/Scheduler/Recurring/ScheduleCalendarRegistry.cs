using System.Collections.Frozen;

namespace EverTask.Scheduler.Recurring;

internal sealed class ScheduleCalendarRegistry
{
    private readonly FrozenDictionary<string, ScheduleExclusions> _calendars;

    private ScheduleCalendarRegistry(IReadOnlyDictionary<string, ScheduleExclusions> calendars)
    {
        _calendars = calendars.ToFrozenDictionary(
            static entry => entry.Key,
            static entry => ScheduleExclusionNormalizer.Clone(entry.Value),
            StringComparer.Ordinal);
    }

    internal static ScheduleCalendarRegistry Create(IReadOnlyDictionary<string, ScheduleExclusions> calendars) =>
        new(calendars);

    internal ScheduleExclusions Resolve(ScheduleExclusions exclusions)
    {
        var inline = ScheduleExclusionNormalizer.Clone(exclusions);
        ScheduleExclusionNormalizer.Normalize(inline);

        var days = inline.Days.ToList();
        var dates = inline.Dates.ToList();
        var ranges = inline.Ranges.ToList();

        foreach (var name in inline.Calendars)
        {
            if (!_calendars.TryGetValue(name, out var calendar))
                throw UnknownCalendar(name);

            days.AddRange(calendar.Days);
            dates.AddRange(calendar.Dates);
            ranges.AddRange(calendar.Ranges.Select(static range => new ExclusionRange
            {
                FromUtc = range.FromUtc,
                ToUtc = range.ToUtc
            }));
        }

        var resolved = new ScheduleExclusions
        {
            Days = days.ToArray(),
            Dates = dates.ToArray(),
            Ranges = ranges.ToArray()
        };
        ScheduleExclusionNormalizer.Normalize(resolved);
        return resolved;
    }

    private ArgumentException UnknownCalendar(string name)
    {
        var registered = _calendars.Count == 0
            ? "none is registered"
            : $"the registered calendars are {string.Join(", ", _calendars.Keys)}";

        return new ArgumentException(
            $"No schedule exclusion calendar is registered under the name '{name}': {registered}. Register it " +
            $"with AddScheduleCalendar(\"{name}\", ...) in AddEverTask.", nameof(name));
    }
}
