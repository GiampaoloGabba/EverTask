namespace EverTask.Scheduler.Recurring;

internal static class ScheduleExclusionNormalizer
{
    internal const int MaxEntries = 1000;
    internal const int MaxCalendars = 16;
    internal const int MaxCalendarNameLength = 100;

    internal static bool Normalize(ScheduleExclusions exclusions)
    {
        exclusions.Days ??= [];
        exclusions.Dates ??= [];
        exclusions.Ranges ??= [];
        exclusions.Calendars ??= [];

        if (exclusions.Days.Any(day => !Enum.IsDefined(day)))
            throw new ArgumentException(
                "An exclusion day is outside the defined DayOfWeek values.", nameof(RecurringTask.Exclusions));

        exclusions.Days = exclusions.Days.Distinct().Order().ToArray();
        exclusions.Dates = exclusions.Dates.Distinct().Order().ToArray();
        exclusions.Calendars = NormalizeCalendarNames(exclusions.Calendars);

        var ranges = new List<ExclusionRange>(exclusions.Ranges.Length);
        foreach (var range in exclusions.Ranges)
        {
            if (range is null)
                throw new ArgumentException("An exclusion range cannot be null.", nameof(RecurringTask.Exclusions));

            var normalized = new ExclusionRange
            {
                FromUtc = range.FromUtc.ToUniversalTime(),
                ToUtc = range.ToUtc.ToUniversalTime()
            };

            if (normalized.FromUtc >= normalized.ToUtc)
            {
                throw new ArgumentException(
                    "An exclusion range start must be earlier than its end.", nameof(RecurringTask.Exclusions));
            }

            ranges.Add(normalized);
        }

        ranges.Sort(static (left, right) =>
        {
            var fromComparison = left.FromUtc.CompareTo(right.FromUtc);
            return fromComparison != 0 ? fromComparison : left.ToUtc.CompareTo(right.ToUtc);
        });

        var merged = new List<ExclusionRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.FromUtc > merged[^1].ToUtc)
            {
                merged.Add(range);
                continue;
            }

            if (range.ToUtc > merged[^1].ToUtc)
                merged[^1].ToUtc = range.ToUtc;
        }

        exclusions.Ranges = merged.ToArray();

        if (exclusions.Dates.Length + exclusions.Ranges.Length > MaxEntries)
        {
            throw new InvalidOperationException(
                $"A schedule may carry at most {MaxEntries} exclusion dates and windows after normalization.");
        }

        if (exclusions.Days.Length == 7)
            throw new InvalidOperationException("Excluding every day of the week leaves no recurring occurrence.");

        return exclusions.Days.Length == 0
            && exclusions.Dates.Length == 0
            && exclusions.Ranges.Length == 0
            && exclusions.Calendars.Length == 0;
    }

    internal static ScheduleExclusions Clone(ScheduleExclusions source) => new()
    {
        Days = (source.Days ?? []).ToArray(),
        Dates = (source.Dates ?? []).ToArray(),
        Ranges = (source.Ranges ?? []).Select(static range => range is null
            ? null!
            : new ExclusionRange { FromUtc = range.FromUtc, ToUtc = range.ToUtc }).ToArray(),
        Calendars = (source.Calendars ?? []).ToArray()
    };

    private static string[] NormalizeCalendarNames(string[] names)
    {
        var normalized = new string[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            if (names[index] is null)
                throw new ArgumentException(
                    "An exclusion calendar name cannot be null.", nameof(RecurringTask.Exclusions));

            var name = names[index].Trim();
            if (name.Length == 0)
                throw new ArgumentException(
                    "An exclusion calendar name cannot be empty.", nameof(RecurringTask.Exclusions));
            if (name.Length > MaxCalendarNameLength)
            {
                throw new ArgumentException(
                    $"An exclusion calendar name cannot exceed {MaxCalendarNameLength} characters.",
                    nameof(RecurringTask.Exclusions));
            }

            normalized[index] = name;
        }

        normalized = normalized.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length > MaxCalendars)
        {
            throw new InvalidOperationException(
                $"A schedule may reference at most {MaxCalendars} exclusion calendars after normalization.");
        }

        return normalized;
    }
}
