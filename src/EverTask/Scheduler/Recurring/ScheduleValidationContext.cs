using EverTask.Scheduler.Occurrences;

namespace EverTask.Scheduler.Recurring;

internal sealed record ScheduleValidationContext(
    OccurrenceProviderRegistry? Providers,
    ScheduleCalendarRegistry? Calendars);
