namespace EverTask.Scheduler.Recurring.Intervals;

public interface IInterval
{
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current);
}
